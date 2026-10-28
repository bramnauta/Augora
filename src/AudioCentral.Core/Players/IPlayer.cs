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

/// <summary>What a player is asked to play. <see cref="StreamUrl"/> points to AudioCentral's stream endpoint.</summary>
public sealed record PlayMedia(
    Uri StreamUrl,
    string ContentType,
    bool IsLive,
    string ItemUri,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    TimeSpan? Duration);

public enum PlaybackEndReason
{
    Finished,
    Error,
}

public sealed class PlaybackEndedEventArgs(PlaybackEndReason reason) : EventArgs
{
    public PlaybackEndReason Reason { get; } = reason;
}
