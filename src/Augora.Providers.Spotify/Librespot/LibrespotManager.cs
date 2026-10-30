using System.Collections.Concurrent;
using System.Diagnostics;
using Augora.Contracts;
using Augora.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Augora.Providers.Spotify.Librespot;

/// <summary>
/// Starts a go-librespot daemon per account on first use and turns its PCM output into an encoded stream per track.
/// </summary>
internal sealed class LibrespotManager(
    IOptions<SpotifyOptions> options,
    IOptions<AugoraOptions> coreOptions,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    // s16le, 44.1 kHz, stereo.
    private const int BytesPerSecond = 44_100 * 2 * 2;
    private static readonly TimeSpan EndIdle = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan StallIdle = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, LibrespotDaemon> _daemons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggerFactory.CreateLogger<LibrespotManager>();
    private readonly SpotifyOptions _options = options.Value;

    /// <summary>Tracks are encoded as 320 kbps MP3: every Cast device and browser plays it, and it is transparent.</summary>
    public const string ContentType = "audio/mpeg";

    /// <summary>
    /// Constant 320 kbit/s without headers: a byte offset is a time offset, so players can seek with byte ranges.
    /// </summary>
    public const int EncodedBytesPerSecond = 320_000 / 8;

    public async Task<LibrespotDaemon> GetDaemonAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_daemons.TryGetValue(account.Id, out var existing))
            {
                if (existing.IsRunning)
                {
                    return existing;
                }

                _logger.LogWarning("go-librespot for {Account} stopped; restarting it", account.Id);
                _daemons.TryRemove(account.Id, out _);
                await existing.DisposeAsync();
            }

            var directory = Path.Combine(
                Path.GetFullPath(coreOptions.Value.DataDirectory, environment.ContentRootPath), "spotify", account.Id);
            var daemon = new LibrespotDaemon(account, directory, _options, loggerFactory.CreateLogger<LibrespotDaemon>());
            try
            {
                await daemon.StartAsync(cancellationToken);
            }
            catch
            {
                await daemon.DisposeAsync();
                throw;
            }

            _daemons[account.Id] = daemon;
            return daemon;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The playback login of <paramref name="account"/>: paired, or the code to approve. A login with another Spotify
    /// account than the linked one is discarded, so the next attempt pairs again.
    /// </summary>
    public async Task<SpotifyPairingDto> GetPairingAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        var daemon = await GetDaemonAsync(account, cancellationToken);
        var login = await daemon.GetLoginAsync(TimeSpan.FromSeconds(15), cancellationToken)
            ?? throw new InvalidOperationException("go-librespot neither logged in nor offered a pairing code in time.");

        if (login.Username is { } username)
        {
            if (!username.Equals(account.Id, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("go-librespot for {Account} logged in as {Username}; discarding that login", account.Id, username);
                await RemoveAsync(account.Id);
                daemon.DeleteStoredLogin();
                throw new AugoraException(ErrorCodes.SpotifyAuthFailed,
                    $"Playback was approved with Spotify account '{username}' instead of '{account.Id}'. Try again with the right account.", 409);
            }

            return new SpotifyPairingDto(account.Id, account.DisplayName, true, null, null, null);
        }

        var pairing = login.Pairing!;
        return new SpotifyPairingDto(account.Id, account.DisplayName, false, pairing.Url, pairing.Code, pairing.ExpiresAt);
    }

    /// <summary>A logged-in daemon, or <see cref="ErrorCodes.SpotifyPairingRequired"/> with the code to approve.</summary>
    public async Task<LibrespotDaemon> GetLoggedInDaemonAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        var pairing = await GetPairingAsync(account, cancellationToken);
        if (!pairing.Paired)
        {
            var error = new AugoraException(ErrorCodes.SpotifyPairingRequired,
                $"Approve playback for Spotify account '{account.DisplayName}' once at {pairing.Url} (code {pairing.Code}).", 409);
            error.Extensions["pairing"] = pairing;
            throw error;
        }

        return await GetDaemonAsync(account, cancellationToken);
    }

    public async Task RemoveAsync(string accountId)
    {
        if (_daemons.TryRemove(accountId, out var daemon))
        {
            await daemon.DisposeAsync();
        }
    }

    /// <summary>
    /// Plays one track on the daemon from <paramref name="start"/> and writes it, encoded, to <paramref name="output"/>.
    /// Returns when the track ended.
    /// </summary>
    public async Task StreamTrackAsync(LibrespotDaemon daemon, string trackId, TimeSpan? duration, TimeSpan start, Stream output, CancellationToken cancellationToken)
    {
        var uri = $"spotify:track:{trackId}";
        var ended = new TaskCompletionSource<LibrespotEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(object? sender, LibrespotEvent e)
        {
            if (e.Type == "stopped" || (e.Type is "not_playing" or "playback_error" && (e.Uri is null || e.Uri == uri)))
            {
                ended.TrySetResult(e);
            }
        }

        var reader = daemon.Pipe.Attach();
        daemon.EventReceived += OnEvent;
        using var encoder = StartEncoder();
        var copy = encoder.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        try
        {
            await daemon.PlayAsync(uri, start, cancellationToken);
            var input = encoder.StandardInput.BaseStream;
            var expected = duration is { } d ? (long)((d - start).TotalSeconds * BytesPerSecond * 0.99) : long.MaxValue;
            long received = 0;
            while (true)
            {
                var finishing = ended.Task.IsCompleted || received >= expected;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(finishing ? EndIdle : StallIdle);
                // An end or error event must not wait for the stall timeout: a refused track has no audio at all.
                using var onEnded = finishing ? default : ended.Task.ContinueWith(_ => CancelQuietly(idle), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                try
                {
                    if (!await reader.WaitToReadAsync(idle.Token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!finishing && ended.Task.IsCompleted)
                    {
                        if (received == 0 && ended.Task.Result.Type == "playback_error")
                        {
                            break;
                        }

                        // Drain what is still in the pipe with the short idle timeout.
                        continue;
                    }

                    if (!finishing)
                    {
                        _logger.LogWarning("No audio from go-librespot for {Uri} for {Seconds}s; ending the track", uri, StallIdle.TotalSeconds);
                    }

                    break;
                }

                while (reader.TryRead(out var chunk))
                {
                    await input.WriteAsync(chunk, cancellationToken);
                    received += chunk.Length;
                }
            }

            if (received == 0 && ended.Task.IsCompleted && ended.Task.Result is { Type: "playback_error" } error)
            {
                throw new InvalidOperationException($"Spotify could not play {uri}: {error.Message}");
            }

            input.Close();
            await copy;
            _logger.LogInformation("Streamed {Uri}: {Seconds:F0}s of audio", uri, received / (double)BytesPerSecond);
        }
        finally
        {
            daemon.EventReceived -= OnEvent;
            if (daemon.Pipe.IsAttached(reader))
            {
                daemon.Pipe.Detach(reader);
                if (cancellationToken.IsCancellationRequested)
                {
                    // The player went away (stop, skip, other source): stop librespot so it does not keep pulling.
                    await daemon.StopAsync();
                }
            }

            if (!encoder.HasExited)
            {
                encoder.Kill();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in _daemons.Values)
        {
            await daemon.DisposeAsync();
        }

        _daemons.Clear();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait already finished.
        }
    }

    private Process StartEncoder()
    {
        var start = new ProcessStartInfo(_options.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "s16le", "-ar", "44100", "-ac", "2", "-i", "pipe:0",
            "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "0", "-write_xing", "0", "-f", "mp3", "pipe:1",
        ])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _logger.LogWarning("ffmpeg: {Line}", e.Data);
                }
            };
            process.BeginErrorReadLine();
            return process;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"ffmpeg was not found at '{_options.FfmpegPath}'. Install it or set Providers:Spotify:FfmpegPath.", ex);
        }
    }
}
