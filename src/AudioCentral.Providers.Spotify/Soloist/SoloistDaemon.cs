using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Providers.Spotify.Soloist;

/// <summary>An event from the Soloist WebSocket API (<c>playback_changed</c>, <c>track_changed</c>, ...).</summary>
/// <param name="Status">For <c>playback_changed</c> and <c>playback_state</c>: idle, playing, paused or buffering.</param>
/// <param name="Uri">For <c>track_changed</c> and <c>playback_state</c>: the current item.</param>
internal sealed record SoloistEvent(string Type, string? Status, string? Uri, bool? LoggedIn, string? Message, long? PositionMs);

/// <summary>
/// A long-running Soloist process for one account, controlled over its local WebSocket API. Starting it restores the
/// paired session once, so tracks start without a new login each time.
/// </summary>
internal sealed class SoloistDaemon : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ClientWebSocket _socket = new();
    private readonly Process _process;
    private volatile string? _lastError;

    private SoloistDaemon(Process process, string directory, ILogger logger)
    {
        _process = process;
        _directory = directory;
        _logger = logger;
    }

    public bool IsRunning => !_process.HasExited && _socket.State == WebSocketState.Open;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public string? LastError => _lastError;

    /// <summary>Raised for every WebSocket event.</summary>
    public event EventHandler<SoloistEvent>? EventReceived;

    /// <summary>
    /// Starts Soloist with the WebSocket API and waits until the stored session is restored; <c>LoggedIn</c> is
    /// <c>false</c> when it is missing or refused.
    /// </summary>
    public static async Task<(SoloistDaemon Daemon, bool LoggedIn)> StartAsync(
        Func<IEnumerable<string>, Action<string>, Process> start, string directory, ILogger logger, CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(directory, "ws.port");
        if (File.Exists(portFile))
        {
            File.Delete(portFile);
        }

        SoloistDaemon? daemon = null;
        var process = start(["-w", "127.0.0.1:0"], line =>
        {
            if (daemon is not null && (line.Contains(" E [", StringComparison.Ordinal) || line.Contains(" W [", StringComparison.Ordinal)))
            {
                daemon._lastError = line;
            }
        });
        daemon = new SoloistDaemon(process, directory, logger);
        try
        {
            // Port 0 lets the OS choose; Soloist writes the port to its data directory.
            var elapsed = Stopwatch.StartNew();
            int port;
            while (!File.Exists(portFile) || !int.TryParse((await File.ReadAllTextAsync(portFile, cancellationToken)).Trim(), out port))
            {
                if (process.HasExited || elapsed.Elapsed > StartTimeout)
                {
                    throw new InvalidOperationException($"Soloist did not start its WebSocket API (exit {(process.HasExited ? process.ExitCode : -1)}): {daemon._lastError ?? "see the log"}");
                }

                await Task.Delay(100, cancellationToken);
            }

            await daemon._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cancellationToken);
            var authenticated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnAuth(object? sender, SoloistEvent e)
            {
                if (e.Type == "auth_state" && e.LoggedIn is { } loggedIn)
                {
                    // The session restore may still be running when the first auth_state arrives.
                    if (loggedIn || elapsed.Elapsed > TimeSpan.FromSeconds(10))
                    {
                        authenticated.TrySetResult(loggedIn);
                    }
                }
            }

            daemon.EventReceived += OnAuth;
            _ = Task.Run(() => daemon.ReceiveAsync(daemon._stopping.Token), CancellationToken.None);
            try
            {
                while (!authenticated.Task.IsCompleted && elapsed.Elapsed < StartTimeout)
                {
                    await daemon.SendAsync("get_auth_state", null, cancellationToken);
                    await Task.WhenAny(authenticated.Task, Task.Delay(1000, cancellationToken));
                }
            }
            finally
            {
                daemon.EventReceived -= OnAuth;
            }

            var loggedIn = authenticated.Task.IsCompleted && await authenticated.Task;
            logger.LogInformation("Soloist daemon started in {Seconds:F1}s, logged in: {LoggedIn}", elapsed.Elapsed.TotalSeconds, loggedIn);
            return (daemon, loggedIn);
        }
        catch
        {
            await daemon.DisposeAsync();
            throw;
        }
    }

    /// <summary>Sends a command such as <c>play</c> (with <c>uri</c>) or <c>pause</c>.</summary>
    public async Task SendAsync(string command, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["type"] = "command", ["command"] = command };
        foreach (var (name, value) in arguments ?? new Dictionary<string, object?>())
        {
            message[name] = JsonValue.Create(value);
        }

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
        _socket.Dispose();
        _sendLock.Dispose();
        _stopping.Dispose();
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (Parse(message.ToArray()) is { } e)
                {
                    if (e.Type is not "position_sync")
                    {
                        _logger.LogDebug("Soloist event {Type} {Status} {Uri}", e.Type, e.Status, e.Uri);
                    }

                    EventReceived?.Invoke(this, e);
                }

                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            _logger.LogDebug(ex, "Soloist WebSocket closed in {Directory}", _directory);
        }
    }

    internal static SoloistEvent? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() is not { } typeName)
            {
                return null;
            }

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var uri = root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out var u)
                ? u.GetString()
                : null;
            bool? loggedIn = root.TryGetProperty("logged_in", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False ? l.GetBoolean() : null;
            var text = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            long? position = root.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("position_ms", out var ms) && ms.TryGetInt64(out var value)
                ? value
                : null;
            return new SoloistEvent(typeName, status, uri, loggedIn, text, position);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
