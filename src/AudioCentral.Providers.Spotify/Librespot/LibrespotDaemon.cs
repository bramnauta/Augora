using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Providers.Spotify.Librespot;

/// <summary>An event from the go-librespot websocket (<c>playing</c>, <c>not_playing</c>, <c>stopped</c>, ...).</summary>
internal sealed record LibrespotEvent(string Type, string? Uri, string? Message);

/// <summary>
/// One go-librespot process for one Spotify account, controlled through its local HTTP API. Its audio goes to a
/// <see cref="PcmPipe"/>; the events tell when a track has ended.
/// </summary>
internal sealed class LibrespotDaemon : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly SpotifyAccount _account;
    private readonly string _directory;
    private readonly SpotifyOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Process? _process;
    private int _port;
    private volatile string? _lastError;

    public LibrespotDaemon(SpotifyAccount account, string directory, SpotifyOptions options, ILogger logger)
    {
        _account = account;
        _directory = directory;
        _options = options;
        _logger = logger;
        Pipe = PcmPipe.Create(directory, $"spotify-{account.Id}", logger);
    }

    public PcmPipe Pipe { get; }

    public string AccountId => _account.Id;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Raised for every websocket event.</summary>
    public event EventHandler<LibrespotEvent>? EventReceived;

    public async Task StartAsync(string accessToken, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        _port = GetFreePort();
        await File.WriteAllTextAsync(Path.Combine(_directory, "config.yml"), BuildConfig(accessToken), cancellationToken);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_port}/");

        Pipe.Start();
        var start = new ProcessStartInfo(_options.LibrespotPath, ["--config_dir", _directory])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            _process = Process.Start(start) ?? throw new InvalidOperationException("go-librespot did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"go-librespot was not found at '{_options.LibrespotPath}'. Install it or set Providers:Spotify:LibrespotPath.", ex);
        }

        _process.OutputDataReceived += (_, e) => Log(e.Data);
        _process.ErrorDataReceived += (_, e) => Log(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReadyAsync(cancellationToken);
        _ = Task.Run(() => ListenAsync(_stopping.Token), CancellationToken.None);
        _logger.LogInformation("go-librespot for Spotify account {Account} listens on port {Port}", _account.Id, _port);
    }

    public async Task PlayAsync(string spotifyUri, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("player/play", new PlayBody(spotifyUri), LibrespotJsonContext.Default.PlayBody, cancellationToken);
        }
        catch (HttpRequestException ex) when (!IsRunning)
        {
            // The API listens before the Spotify login; a failed login ends the process shortly after.
            throw new InvalidOperationException($"go-librespot stopped: {_lastError ?? "unknown error"}. Link the Spotify account again if the login was refused.", ex);
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"go-librespot refused to play {spotifyUri}: {(int)response.StatusCode} {body}");
        }
    }

    public async Task StopAsync()
    {
        try
        {
            using var response = await _http.PostAsync("player/stop", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Stopping go-librespot failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        Pipe.Dispose();
        _http.Dispose();
        _stopping.Dispose();
    }

    internal string BuildConfig(string accessToken) => $"""
        log_level: info
        device_name: {Yaml($"AudioCentral ({_account.DisplayName})")}
        device_type: speaker
        zeroconf_enabled: false
        audio_backend: pipe
        audio_output_pipe: {Yaml(Pipe.Path)}
        audio_output_pipe_format: s16le
        audio_output_pipe_wait_for_reader: true
        bitrate: {_options.Bitrate}
        external_volume: true
        volume_steps: 100
        initial_volume: 100
        disable_autoplay: true
        server:
          enabled: true
          address: 127.0.0.1
          port: {_port}
        credentials:
          type: spotify_token
          spotify_token:
            username: {Yaml(_account.Id)}
            access_token: {Yaml(accessToken)}
        """;

    /// <summary>
    /// Waits until the API answers and the Spotify login succeeded (the status names the user). The API listens before
    /// the login, which go-librespot retries for a few seconds before it exits.
    /// </summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        TimeSpan? listeningSince = null;
        while (elapsed.Elapsed < StartTimeout)
        {
            if (_process!.HasExited)
            {
                throw new InvalidOperationException($"go-librespot exited with code {_process.ExitCode}: {_lastError ?? "see the log"}.");
            }

            try
            {
                using var response = await _http.GetAsync("status", cancellationToken);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
                {
                    listeningSince ??= elapsed.Elapsed;
                    if (response.StatusCode == HttpStatusCode.OK && HasUsername(await response.Content.ReadAsByteArrayAsync(cancellationToken)))
                    {
                        return;
                    }

                    // Without an active session the status is empty; give the login time to fail before assuming it worked.
                    if (elapsed.Elapsed - listeningSince > TimeSpan.FromSeconds(8))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException("go-librespot did not become ready in time.");
    }

    private static bool HasUsername(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("username", out var username) && !string.IsNullOrEmpty(username.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsRunning)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/events"), cancellationToken);
                var buffer = new byte[16 * 1024];
                using var message = new MemoryStream();
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        if (ParseEvent(message.ToArray()) is { } e)
                        {
                            _logger.LogDebug("go-librespot {Account}: {Type} {Uri}", _account.Id, e.Type, e.Uri);
                            EventReceived?.Invoke(this, e);
                        }

                        message.SetLength(0);
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
            {
                _logger.LogDebug(ex, "go-librespot event socket closed");
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    internal static LibrespotEvent? ParseEvent(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() is not { } typeName)
            {
                return null;
            }

            string? uri = null;
            string? message = null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                uri = data.TryGetProperty("uri", out var u) ? u.GetString() : null;
                message = data.TryGetProperty("message", out var m) ? m.GetString() : null;
            }

            return new LibrespotEvent(typeName, uri, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Log(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains("level=fatal", StringComparison.Ordinal) || line.Contains("level=error", StringComparison.Ordinal))
        {
            _lastError = ExtractError(line);
            _logger.LogWarning("go-librespot {Account}: {Line}", _account.Id, line);
        }
        else
        {
            _logger.LogDebug("go-librespot {Account}: {Line}", _account.Id, line);
        }
    }

    /// <summary>The <c>error="..."</c> (or <c>msg="..."</c>) part of a logrus line.</summary>
    internal static string ExtractError(string line)
    {
        foreach (var key in new[] { "error=\"", "msg=\"" })
        {
            var start = line.IndexOf(key, StringComparison.Ordinal);
            if (start >= 0)
            {
                start += key.Length;
                var end = line.IndexOf('"', start);
                return end > start ? line[start..end] : line[start..];
            }
        }

        return line;
    }

    private static string Yaml(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed record PlayBody([property: System.Text.Json.Serialization.JsonPropertyName("uri")] string Uri);

[System.Text.Json.Serialization.JsonSerializable(typeof(PlayBody))]
internal sealed partial class LibrespotJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
