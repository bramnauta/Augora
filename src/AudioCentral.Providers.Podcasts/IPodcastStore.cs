namespace AudioCentral.Providers.Podcasts;

public sealed record PodcastSubscription(Guid Id, string FeedUrl, string Title, string? Author, string? ImageUrl);

/// <summary>The household's podcast subscriptions; implemented by the server with EF Core.</summary>
public interface IPodcastStore
{
    Task<IReadOnlyList<PodcastSubscription>> GetAllAsync(CancellationToken cancellationToken);

    Task<PodcastSubscription> AddAsync(PodcastSubscription subscription, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken);
}
