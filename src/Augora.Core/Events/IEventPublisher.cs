using Augora.Contracts;

namespace Augora.Core.Events;

/// <summary>
/// Pushes state changes to connected clients (UI, Android, Home Assistant).
/// </summary>
public interface IEventPublisher
{
    Task PlayerUpdatedAsync(PlayerDto player);

    Task PlayerRemovedAsync(string playerId);

    Task QueueUpdatedAsync(QueueDto queue);

    /// <summary>A podcast the user subscribed to published an episode; only that user's connections get it.</summary>
    Task PodcastEpisodeAddedAsync(string userId, PodcastEpisodeAddedDto episode);
}
