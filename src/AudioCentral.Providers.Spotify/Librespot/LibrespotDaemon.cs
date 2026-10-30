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

    /// <summary>Starts the process and waits for its API; the Spotify login may still be pending (see <see cref="GetLoginAsync"/>).</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        _port = GetFreePort();
        await File.WriteAllTextAsync(Path.Combine(_directory, "config.yml"), BuildConfig(), cancellationToken);
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

    /// <param name="position">Where in the track to start.</param>
    public async Task PlayAsync(string spotifyUri, TimeSpan position, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("player/play", new PlayBody(spotifyUri, (long)position.TotalMilliseconds), LibrespotJsonContext.Default.PlayBody, cancellationToken);
        }
        catch (HttpRequestException ex) when (!IsRunning)
        {
            throw new InvalidOperationException($"go-librespot stopped: {_lastError ?? "unknown error"}.", ex);
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

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the login: the Spotify username once logged in, or the pairing code
    /// the user must approve at spotify.com/pair. <c>null</c> when neither is known yet.
    /// </summary>
    public async Task<LibrespotLogin?> GetLoginAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            EnsureRunning();
            try
            {
                using (var status = await _http.GetAsync("status", cancellationToken))
                {
                    if (status.StatusCode == HttpStatusCode.OK && Username(await status.Content.ReadAsByteArrayAsync(cancellationToken)) is { } username)
                    {
                        return new LibrespotLogin(username, null);
                    }
                }

                using var code = await _http.GetAsync("auth/code", cancellationToken);
                if (code.StatusCode == HttpStatusCode.OK
                    && await code.Content.ReadFromJsonAsync(LibrespotJsonContext.Default.DeviceAuthCode, cancellationToken) is { } pairing)
                {
                    return new LibrespotLogin(null, pairing);
                }
            }
            catch (HttpRequestException) when (!IsRunning)
            {
                EnsureRunning();
            }

            await Task.Delay(500, cancellationToken);
        }
        while (elapsed.Elapsed < wait);

        return null;
    }

    /// <summary>Forgets the stored login, e.g. after pairing with the wrong Spotify account.</summary>
    public void DeleteStoredLogin()
    {
        var state = Path.Combine(_directory, "state.json");
        if (File.Exists(state))
        {
            File.Delete(state);
        }
    }

    private void EnsureRunning()
    {
        if (_process is { HasExited: true } process)
        {
            throw new InvalidOperationException($"go-librespot exited with code {process.ExitCode}: {_lastError ?? "see the log"}.");
        }
    }

    // go-librespot logs in itself: Spotify only accepts login tokens issued to librespot's own client id, not to the
    // app used for the Web API. The user approves the device once at spotify.com/pair; state.json keeps the login.
    internal string BuildConfig() => $"""
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
          type: device_auth
        """;

    /// <summary>Waits until the API answers; the login itself can wait for the user's approval.</summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < StartTimeout)
        {
            EnsureRunning();
            try
            {
                using var response = await _http.GetAsync("status", cancellationToken);
                return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException("go-librespot did not become ready in time.");
    }

    internal static string? Username(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("username", out var username) && username.GetString() is { Length: > 0 } name ? name : null;
        }
        catch (JsonException)
        {
            return null;
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

internal sealed record PlayBody(
    [property: System.Text.Json.Serialization.JsonPropertyName("uri")] string Uri,
    [property: System.Text.Json.Serialization.JsonPropertyName("position")] long PositionMs);

/// <summary>A pending device authorisation (<c>GET /auth/code</c>).</summary>
internal sealed record DeviceAuthCode(
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>Logged in as <paramref name="Username"/>, or waiting for the user to approve <paramref name="Pairing"/>.</summary>
internal sealed record LibrespotLogin(string? Username, DeviceAuthCode? Pairing);

[System.Text.Json.Serialization.JsonSerializable(typeof(PlayBody))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DeviceAuthCode))]
internal sealed partial class LibrespotJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
