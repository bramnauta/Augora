using AudioCentral.Contracts;

namespace AudioCentral.Core.Players;

/// <summary>
/// Something that can play a stream: a Cast device, a browser tab, an Android app, ...
/// </summary>
public interface IPlayer
{
    /// <summary>Globally unique and stable across restarts, e.g. <c>cast:5c3f...</c>.</summary>
    string Id { get; }

    string Name { get; }

    string ProviderId { get; }

    PlayerKind Kind { get; }

    string? Model { get; }

    PlayerFeatures Features { get; }

    PlayerStatus Status { get; }

    event EventHandler? StatusChanged;

    /// <summary>Raised when the current media stops by itself (finished or failed), not on an explicit stop.</summary>
    event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    Task PlayAsync(PlayMedia media, CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task SetVolumeAsync(double level, CancellationToken cancellationToken);

    Task SetMutedAsync(bool muted, CancellationToken cancellationToken);
}

public sealed record PlayerStatus(
    bool Available,
    PlaybackState State,
    double Volume,
    bool Muted,
    NowPlayingDto? NowPlaying,
    string? ExternalSource)
{
    public static PlayerStatus Unavailable { get; } = new(false, PlaybackState.Idle, 0, false, null, null);
}

/// <summary>What a player is asked to play.</summary>
/// <param name="StreamUrl">Absolute url for players on the LAN (Cast): AudioCentral's stream endpoint, or the upstream for direct streams.</param>
/// <param name="StreamPath">
/// The same stream relative to the server (<c>/api/stream/...</c>), for players that reach AudioCentral through
/// the url of the UI (browser behind a reverse proxy). Absolute for direct streams.
/// </param>
/// <param name="ImageUrl">Absolute artwork url (server artwork is made absolute like <paramref name="StreamUrl"/>).</param>
/// <param name="ImagePath">The artwork as the item has it, relative for server artwork; for players using the UI's url.</param>
public sealed record PlayMedia(
    Uri StreamUrl,
    string StreamPath,
    string ContentType,
    bool IsLive,
    string ItemUri,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    TimeSpan? Duration,
    string? ImagePath = null);

public enum PlaybackEndReason
{
    Finished,
    Error,
}

public sealed class PlaybackEndedEventArgs(PlaybackEndReason reason) : EventArgs
{
    public PlaybackEndReason Reason { get; } = reason;
}
