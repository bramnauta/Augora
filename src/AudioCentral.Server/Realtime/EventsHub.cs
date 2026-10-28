using AudioCentral.Contracts;
using AudioCentral.Core.Events;
using Microsoft.AspNetCore.SignalR;

namespace AudioCentral.Server.Realtime;

/// <summary>
/// Realtime channel for UI, Android and Home Assistant, mapped at <see cref="EventsHub.Path"/>.
/// Browser and Android players will also register themselves here (see docs/architecture.md).
/// </summary>
public sealed class AudioCentralHub : Hub;

internal sealed class SignalREventPublisher(IHubContext<AudioCentralHub> hub) : IEventPublisher
{
    public Task PlayerUpdatedAsync(PlayerDto player) => hub.Clients.All.SendAsync(EventsHub.PlayerUpdated, player);

    public Task PlayerRemovedAsync(string playerId) => hub.Clients.All.SendAsync(EventsHub.PlayerRemoved, playerId);

    public Task QueueUpdatedAsync(QueueDto queue) => hub.Clients.All.SendAsync(EventsHub.QueueUpdated, queue);
}
