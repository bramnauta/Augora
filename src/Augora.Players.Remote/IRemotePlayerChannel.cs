using Augora.Contracts;

namespace Augora.Players.Remote;

/// <summary>
/// Delivers commands to the connection of a remote player. Implemented by the host on top of SignalR.
/// </summary>
public interface IRemotePlayerChannel
{
    Task SendCommandAsync(string connectionId, RemotePlayerCommandDto command, CancellationToken cancellationToken);

    Task SendReleasedAsync(string connectionId, CancellationToken cancellationToken);
}
