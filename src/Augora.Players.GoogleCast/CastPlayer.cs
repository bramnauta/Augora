using Augora.Contracts;
using Augora.Core;
using Augora.Core.Players;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;

namespace Augora.Players.GoogleCast;

/// <summary>
/// A Google Cast device or group, played through the Default Media Receiver.
/// Keeps a connection open so status changes (also from other apps) are visible immediately.
/// </summary>
internal sealed class CastPlayer : IPlayer, IContinuousStreamPlayer, IAsyncDisposable
{
    private const string Backdrop = "E8C28D3C";

    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _reconnectInterval;
    private readonly string _receiverAppId;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _stateLock = new();

    private Task? _connectionLoop;
    private ChromecastClient? _client;
    private ChromecastStatus? _receiverStatus;
    private MediaStatus? _mediaStatus;
    private PlayMedia? _current;
    private long? _endedMediaSession;
    private PlayerStatus _status = PlayerStatus.Unavailable;

    // Flow mode: the items in the stream that plays, and a clock since the last media status to estimate the position.
    private readonly StreamSegments _segments = new();
    private readonly System.Diagnostics.Stopwatch _sinceMediaStatus = new();
    private Timer? _segmentTimer;
    private string? _shownItem;

    /// <param name="receiverAppId">The Cast receiver app to play in; its name is what the device shows.</param>
    public CastPlayer(CastDevice device, TimeSpan reconnectInterval, string receiverAppId, ILoggerFactory loggerFactory)
    {
        Device = device;
        _reconnectInterval = reconnectInterval;
        _receiverAppId = receiverAppId;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CastPlayer>();
    }

    public CastDevice Device { get; private set; }

    public string Id => Device.Id;

    public string Name => Device.Name;

    public string ProviderId => CastPlayerProvider.ProviderId;

    public PlayerKind Kind => Device.Kind;

    public string? Model => Device.Model;

    public PlayerFeatures Features => PlayerFeatures.Volume | PlayerFeatures.Mute | PlayerFeatures.Pause | PlayerFeatures.Seek;

    public PlayerStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    public void Start() => _connectionLoop ??= Task.Run(() => RunConnectionLoopAsync(_stopping.Token));

    /// <summary>The device moved (new IP or port after a DHCP renewal); reconnect to the new address.</summary>
    public void UpdateDevice(CastDevice device)
    {
        if (device.Host == Device.Host && device.Port == Device.Port && device.Name == Device.Name)
        {
            return;
        }

        _logger.LogInformation("Cast device {Name} moved to {Host}:{Port}", device.Name, device.Host, device.Port);
        Device = device;
        _ = DisconnectAsync();
    }

    public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken) =>
        RunCommandAsync(async client =>
        {
            var app = client.ChromecastStatus?.Application;
            if (app?.AppId != _receiverAppId)
            {
                await client.LaunchApplicationAsync(_receiverAppId, false);
            }

            lock (_stateLock)
            {
                _current = media;
                _endedMediaSession = null;
                _segments.Reset(media);
            }

            // A continuous stream changes item without the device knowing: check once a second which one plays.
            if (media.Continuous)
            {
                _segmentTimer ??= new Timer(_ => RefreshSegment(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }

            await client.MediaChannel.LoadAsync(ToCastMedia(media), true, []);
        }, cancellationToken);

    public void AddSegment(PlayMedia media, TimeSpan streamOffset) => _segments.Add(media, streamOffset);

    public Task PauseAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(client => client.MediaChannel.PauseAsync(), cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(client => client.MediaChannel.PlayAsync(), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(async client =>
        {
            lock (_stateLock)
            {
                _current = null;
            }

            if (client.MediaStatus is not null)
            {
                await client.MediaChannel.StopAsync();
            }
        }, cancellationToken);

    public Task SetVolumeAsync(double level, CancellationToken cancellationToken) =>
        RunCommandAsync(client => client.ReceiverChannel.SetVolume(Math.Clamp(level, 0, 1)), cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
        RunCommandAsync(client => client.ReceiverChannel.SetMute(muted), cancellationToken);

    /// <summary>The receiver seeks itself, with byte ranges on the stream (also what the device's own screen does).</summary>
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) =>
        RunCommandAsync(client =>
        {
            TimeSpan start;
            lock (_stateLock)
            {
                start = _current?.StartPosition ?? TimeSpan.Zero;
            }

            return client.MediaChannel.SeekAsync(Math.Max(0, (position - start).TotalSeconds));
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_segmentTimer is not null)
        {
            await _segmentTimer.DisposeAsync();
        }

        await DisconnectAsync();
        if (_connectionLoop is not null)
        {
            await _connectionLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stopping.Dispose();
        _commandLock.Dispose();
    }

    private async Task RunCommandAsync(Func<ChromecastClient, Task> command, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            var client = _client ?? throw new AugoraException(ErrorCodes.PlayerUnavailable, $"'{Name}' is not connected.", 409);
            await command(client).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new AugoraException(ErrorCodes.PlaybackFailed, $"'{Name}' did not respond.", 504, ex);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var client = new ChromecastClient(_loggerFactory.CreateLogger<ChromecastClient>());
                client.Disconnected += (_, _) => disconnected.TrySetResult();
                client.ReceiverChannel.ReceiverStatusChanged += (_, status) => OnReceiverStatus(status);
                client.MediaChannel.StatusChanged += (_, status) => OnMediaStatus(status);

                var status = await client.ConnectChromecast(Device.ToReceiver()).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                _client = client;
                _logger.LogInformation("Connected to Cast device {Name} at {Host}:{Port}", Name, Device.Host, Device.Port);
                OnReceiverStatus(status);

                await disconnected.Task.WaitAsync(cancellationToken);
                _logger.LogInformation("Cast device {Name} disconnected", Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connecting to Cast device {Name} at {Host}:{Port} failed", Name, Device.Host, Device.Port);
            }

            await DisconnectAsync();
            await Task.Delay(_reconnectInterval, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task DisconnectAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        lock (_stateLock)
        {
            _receiverStatus = null;
            _mediaStatus = null;
        }

        UpdateStatus();
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disconnecting from {Name} failed", Name);
        }
    }

    private void OnReceiverStatus(ChromecastStatus? status)
    {
        if (status is null)
        {
            return;
        }

        lock (_stateLock)
        {
            _receiverStatus = status;
        }

        UpdateStatus();
    }

    private void OnMediaStatus(MediaStatus? status)
    {
        PlaybackEndReason? ended = null;
        lock (_stateLock)
        {
            _mediaStatus = status;
            _sinceMediaStatus.Restart();

            // Idle with a reason means the media stopped by itself; report it once per media session.
            if (status is { PlayerState: PlayerStateType.Idle, IdleReason: { } reason }
                && _current is not null
                && _endedMediaSession != status.MediaSessionId
                && reason is "FINISHED" or "ERROR")
            {
                _endedMediaSession = status.MediaSessionId;
                ended = reason == "FINISHED" ? PlaybackEndReason.Finished : PlaybackEndReason.Error;
            }
        }

        UpdateStatus();
        if (ended is { } endReason)
        {
            _logger.LogInformation("Playback on {Name} ended: {Reason}", Name, endReason);
            PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(endReason));
        }
    }

    private void UpdateStatus()
    {
        lock (_stateLock)
        {
            _status = BuildStatus();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private PlayerStatus BuildStatus()
    {
        if (_client is null || _receiverStatus is null)
        {
            return PlayerStatus.Unavailable;
        }

        var volume = _receiverStatus.Volume;
        var app = _receiverStatus.Application;
        var isOurs = app?.AppId == _receiverAppId
            && _current is not null
            && (_mediaStatus?.Media is null || _mediaStatus.Media.ContentUrl == _current.StreamUrl.ToString() || _mediaStatus.Media.ContentId == _current.StreamUrl.ToString());

        string? external = app is null || app.AppId == Backdrop || isOurs ? null : app.DisplayName;

        var state = _mediaStatus?.PlayerState switch
        {
            PlayerStateType.Playing => PlaybackState.Playing,
            PlayerStateType.Paused => PlaybackState.Paused,
            PlayerStateType.Buffering or PlayerStateType.Loading => PlaybackState.Buffering,
            _ => PlaybackState.Idle,
        };

        NowPlayingDto? nowPlaying = null;
        if (isOurs && _current is { } current && state != PlaybackState.Idle)
        {
            // The device reports its time only on changes: count on from there, so every status has a current position.
            var streamPosition = StreamPosition(state);
            TimeSpan? position = streamPosition is { } played ? current.ItemPosition(played) : null;
            if (current.Continuous && streamPosition is { } inStream && _segments.At(inStream) is { } segment)
            {
                current = segment.Media;
                position = segment.Position;
            }

            _shownItem = current.ItemUri;
            nowPlaying = new NowPlayingDto(
                current.ItemUri,
                current.Title,
                current.Subtitle,
                // The UI shows this: relative server artwork works through the UI's url, unlike the LAN url.
                current.ImagePath ?? current.ImageUrl,
                current.IsLive,
                position,
                current.Duration);
        }
        else if (external is not null && _mediaStatus?.Media?.Metadata is { } metadata)
        {
            nowPlaying = new NowPlayingDto(null, metadata.Title ?? external, metadata.SubTitle, metadata.Images?.FirstOrDefault()?.Url, false, null, null);
        }

        return new PlayerStatus(true, state, volume?.Level ?? 0, volume?.Muted ?? false, nowPlaying, external);
    }

    /// <summary>The device's position in the stream: the last reported time plus the time played since.</summary>
    private TimeSpan? StreamPosition(PlaybackState state) =>
        _mediaStatus is { } media
            ? TimeSpan.FromSeconds(media.CurrentTime) + (state == PlaybackState.Playing ? _sinceMediaStatus.Elapsed : TimeSpan.Zero)
            : null;

    /// <summary>Publishes the status when the item at the stream position changed (flow mode).</summary>
    private void RefreshSegment()
    {
        string? playing;
        lock (_stateLock)
        {
            if (_current is not { Continuous: true } || StreamPosition(_status.State) is not { } position)
            {
                return;
            }

            playing = _segments.At(position)?.Media.ItemUri;
        }

        if (playing != _shownItem)
        {
            UpdateStatus();
        }
    }

    private static Media ToCastMedia(PlayMedia media) => new()
    {
        ContentUrl = media.StreamUrl.ToString(),
        ContentId = media.StreamUrl.ToString(),
        ContentType = media.ContentType,
        StreamType = media.IsLive ? StreamType.Live : StreamType.Buffered,
        // A continuous stream is as long as the queue; the device must not stop after the first item's length.
        Duration = media.Continuous ? null : media.Duration?.TotalSeconds,
        Metadata = new MusicTrackMetadata
        {
            MetadataType = MetadataType.Music,
            Title = media.Title,
            Artist = media.Subtitle,
            Images = media.ImageUrl is null ? [] : [new Image { Url = media.ImageUrl }],
        },
    };
}
