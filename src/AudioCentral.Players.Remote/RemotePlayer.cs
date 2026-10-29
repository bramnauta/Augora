using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Players;

namespace AudioCentral.Players.Remote;

/// <summary>
/// A player that plays audio itself (browser tab, Android app) and is driven over the events hub.
/// The device reports its state; this class only relays commands and mirrors that state.
/// </summary>
internal sealed class RemotePlayer(string id, string name, PlayerKind kind, IRemotePlayerChannel channel) : IPlayer
{
    private readonly Lock _lock = new();
    private PlayerStatus _status = PlayerStatus.Unavailable;
    private PlayMedia? _current;

    public string Id { get; } = id;

    public string Name { get; private set; } = name;

    public string ProviderId => RemotePlayerProvider.ProviderId;

    public PlayerKind Kind { get; private set; } = kind;

    public string? Model => null;

    public PlayerFeatures Features => PlayerFeatures.Volume | PlayerFeatures.Mute | PlayerFeatures.Pause | PlayerFeatures.Seek;

    public string? ConnectionId { get; private set; }

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
    public string? Attach(string connectionId, string name, PlayerKind kind)
    {
        string? previous;
        lock (_lock)
        {
            previous = ConnectionId == connectionId ? null : ConnectionId;
            ConnectionId = connectionId;
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
            var nowPlaying = _current is { } current && state.State != PlaybackState.Idle
                ? new NowPlayingDto(
                    current.ItemUri,
                    current.Title,
                    current.Subtitle,
                    current.ImageUrl,
                    current.IsLive,
                    state.PositionSeconds is { } position ? TimeSpan.FromSeconds(position) : null,
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
        }

        return SendAsync(
            new RemotePlayerCommandDto(
                RemotePlayerCommandType.Play,
                new RemoteMediaDto(media.StreamPath, media.ContentType, media.IsLive, media.ItemUri, media.Title, media.Subtitle, media.ImageUrl, media.Duration?.TotalSeconds)),
            cancellationToken);
    }

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

    private Task SendAsync(RemotePlayerCommandDto command, CancellationToken cancellationToken) =>
        ConnectionId is { } connectionId
            ? channel.SendCommandAsync(connectionId, command, cancellationToken)
            : throw new AudioCentralException(ErrorCodes.PlayerUnavailable, $"'{Name}' is not connected.", 409);
}
