using Augora.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Augora.Client;

/// <summary>
/// Makes the device an Augora player: registers over the events hub (the same protocol as the browser player),
/// plays the commanded streams through the <see cref="IPlayerBackend"/> and reports its state.
/// </summary>
public sealed class PlayerController : IAsyncDisposable
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly ServerClient _server;
    private readonly IPlayerBackend _backend;
    private readonly CancellationTokenSource _stopping = new();
    private HubConnection? _hub;
    private CancellationTokenSource? _reporting;
    private RemoteMediaDto? _media;
    private PlaybackState _state = PlaybackState.Idle;
    private double _volume = 1;
    private bool _muted;
    private bool _loaded;
    private string? _playerId;
    private bool _started;

    public PlayerController(AppSettings settings, ServerClient server, IPlayerBackend backend)
    {
        _settings = settings;
        _server = server;
        _backend = backend;
        backend.Ready += OnReady;
        backend.Completed += OnCompleted;
        backend.Failed += OnFailed;
        backend.CommandRequested += SendCommand;
    }

    /// <summary>Another app instance took over this device id; the owner should stop.</summary>
    public event Action? Released;

    /// <summary>The login expired; the app asks for a new one when it is opened.</summary>
    public event Action? LoginExpired;

    /// <summary>Connects and registers in the background; does nothing when already started.</summary>
    public void Start()
    {
        if (!_started)
        {
            _started = true;
            _ = Task.Run(() => ConnectAsync(_stopping.Token));
        }
    }

    /// <summary>Sends a player command (pause, resume, next, previous, stop) through the server.</summary>
    public void SendCommand(string command)
    {
        if (_playerId is { } playerId)
        {
            _ = _server.SendPlayerCommandAsync(playerId, command);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _reporting?.Cancel();
        _backend.Ready -= OnReady;
        _backend.Completed -= OnCompleted;
        _backend.Failed -= OnFailed;
        _backend.CommandRequested -= SendCommand;
        _backend.Release();
        if (_hub is { } hub)
        {
            await hub.DisposeAsync();
        }

        _stopping.Dispose();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_settings.ServerUrl}{EventsHub.Path}", options => options.AccessTokenProvider = _server.GetAccessTokenAsync)
            .WithAutomaticReconnect(new RetryForever())
            .Build();
        hub.On<RemotePlayerCommandDto>(RemotePlayerHub.Command, command => _backend.Post(() => Execute(command)));
        hub.On(RemotePlayerHub.Released, () => _backend.Post(() =>
        {
            // Another app instance took over this device id.
            Release();
            Released?.Invoke();
        }));
        hub.Reconnected += _ => RegisterAsync();
        _hub = hub;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await hub.StartAsync(cancellationToken);
                await RegisterAsync();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
            {
                if (!_server.HasTokens)
                {
                    _backend.Post(() => LoginExpired?.Invoke());
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task RegisterAsync()
    {
        if (_hub is not { State: HubConnectionState.Connected } hub)
        {
            return;
        }

        _playerId = await hub.InvokeAsync<string>(RemotePlayerHub.Register, new RegisterPlayerRequest(_settings.DeviceId, _backend.DeviceName, _backend.Kind));
        await ReportAsync();
    }

    private void Execute(RemotePlayerCommandDto command)
    {
        switch (command.Type)
        {
            case RemotePlayerCommandType.Play when command.Media is { } media:
                Play(media);
                break;
            case RemotePlayerCommandType.Pause when _loaded && _state == PlaybackState.Playing:
                _backend.Pause();
                SetState(PlaybackState.Paused);
                break;
            case RemotePlayerCommandType.Resume when _loaded && _state == PlaybackState.Paused:
                _backend.Start();
                SetState(PlaybackState.Playing);
                break;
            case RemotePlayerCommandType.Stop:
                Release();
                SetState(PlaybackState.Idle);
                break;
            case RemotePlayerCommandType.SetVolume when command.Volume is { } volume:
                _volume = Math.Clamp(volume, 0, 1);
                ApplyVolume();
                _ = ReportAsync();
                break;
            case RemotePlayerCommandType.SetMuted when command.Muted is { } muted:
                _muted = muted;
                ApplyVolume();
                _ = ReportAsync();
                break;
            case RemotePlayerCommandType.Seek when _loaded && command.PositionSeconds is { } position:
                _backend.Seek(Math.Max(0, position));
                _ = ReportAsync();
                break;
        }
    }

    private void Play(RemoteMediaDto media)
    {
        Release();
        _media = media;
        var url = media.StreamUrl.StartsWith('/') ? _settings.ServerUrl + media.StreamUrl : media.StreamUrl;

        try
        {
            _backend.Load(new Uri(url));
            _loaded = true;
            ApplyVolume();
            SetState(PlaybackState.Buffering);
        }
        catch (Exception ex)
        {
            Release();
            SetState(PlaybackState.Idle, RemotePlaybackEvent.Error, ex.Message);
        }
    }

    private void OnReady()
    {
        if (_loaded)
        {
            _backend.Start();
            SetState(PlaybackState.Playing);
        }
    }

    private void OnCompleted()
    {
        Release();
        SetState(PlaybackState.Idle, RemotePlaybackEvent.Finished);
    }

    private void OnFailed(string error)
    {
        Release();
        SetState(PlaybackState.Idle, RemotePlaybackEvent.Error, error);
    }

    private void Release()
    {
        _loaded = false;
        _backend.Release();
    }

    private void SetState(PlaybackState state, RemotePlaybackEvent playbackEvent = RemotePlaybackEvent.None, string? error = null)
    {
        _state = state;
        _backend.ShowNowPlaying(_media, state);
        _ = ReportAsync(playbackEvent, error);
        ScheduleReports(state == PlaybackState.Playing);
    }

    /// <summary>Position updates while playing, so the UI's progress bar follows.</summary>
    private void ScheduleReports(bool playing)
    {
        _reporting?.Cancel();
        if (!playing)
        {
            return;
        }

        var reporting = _reporting = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!reporting.IsCancellationRequested)
            {
                await Task.Delay(ReportInterval, reporting.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!reporting.IsCancellationRequested)
                {
                    _backend.Post(() => _ = ReportAsync());
                }
            }
        }, reporting.Token);
    }

    private async Task ReportAsync(RemotePlaybackEvent playbackEvent = RemotePlaybackEvent.None, string? error = null)
    {
        if (_hub is not { State: HubConnectionState.Connected } hub || _playerId is null)
        {
            return;
        }

        var position = _loaded && _state is PlaybackState.Playing or PlaybackState.Paused ? _backend.PositionSeconds : null;
        try
        {
            await hub.SendAsync(RemotePlayerHub.ReportState, new RemotePlayerStateDto(_state, _volume, _muted, position, playbackEvent, error));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            // Reconnecting; the state is reported again after registering.
        }
    }

    private void ApplyVolume() => _backend.SetVolume(_muted ? 0 : _volume);

    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            TimeSpan.FromSeconds(Math.Min(30, 2 + retryContext.PreviousRetryCount * 2));
    }
}
