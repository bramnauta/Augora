using Augora.Contracts;
using Augora.Core;
using Augora.Core.Players;

namespace Augora.Players.Remote;

/// <summary>
/// A player that plays audio itself (browser tab, Android app) and is driven over the events hub.
/// The device reports its state; this class only relays commands and mirrors that state.
/// </summary>
internal sealed class RemotePlayer(string id, string name, PlayerKind kind, IRemotePlayerChannel channel) : IPlayer, IContinuousStreamPlayer
{
    private readonly Lock _lock = new();
    private readonly StreamSegments _segments = new();
    private PlayerStatus _status = PlayerStatus.Unavailable;
    private PlayMedia? _current;

    public string Id { get; } = id;

    public string Name { get; private set; } = name;

    public string ProviderId => RemotePlayerProvider.ProviderId;

    public PlayerKind Kind { get; private set; } = kind;

    public string? Model => null;

    public PlayerFeatures Features => PlayerFeatures.Volume | PlayerFeatures.Mute | PlayerFeatures.Pause | PlayerFeatures.Seek;

    /// <summary>Browser sessions are forgotten when they stay disconnected; installed apps are kept.</summary>
    public bool Ephemeral { get; private set; }

    public string? ConnectionId { get; private set; }

    /// <summary>When the last connection dropped; <c>null</c> while connected.</summary>
    public DateTimeOffset? DisconnectedAt { get; private set; }

    public PlayerStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    /// <summary>Binds the player to a (new) connection and returns the connection it replaces, if any.</summary>
    public string? Attach(string connectionId, string name, PlayerKind kind, bool ephemeral)
    {
        string? previous;
        lock (_lock)
        {
            previous = ConnectionId == connectionId ? null : ConnectionId;
            ConnectionId = connectionId;
            DisconnectedAt = null;
            Ephemeral = ephemeral;
            Name = name;
            Kind = kind;
            _status = _status with { Available = true };
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        return previous;
    }

    /// <summary>Returns <c>false</c> when the connection was not (or no longer) the active one.</summary>
    public bool Detach(string connectionId)
    {
        lock (_lock)
        {
            if (ConnectionId != connectionId)
            {
                return false;
            }

            ConnectionId = null;
            DisconnectedAt = DateTimeOffset.UtcNow;
            _current = null;
            _status = PlayerStatus.Unavailable;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ReportState(RemotePlayerStateDto state)
    {
        lock (_lock)
        {
            var current = _current;
            TimeSpan? position = state.PositionSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
            // Flow mode: the device reports its position in the whole stream; show the item playing there.
            if (current is { Continuous: true } && position is { } streamPosition && _segments.At(streamPosition) is { } segment)
            {
                current = segment.Media;
                position = segment.Position;
            }
            else if (current is not null && position is { } itemStreamPosition)
            {
                position = current.ItemPosition(itemStreamPosition);
            }

            var nowPlaying = current is not null && state.State != PlaybackState.Idle
                ? new NowPlayingDto(
                    current.ItemUri,
                    current.Title,
                    current.Subtitle,
                    current.ImagePath ?? current.ImageUrl,
                    current.IsLive,
                    position,
                    current.Duration)
                : null;
            _status = new PlayerStatus(ConnectionId is not null, state.State, Math.Clamp(state.Volume, 0, 1), state.Muted, nowPlaying, null);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (state.Event != RemotePlaybackEvent.None)
        {
            PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(state.Event == RemotePlaybackEvent.Finished ? PlaybackEndReason.Finished : PlaybackEndReason.Error));
        }
    }

    public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _current = media;
            _segments.Reset(media);
        }

        return SendAsync(
            new RemotePlayerCommandDto(
                RemotePlayerCommandType.Play,
                new RemoteMediaDto(media.StreamPath, media.ContentType, media.IsLive, media.ItemUri, media.Title, media.Subtitle, media.ImagePath ?? media.ImageUrl,
                    media.Continuous ? null : media.Duration?.TotalSeconds)),
            cancellationToken);
    }

    public void AddSegment(PlayMedia media, TimeSpan streamOffset) => _segments.Add(media, streamOffset);

    public Task PauseAsync(CancellationToken cancellationToken) => SendAsync(new(RemotePlayerCommandType.Pause), cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => SendAsync(new(RemotePlayerCommandType.Resume), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _current = null;
        }

        return SendAsync(new(RemotePlayerCommandType.Stop), cancellationToken);
    }

    public Task SetVolumeAsync(double level, CancellationToken cancellationToken) =>
        SendAsync(new(RemotePlayerCommandType.SetVolume, Volume: Math.Clamp(level, 0, 1)), cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
        SendAsync(new(RemotePlayerCommandType.SetMuted, Muted: muted), cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        TimeSpan start;
        lock (_lock)
        {
            start = _current?.StartPosition ?? TimeSpan.Zero;
        }

        return SendAsync(new(RemotePlayerCommandType.Seek, PositionSeconds: Math.Max(0, (position - start).TotalSeconds)), cancellationToken);
    }

    private Task SendAsync(RemotePlayerCommandDto command, CancellationToken cancellationToken) =>
        ConnectionId is { } connectionId
            ? channel.SendCommandAsync(connectionId, command, cancellationToken)
            : throw new AugoraException(ErrorCodes.PlayerUnavailable, $"'{Name}' is not connected.", 409);
}
