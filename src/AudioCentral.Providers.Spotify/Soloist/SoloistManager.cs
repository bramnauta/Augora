using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AudioCentral.Contracts;
using AudioCentral.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioCentral.Providers.Spotify.Soloist;

/// <summary>
/// Plays Spotify through Spotify Soloist, Spotify's own headless client. Soloist only outputs to PipeWire or
/// PulseAudio, so a private PulseAudio daemon gets a null sink per account and ffmpeg records its monitor.
/// </summary>
/// <remarks>
/// Spotify does not allow redistributing Soloist: it is downloaded from Spotify's CDN after the user's consent and
/// replaced before a build expires (90 days). One track per process (<c>--single-track</c>), so the process ending is
/// the end of the track.
/// </remarks>
internal sealed partial class SoloistManager(
    IHttpClientFactory httpClientFactory,
    IOptions<SpotifyOptions> options,
    IOptions<AudioCentralOptions> coreOptions,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    public const string DownloadClientName = "soloist-downloads";
    private const string DownloadBaseUrl = "https://soloist-builds.spotifycdn.com/";
    private const int ExitBuildExpired = 10;
    private static readonly TimeSpan BuildLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(7);
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(1.5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<SoloistManager>();
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly SemaphoreSlim _audioLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Process> _pairing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SoloistDaemon> _daemons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _daemonLocks = new(StringComparer.OrdinalIgnoreCase);
    private Process? _pulse;
    private (TimeSpan Duration, byte[] Data)? _preroll;

    public static bool IsSupported => OperatingSystem.IsLinux();

    private string Root => Path.Combine(Path.GetFullPath(coreOptions.Value.DataDirectory, environment.ContentRootPath), "soloist");

    private string BinaryPath => Path.Combine(Root, "bin", "soloist");

    private string PulseDirectory => Path.Combine(Root, "pulse");

    private string PulseSocket => Path.Combine(PulseDirectory, "native");

    public static string DeviceName(SpotifyAccount account) => $"AudioCentral ({account.DisplayName})";

    /// <summary>Paired, or waiting for the user to select the device in the Spotify app (pairing runs in the background).</summary>
    public async Task<SpotifyPairingDto> GetPairingAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        var key = RequireApiKey(account);
        if (File.Exists(PairedMarker(account)))
        {
            return new SpotifyPairingDto(account.Id, account.DisplayName, true, null, null, null, SpotifyPairingMethod.Connect, DeviceName(account));
        }

        await EnsureInstalledAsync(cancellationToken);
        if (!_pairing.TryGetValue(account.Id, out var running) || running.HasExited)
        {
            var directory = AccountDirectory(account);
            Directory.CreateDirectory(directory);
            var process = Start(BinaryPath, ["-n", DeviceName(account), "-k", key, "-D", directory, "--pair"], account.Id, environment: null);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                // Forget() removes the entry before stopping the process, and Soloist exits with 0 on a stop as well.
                if (!_pairing.TryRemove(new KeyValuePair<string, Process>(account.Id, process)))
                {
                    return;
                }

                if (process.ExitCode == 0)
                {
                    File.WriteAllText(PairedMarker(account), DateTimeOffset.UtcNow.ToString("O"));
                    _logger.LogInformation("Soloist paired for Spotify account {Account}", account.Id);
                }
                else
                {
                    _logger.LogWarning("Soloist pairing for {Account} ended with exit code {Code}", account.Id, process.ExitCode);
                }
            };
            _pairing[account.Id] = process;
            _logger.LogInformation("Soloist waits to be selected as {Device} in the Spotify app", DeviceName(account));
        }

        return new SpotifyPairingDto(account.Id, account.DisplayName, false, null, null, null, SpotifyPairingMethod.Connect, DeviceName(account));
    }

    public bool IsPaired(SpotifyAccount account) => File.Exists(PairedMarker(account));

    /// <summary>
    /// Plays one track on the account's Soloist daemon and writes it, encoded, to <paramref name="output"/>.
    /// Returns when the track ended; Soloist is paused then, so it does not continue with autoplay.
    /// </summary>
    public async Task StreamTrackAsync(SpotifyAccount account, string trackId, TimeSpan? duration, Stream output, CancellationToken cancellationToken)
    {
        var sink = SinkName(account);
        var daemon = await GetDaemonAsync(account, cancellationToken);
        var uri = $"spotify:track:{trackId}";

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<SoloistEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = false;
        var playing = false;
        void OnEvent(object? sender, SoloistEvent e)
        {
            switch (e.Type)
            {
                case "track_changed" or "playback_state" when e.Uri is not null:
                    if (e.Uri == uri)
                    {
                        current = true;
                    }
                    else if (current)
                    {
                        // Autoplay moved on to another track: ours is over.
                        ended.TrySetResult(SoloistEnd.NextTrack);
                    }

                    if (e.Type == "playback_state")
                    {
                        goto case "playback_changed";
                    }

                    break;
                case "playback_changed":
                    if (e.Status == "playing" && current)
                    {
                        playing = true;
                        started.TrySetResult();
                    }
                    else if (e.Status is "idle" or "paused" && playing)
                    {
                        ended.TrySetResult(SoloistEnd.Stopped);
                    }

                    break;
                case "error" when !playing:
                    ended.TrySetException(new InvalidOperationException($"Soloist could not play {uri}: {e.Message}"));
                    break;
            }
        }

        var pulse = new Dictionary<string, string> { ["PULSE_SERVER"] = $"unix:{PulseSocket}" };
        var preroll = await GetPrerollAsync(cancellationToken);
        await output.WriteAsync(preroll, cancellationToken);
        using var recorder = Start(options.Value.FfmpegPath, RecorderArguments(sink), account.Id, pulse, redirectInput: true, captureOutput: false);
        var copy = recorder.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);

        daemon.EventReceived += OnEvent;
        var elapsed = Stopwatch.StartNew();
        var end = SoloistEnd.Stopped;
        try
        {
            await daemon.SendAsync("set_volume", new Dictionary<string, object?> { ["volume"] = 100 }, cancellationToken);
            await daemon.SendAsync("play", new Dictionary<string, object?> { ["uri"] = uri }, cancellationToken);

            if (await Task.WhenAny(started.Task, ended.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)) != started.Task)
            {
                if (ended.Task.IsFaulted)
                {
                    await ended.Task;
                }

                throw new InvalidOperationException($"Soloist did not start {uri}: {Clean(daemon.LastError)}");
            }

            _logger.LogInformation("Soloist plays {Uri} after {Milliseconds} ms", uri, elapsed.ElapsedMilliseconds);

            // The end comes from the events; the duration is a safety net when an event is missed.
            var limit = (duration ?? TimeSpan.FromMinutes(30)) + TimeSpan.FromSeconds(30);
            if (await Task.WhenAny(ended.Task, Task.Delay(limit, cancellationToken)) == ended.Task)
            {
                end = await ended.Task;
            }

            if (end != SoloistEnd.NextTrack)
            {
                // The null sink plays in real time: let the last audio arrive.
                await Task.Delay(DrainTime, cancellationToken);
            }

            await PauseQuietlyAsync(daemon);
            await recorder.StandardInput.WriteAsync("q");
            await recorder.StandardInput.FlushAsync(cancellationToken);
            await copy.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The player went away (stop, skip, other source): stop Soloist so the account is not streaming for nothing.
            await PauseQuietlyAsync(daemon);
            throw;
        }
        finally
        {
            daemon.EventReceived -= OnEvent;
            Kill(recorder);
        }
    }

    /// <summary>
    /// ffmpeg recording the account's sink as bare MP3 frames (no ID3 or Xing header), so they can follow
    /// <see cref="GetPrerollAsync"/>. The monitor only delivers audio while something plays, so the recording starts
    /// with the track. No silenceremove: in ffmpeg 6 it holds back all audio. Small capture fragments and per-packet
    /// flushing keep ffmpeg's own delay around half a second.
    /// </summary>
    internal static List<string> RecorderArguments(string sink) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-fragment_size", "1024", "-f", "pulse", "-i", $"{sink}.monitor",
        "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "320k",
        "-id3v2_version", "0", "-write_xing", "0", "-flush_packets", "1", "-f", "mp3", "pipe:1",
    ];

    /// <summary>
    /// Silence in the same MP3 format as the recording, sent at once before it. Soloist plays in real time while Cast
    /// devices wait for a few seconds of audio before they start: the burst fills that buffer immediately, so the
    /// music starts that much sooner. Encoded once and cached.
    /// </summary>
    private async Task<byte[]> GetPrerollAsync(CancellationToken cancellationToken)
    {
        var preroll = options.Value.SoloistPreroll;
        if (preroll <= TimeSpan.Zero)
        {
            return [];
        }

        if (_preroll is { } cached && cached.Duration == preroll)
        {
            return cached.Data;
        }

        var seconds = preroll.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var start = new ProcessStartInfo(options.Value.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"anullsrc=r=44100:cl=stereo:d={seconds}",
            "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "0", "-write_xing", "0", "-f", "mp3", "pipe:1",
        ])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        using var silence = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(silence, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _preroll = (preroll, silence.ToArray());
        return _preroll.Value.Data;
    }

    private async Task<SoloistDaemon> GetDaemonAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        var key = RequireApiKey(account);
        var gate = _daemonLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_daemons.TryGetValue(account.Id, out var existing))
            {
                if (existing.IsRunning)
                {
                    return existing;
                }

                _daemons.TryRemove(account.Id, out _);
                if (existing.ExitCode == ExitBuildExpired)
                {
                    File.Delete(BinaryPath);
                }

                _logger.LogWarning("Soloist for {Account} stopped (exit {Code}): {Error}; restarting it", account.Id, existing.ExitCode, Clean(existing.LastError));
                await existing.DisposeAsync();
            }

            // A pairing process would hold the data directory.
            StopPairing(account);
            await EnsureInstalledAsync(cancellationToken);
            var sink = SinkName(account);
            await EnsureSinkAsync(sink, cancellationToken);
            var pulse = new Dictionary<string, string> { ["PULSE_SERVER"] = $"unix:{PulseSocket}", ["PULSE_SINK"] = sink };
            var directory = AccountDirectory(account);
            var (daemon, loggedIn) = await SoloistDaemon.StartAsync(
                (arguments, onLine) => Start(BinaryPath, ["-n", DeviceName(account), "-k", key, "-D", directory, "-i", "100", .. arguments], account.Id, pulse, onLine: onLine),
                directory,
                _logger,
                cancellationToken);
            if (!loggedIn)
            {
                await daemon.DisposeAsync();
                File.Delete(PairedMarker(account));
                throw new InvalidOperationException($"Soloist has no valid session for Spotify account '{account.DisplayName}'; pair it again.");
            }

            _daemons[account.Id] = daemon;
            return daemon;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PauseQuietlyAsync(SoloistDaemon daemon)
    {
        try
        {
            await daemon.SendAsync("pause", null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Pausing Soloist failed");
        }
    }

    private enum SoloistEnd
    {
        Stopped,
        NextTrack,
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _pairing.Values)
        {
            Kill(process);
        }

        foreach (var daemon in _daemons.Values)
        {
            await daemon.DisposeAsync();
        }

        if (_pulse is not null)
        {
            Kill(_pulse);
            _pulse.Dispose();
        }

        _installLock.Dispose();
        _audioLock.Dispose();
    }

    /// <summary>Stops the account's pairing and daemon, e.g. when it switches engine or is unlinked.</summary>
    public void Forget(SpotifyAccount account)
    {
        StopPairing(account);
        if (_daemons.TryRemove(account.Id, out var daemon))
        {
            _ = daemon.DisposeAsync().AsTask();
        }
    }

    private void StopPairing(SpotifyAccount account)
    {
        if (_pairing.TryRemove(account.Id, out var process))
        {
            Kill(process);
        }
    }

    /// <summary>Downloads Soloist from Spotify when missing or about to expire.</summary>
    private async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(BinaryPath) && await GetBuildDateAsync(cancellationToken) is { } built
                && DateTimeOffset.UtcNow < built + BuildLifetime - RenewBefore)
            {
                return;
            }

            var archive = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "soloist_release_x86_64.tar.gz",
                Architecture.Arm64 => "soloist_release_arm64.tar.gz",
                Architecture.Arm => "soloist_release_arm32.tar.gz",
                var other => throw new InvalidOperationException($"Soloist is not available for {other}."),
            };

            _logger.LogInformation("Downloading Spotify Soloist ({Archive}) from Spotify", archive);
            Directory.CreateDirectory(Path.GetDirectoryName(BinaryPath)!);
            var temp = BinaryPath + ".download";
            await using (var response = await httpClientFactory.CreateClient(DownloadClientName).GetStreamAsync(DownloadBaseUrl + archive, cancellationToken))
            await using (var gzip = new GZipStream(response, CompressionMode.Decompress))
            {
                await using var tar = new TarReader(gzip);
                var found = false;
                while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
                {
                    if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == "soloist")
                    {
                        await entry.ExtractToFileAsync(temp, overwrite: true, cancellationToken);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException("The Soloist archive does not contain the soloist executable.");
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            File.Move(temp, BinaryPath, overwrite: true);
            _logger.LogInformation("Installed Spotify Soloist, build date {Date:d}", await GetBuildDateAsync(cancellationToken));
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task<DateTimeOffset?> GetBuildDateAsync(CancellationToken cancellationToken)
    {
        var (exit, output) = await RunAsync(BinaryPath, ["--version"], null, cancellationToken);
        return exit == 0 ? ParseBuildDate(output) : null;
    }

    /// <summary>The build date in <c>soloist 1.3.10.28 build 1793296881 (20261029) (g19412df6ea) (linux/x86_64)</c>.</summary>
    internal static DateTimeOffset? ParseBuildDate(string version) =>
        BuildDatePattern().Match(version) is { Success: true } match
        && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero)
            : null;

    /// <summary>Starts the private PulseAudio daemon when needed and adds the account's null sink.</summary>
    private async Task EnsureSinkAsync(string sink, CancellationToken cancellationToken)
    {
        await _audioLock.WaitAsync(cancellationToken);
        try
        {
            if (_pulse is null || _pulse.HasExited)
            {
                var home = Path.Combine(PulseDirectory, "home");
                Directory.CreateDirectory(home);
                if (File.Exists(PulseSocket))
                {
                    File.Delete(PulseSocket);
                }

                _pulse = Start("pulseaudio",
                [
                    "-n", "--daemonize=no", "--exit-idle-time=-1", "--use-pid-file=no", "--log-target=stderr",
                    $"--load=module-native-protocol-unix socket={PulseSocket} auth-anonymous=1",
                ], "pulseaudio", new Dictionary<string, string> { ["HOME"] = home, ["XDG_RUNTIME_DIR"] = home });

                var waited = Stopwatch.StartNew();
                while (!File.Exists(PulseSocket))
                {
                    if (_pulse.HasExited || waited.Elapsed > TimeSpan.FromSeconds(10))
                    {
                        throw new InvalidOperationException("The PulseAudio daemon for Soloist did not start.");
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }

            var server = new Dictionary<string, string> { ["PULSE_SERVER"] = $"unix:{PulseSocket}" };
            var (_, sinks) = await RunAsync("pactl", ["list", "short", "sinks"], server, cancellationToken);
            if (!sinks.Split('\n').Any(l => l.Split('\t') is [_, var name, ..] && name == sink))
            {
                var (exit, output) = await RunAsync("pactl",
                    ["load-module", "module-null-sink", $"sink_name={sink}", "rate=44100", "channels=2", "format=s16le"], server, cancellationToken);
                if (exit != 0)
                {
                    throw new InvalidOperationException($"Creating the audio sink for Soloist failed: {output.Trim()}");
                }
            }
        }
        finally
        {
            _audioLock.Release();
        }
    }

    private string AccountDirectory(SpotifyAccount account) => Path.Combine(Root, "accounts", account.Id);

    private string PairedMarker(SpotifyAccount account) => Path.Combine(AccountDirectory(account), "audiocentral-paired");

    internal static string SinkName(SpotifyAccount account) =>
        "ac_" + new string(account.Id.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    private static string RequireApiKey(SpotifyAccount account) =>
        account.SoloistApiKey is { Length: > 0 } key
            ? key
            : throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured, $"Spotify account '{account.DisplayName}' has no Soloist API key.", 409);

    private Process Start(
        string fileName,
        IEnumerable<string> arguments,
        string logName,
        IReadOnlyDictionary<string, string>? environment,
        bool redirectInput = false,
        bool captureOutput = true,
        Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{fileName} was not found; Soloist needs the AudioCentral container image.", ex);
        }

        void Log(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            onLine?.Invoke(line);
            _logger.LogDebug("{Process} {Name}: {Line}", Path.GetFileName(fileName), logName, line);
        }

        process.ErrorDataReceived += (_, e) => Log(e.Data);
        process.BeginErrorReadLine();
        if (captureOutput)
        {
            process.OutputDataReceived += (_, e) => Log(e.Data);
            process.BeginOutputReadLine();
        }

        return process;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output + await error);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>Soloist log lines carry colour codes and a source tag; keep the message.</summary>
    private static string Clean(string? line) =>
        line is null ? "see the log" : LogPrefixPattern().Replace(AnsiPattern().Replace(line, ""), "").Trim();

    [GeneratedRegex(@"\((\d{8})\)")]
    private static partial Regex BuildDatePattern();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"^\S+\s+[A-Z]\s+\[[^\]]*\]\s*")]
    private static partial Regex LogPrefixPattern();
}
