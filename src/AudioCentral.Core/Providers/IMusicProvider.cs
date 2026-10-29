using AudioCentral.Core.Media;
using AudioCentral.Core.Streaming;

namespace AudioCentral.Core.Providers;

/// <summary>
/// Who starts playback where. Providers with limited resources (one Spotify stream per account) use it to pick
/// an account; <see cref="TakeoverPlayerId"/> allows stopping that player to free its resource.
/// </summary>
/// <param name="UserId">The AudioCentral user, <c>null</c> when the queue advances on its own and nobody is known.</param>
/// <param name="Continuation">Flow mode: lets a producer continue with the next queue item in the same stream.</param>
public sealed record PlaybackContext(string PlayerId, string? UserId = null, string? TakeoverPlayerId = null, IQueueContinuation? Continuation = null);

/// <summary>
/// Flow mode: a producer whose item ended asks for the next queue item to play in the same stream, so there is no
/// gap and the player does not have to buffer again.
/// </summary>
public interface IQueueContinuation
{
    /// <summary>
    /// Makes the next queue item current when <paramref name="canPlay"/> accepts it and returns it; <c>null</c> ends the
    /// stream (end of the queue, or an item the producer cannot play) and the player moves on as usual.
    /// </summary>
    /// <param name="streamOffset">Where in the stream the next item starts, so the player shows the right item.</param>
    Task<MediaItem?> ContinueAsync(Func<MediaItem, bool> canPlay, TimeSpan streamOffset, CancellationToken cancellationToken);
}

/// <summary>
/// A source of music: radio directory, streaming service, local library, podcasts, ...
/// </summary>
public interface IMusicProvider
{
    /// <summary>Stable identifier, used as the first part of every <see cref="MediaItemRef"/>.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Provider type, e.g. <c>tunein</c> or <c>spotify</c>. Several instances may share a type.</summary>
    string Type { get; }

    /// <summary>Browses a folder; <paramref name="folder"/> is <c>null</c> for the provider root.</summary>
    Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken);

    /// <summary>
    /// The items to put in the queue when <paramref name="item"/> is played: the item itself, or the tracks of an
    /// album, playlist or podcast.
    /// </summary>
    Task<IReadOnlyList<MediaItem>> GetQueueItemsAsync(MediaItem item, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaItem>>(item.IsPlayable ? [item] : []);

    Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken);
}
