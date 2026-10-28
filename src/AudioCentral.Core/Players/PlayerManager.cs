using System.Collections.Concurrent;
using AudioCentral.Contracts;
using AudioCentral.Core.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Core.Players;

/// <summary>
/// The registry of all players across all player providers.
/// </summary>
public sealed class PlayerManager : IHostedService
{
    private readonly ConcurrentDictionary<string, IPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEventPublisher _events;
    private readonly ILogger<PlayerManager> _logger;

    public PlayerManager(IEnumerable<IPlayerProvider> providers, IEventPublisher events, ILogger<PlayerManager> logger)
    {
        _events = events;
        _logger = logger;

        foreach (var provider in providers)
        {
            provider.PlayerAdded += (_, player) => Add(player);
            provider.PlayerRemoved += (_, player) => Remove(player);
            foreach (var player in provider.Players)
            {
                Add(player);
            }
        }
    }

    /// <summary>Raised when media on a player ended by itself; the queue uses this to advance.</summary>
    public event EventHandler<(IPlayer Player, PlaybackEndReason Reason)>? PlaybackEnded;

    public IReadOnlyCollection<IPlayer> Players => _players.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public IPlayer Get(string id) =>
        _players.TryGetValue(id, out var player)
            ? player
            : throw new AudioCentralException(ErrorCodes.PlayerNotFound, $"Player '{id}' does not exist.", 404);

    public IPlayer GetAvailable(string id)
    {
        var player = Get(id);
        return player.Status.Available
            ? player
            : throw new AudioCentralException(ErrorCodes.PlayerUnavailable, $"Player '{player.Name}' is not available.", 409);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Add(IPlayer player)
    {
        if (!_players.TryAdd(player.Id, player))
        {
            return;
        }

        _logger.LogInformation("Player {Name} ({Id}) added", player.Name, player.Id);
        player.StatusChanged += OnStatusChanged;
        player.PlaybackEnded += OnPlaybackEnded;
        Publish(_events.PlayerUpdatedAsync(player.ToDto()));
    }

    private void Remove(IPlayer player)
    {
        if (!_players.TryRemove(player.Id, out _))
        {
            return;
        }

        _logger.LogInformation("Player {Name} ({Id}) removed", player.Name, player.Id);
        player.StatusChanged -= OnStatusChanged;
        player.PlaybackEnded -= OnPlaybackEnded;
        Publish(_events.PlayerRemovedAsync(player.Id));
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (sender is IPlayer player)
        {
            Publish(_events.PlayerUpdatedAsync(player.ToDto()));
        }
    }

    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (sender is IPlayer player)
        {
            PlaybackEnded?.Invoke(this, (player, e.Reason));
        }
    }

    private async void Publish(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publishing player event failed");
        }
    }
}

public static class PlayerExtensions
{
    public static PlayerDto ToDto(this IPlayer player)
    {
        var status = player.Status;
        return new PlayerDto(
            player.Id,
            player.Name,
            player.ProviderId,
            player.Kind,
            status.Available,
            status.State,
            status.Volume,
            status.Muted,
            player.Features,
            status.NowPlaying,
            status.ExternalSource,
            player.Model);
    }
}
