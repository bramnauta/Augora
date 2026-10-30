using Augora.Contracts;

namespace Augora.Core.Players;

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

    /// <summary>Seeks in the stream that plays; only called for seekable streams (files, ranged HTTP, sized producers).</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
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
/// <param name="StreamUrl">Absolute url for players on the LAN (Cast): Augora's stream endpoint, or the upstream for direct streams.</param>
/// <param name="StreamPath">
/// The same stream relative to the server (<c>/api/stream/...</c>), for players that reach Augora through
/// the url of the UI (browser behind a reverse proxy). Absolute for direct streams.
/// </param>
/// <param name="ImageUrl">Absolute artwork url (server artwork is made absolute like <paramref name="StreamUrl"/>).</param>
/// <param name="ImagePath">The artwork as the item has it, relative for server artwork; for players using the UI's url.</param>
/// <param name="Continuous">
/// Flow mode: the stream may carry the following queue items too. Its length is unknown, and the player learns where
/// each next item starts through <see cref="IContinuousStreamPlayer.AddSegment"/>.
/// </param>
/// <param name="StartPosition">
/// The stream starts this far into the item (a seek in a stream that cannot seek itself restarts it there): players add
/// it to the stream position they report.
/// </param>
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
    string? ImagePath = null,
    bool Continuous = false,
    TimeSpan StartPosition = default)
{
    /// <summary>The position in the item at <paramref name="streamPosition"/> of a stream that plays only this item.</summary>
    public TimeSpan ItemPosition(TimeSpan streamPosition) => streamPosition + StartPosition;
}

/// <summary>A player that can show the item playing at the current position of a continuous (flow mode) stream.</summary>
public interface IContinuousStreamPlayer
{
    /// <summary><paramref name="media"/> starts at <paramref name="streamOffset"/> into the stream that is playing.</summary>
    void AddSegment(PlayMedia media, TimeSpan streamOffset);
}

/// <summary>Which item of a continuous stream plays at a stream position.</summary>
public sealed class StreamSegments
{
    private readonly Lock _lock = new();
    private readonly List<(TimeSpan Offset, PlayMedia Media)> _segments = [];

    public void Reset(PlayMedia first)
    {
        lock (_lock)
        {
            _segments.Clear();
            _segments.Add((TimeSpan.Zero, first));
        }
    }

    public void Add(PlayMedia media, TimeSpan offset)
    {
        lock (_lock)
        {
            _segments.Add((offset, media));
            _segments.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }
    }

    /// <summary>The item at <paramref name="streamPosition"/> and the position within it.</summary>
    public (PlayMedia Media, TimeSpan Position)? At(TimeSpan streamPosition)
    {
        lock (_lock)
        {
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                if (_segments[i].Offset <= streamPosition || i == 0)
                {
                    var position = streamPosition - _segments[i].Offset;
                    var media = _segments[i].Media;
                    return (media, media.ItemPosition(position < TimeSpan.Zero ? TimeSpan.Zero : position));
                }
            }

            return null;
        }
    }

    /// <summary>The offset of the first segment after <paramref name="streamPosition"/>, to refresh the status then.</summary>
    public TimeSpan? NextOffsetAfter(TimeSpan streamPosition)
    {
        lock (_lock)
        {
            return _segments.Where(s => s.Offset > streamPosition).Select(s => (TimeSpan?)s.Offset).FirstOrDefault();
        }
    }
}

public enum PlaybackEndReason
{
    Finished,
    Error,
}

public sealed class PlaybackEndedEventArgs(PlaybackEndReason reason) : EventArgs
{
    public PlaybackEndReason Reason { get; } = reason;
}
