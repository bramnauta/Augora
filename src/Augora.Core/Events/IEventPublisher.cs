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
}
