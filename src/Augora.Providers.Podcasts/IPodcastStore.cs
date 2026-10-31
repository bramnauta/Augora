namespace Augora.Providers.Podcasts;

/// <param name="UserId">The Augora user who subscribed: subscriptions are personal.</param>
/// <param name="LatestEpisodeKey">The newest episode of the feed when it was last checked; newer ones are announced as new.</param>
public sealed record PodcastSubscription(
    Guid Id,
    string UserId,
    string FeedUrl,
    string Title,
    string? Author,
    string? ImageUrl,
    string? LatestEpisodeKey = null,
    DateTimeOffset? LatestPublishedAt = null);

/// <param name="EpisodeId">Item id of the episode: <c>{feed}/{episode}</c>.</param>
/// <param name="Position">Where the user stopped; zero for a heard episode.</param>
/// <param name="Completed">The user listened to the end.</param>
public sealed record PodcastProgress(string EpisodeId, TimeSpan Position, TimeSpan? Duration, bool Completed);

/// <summary>Each user's podcast subscriptions and listening progress; implemented by the server with EF Core.</summary>
public interface IPodcastStore
{
    Task<IReadOnlyList<PodcastSubscription>> GetAllAsync(string userId, CancellationToken cancellationToken);

    /// <summary>The subscriptions of all users, to check the feeds for new episodes.</summary>
    Task<IReadOnlyList<PodcastSubscription>> GetEveryoneAsync(CancellationToken cancellationToken);

    Task<PodcastSubscription> AddAsync(PodcastSubscription subscription, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string userId, Guid id, CancellationToken cancellationToken);

    Task SetLatestEpisodeAsync(Guid id, string episodeKey, DateTimeOffset? publishedAt, CancellationToken cancellationToken);

    Task<PodcastProgress?> GetProgressAsync(string userId, string episodeId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, PodcastProgress>> GetProgressAsync(string userId, IReadOnlyCollection<string> episodeIds, CancellationToken cancellationToken);

    Task SaveProgressAsync(string userId, PodcastProgress progress, CancellationToken cancellationToken);
}
