using Augora.Core.Media;

namespace Augora.Core.Providers;

/// <summary>
/// A provider whose items remember how far each user listened (podcasts): playback continues there the next time and
/// finished items count as heard.
/// </summary>
public interface IPlaybackProgressProvider
{
    /// <summary>Whether progress is kept for <paramref name="item"/> (episodes, not podcasts or live streams).</summary>
    bool TracksProgress(MediaItemRef item);

    /// <summary>Where <paramref name="userId"/> stopped listening, or <c>null</c> to start at the beginning (never played or finished).</summary>
    Task<TimeSpan?> GetResumePositionAsync(MediaItemRef item, string userId, CancellationToken cancellationToken);

    /// <param name="duration">The length of the item when the player knows it.</param>
    /// <param name="ended">The item played to the end.</param>
    Task SaveProgressAsync(MediaItemRef item, string userId, TimeSpan position, TimeSpan? duration, bool ended, CancellationToken cancellationToken);
}
