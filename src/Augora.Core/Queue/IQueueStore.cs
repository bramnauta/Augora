using Augora.Contracts;

namespace Augora.Core.Queue;

/// <summary>
/// Persists queues so they survive a restart.
/// </summary>
public interface IQueueStore
{
    Task<QueueDto?> LoadAsync(string playerId, CancellationToken cancellationToken);

    Task SaveAsync(QueueDto queue, CancellationToken cancellationToken);
}
