using System.Collections.Concurrent;
using Augora.Contracts;
using Augora.Core.Players;
using Microsoft.Extensions.Logging;

namespace Augora.Players.Remote;

/// <summary>
/// Players that register themselves over the events hub. A player stays known after its connection
/// drops (shown as unavailable) so that its queue survives a page reload or a flaky phone connection.
/// </summary>
public sealed class RemotePlayerProvider(IRemotePlayerChannel channel, ILogger<RemotePlayerProvider> logger) : IPlayerProvider
{
    public const string ProviderId = "remote";

    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, RemotePlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connections = new();

    public string Id => ProviderId;

    public string Name => "Browser / app";

    public IReadOnlyCollection<IPlayer> Players => _players.Values.ToList();

    public event EventHandler<IPlayer>? PlayerAdded;

    public event EventHandler<IPlayer>? PlayerRemoved;

    public static string PlayerIdFor(string deviceId) => $"remote:{deviceId.Trim().ToLowerInvariant()}";

    public async Task<string> RegisterAsync(string connectionId, RegisterPlayerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceId);
        var id = PlayerIdFor(request.DeviceId);
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Browser" : request.Name.Trim();

        var added = false;
        var player = _players.GetOrAdd(id, _ =>
        {
            added = true;
            return new RemotePlayer(id, name, request.Kind, channel);
        });

        // One connection drives one player: a re-registration from another tab takes over.
        if (_connections.TryGetValue(connectionId, out var previousPlayer) && previousPlayer != id)
        {
            Disconnect(connectionId);
        }

        _connections[connectionId] = id;
        var replaced = player.Attach(connectionId, name, request.Kind, request.Ephemeral);
        if (replaced is not null)
        {
            _connections.TryRemove(replaced, out _);
            await channel.SendReleasedAsync(replaced, cancellationToken);
        }

        logger.LogInformation("Remote player {Name} ({Id}) connected", name, id);
        if (added)
        {
            PlayerAdded?.Invoke(this, player);
        }

        return id;
    }

    public void ReportState(string connectionId, RemotePlayerStateDto state)
    {
        if (_connections.TryGetValue(connectionId, out var id) && _players.TryGetValue(id, out var player) && player.ConnectionId == connectionId)
        {
            player.ReportState(state);
        }
    }

    /// <summary>Called when the user switches the player off: the player is forgotten at once.</summary>
    public void Unregister(string connectionId)
    {
        if (Detach(connectionId) is { } player)
        {
            Remove(player.Id);
        }
    }

    /// <summary>
    /// Called when the connection closes (tab closed, device unreachable). Installed apps stay known;
    /// a browser session is kept for
    /// <see cref="ReconnectGrace"/> so a reload or flaky connection keeps its queue, then forgotten.
    /// </summary>
    public void Disconnect(string connectionId)
    {
        if (Detach(connectionId) is { Ephemeral: true } player)
        {
            _ = ForgetWhenStillGoneAsync(player);
        }
    }

    /// <summary>Forgets a player completely, e.g. when a user removes an old browser.</summary>
    public bool Remove(string playerId)
    {
        if (!_players.TryRemove(playerId, out var player))
        {
            return false;
        }

        PlayerRemoved?.Invoke(this, player);
        return true;
    }

    private RemotePlayer? Detach(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var id) && _players.TryGetValue(id, out var player) && player.Detach(connectionId))
        {
            logger.LogInformation("Remote player {Name} ({Id}) disconnected", player.Name, id);
            return player;
        }

        return null;
    }

    private async Task ForgetWhenStillGoneAsync(RemotePlayer player)
    {
        await Task.Delay(ReconnectGrace);
        if (player.ConnectionId is null && player.DisconnectedAt is { } since && DateTimeOffset.UtcNow - since >= ReconnectGrace && Remove(player.Id))
        {
            logger.LogInformation("Remote player {Name} ({Id}) did not return and was removed", player.Name, player.Id);
        }
    }
}
