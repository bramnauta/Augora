using Augora.Contracts;
using Augora.Core.Events;
using Augora.Players.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Augora.Server.Realtime;

/// <summary>
/// Realtime channel for UI, Android and Home Assistant, mapped at <see cref="EventsHub.Path"/>.
/// Clients that play audio themselves also register here as remote players.
/// </summary>
[Authorize]
public sealed class AugoraHub(RemotePlayerProvider remotePlayers) : Hub
{
    [HubMethodName(RemotePlayerHub.Register)]
    public Task<string> RegisterPlayer(RegisterPlayerRequest request) =>
        remotePlayers.RegisterAsync(Context.ConnectionId, request, Context.ConnectionAborted);

    [HubMethodName(RemotePlayerHub.Unregister)]
    public void UnregisterPlayer() => remotePlayers.Unregister(Context.ConnectionId);

    [HubMethodName(RemotePlayerHub.ReportState)]
    public void ReportPlayerState(RemotePlayerStateDto state) => remotePlayers.ReportState(Context.ConnectionId, state);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        remotePlayers.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

internal sealed class SignalREventPublisher(IHubContext<AugoraHub> hub) : IEventPublisher
{
    public Task PlayerUpdatedAsync(PlayerDto player) => hub.Clients.All.SendAsync(EventsHub.PlayerUpdated, player);

    public Task PlayerRemovedAsync(string playerId) => hub.Clients.All.SendAsync(EventsHub.PlayerRemoved, playerId);

    public Task QueueUpdatedAsync(QueueDto queue) => hub.Clients.All.SendAsync(EventsHub.QueueUpdated, queue);
}

internal sealed class SignalRRemotePlayerChannel(IHubContext<AugoraHub> hub) : IRemotePlayerChannel
{
    public Task SendCommandAsync(string connectionId, RemotePlayerCommandDto command, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendAsync(RemotePlayerHub.Command, command, cancellationToken);

    public Task SendReleasedAsync(string connectionId, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendAsync(RemotePlayerHub.Released, cancellationToken);
}
