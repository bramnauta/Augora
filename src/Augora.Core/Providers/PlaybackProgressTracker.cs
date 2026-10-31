using System.Collections.Concurrent;
using Augora.Contracts;
using Augora.Core.Media;
using Augora.Core.Players;
using Augora.Core.Queue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Augora.Core.Providers;

/// <summary>
/// Follows what the players play and hands the position of items of an <see cref="IPlaybackProgressProvider"/> to it, on behalf
/// of the user who started playback, so the item continues there next time.
/// </summary>
public sealed class PlaybackProgressTracker : IHostedService
{
    /// <summary>The position is saved again when it moved this far since the last save.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

    /// <summary>Positions before this are ignored: a player that still has to seek to the resume point reports the start.</summary>
    private static readonly TimeSpan MinPosition = TimeSpan.FromSeconds(5);

    private readonly PlayerManager _players;
    private readonly QueueManager _queues;
    private readonly LibraryService _library;
    private readonly ILogger<PlaybackProgressTracker> _logger;
    private readonly ConcurrentDictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public PlaybackProgressTracker(PlayerManager players, QueueManager queues, LibraryService library, ILogger<PlaybackProgressTracker> logger)
    {
        _players = players;
        _queues = queues;
        _library = library;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _players.StatusChanged += OnStatusChanged;
        _players.PlaybackEnded += OnPlaybackEnded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _players.StatusChanged -= OnStatusChanged;
        _players.PlaybackEnded -= OnPlaybackEnded;
        return Task.CompletedTask;
    }

    private void OnStatusChanged(object? sender, IPlayer player)
    {
        Save? save = null;
        lock (_lock)
        {
            var status = player.Status;
            var current = CurrentItem(player.Id, status);
            _tracked.TryGetValue(player.Id, out var tracked);

            // Another item took over (or playback stopped): the last position of the previous one is its resume point.
            if (tracked is { Active: true } && (current is null || current.Value.Item != tracked.Item))
            {
                save = tracked.ToSave(ended: false);
                tracked = tracked with { Active = false, SavedPosition = tracked.Position };
                _tracked[player.Id] = tracked;
            }

            if (current is not { } now)
            {
                RunSave(save);
                return;
            }

            if (tracked is null || tracked.Item != now.Item)
            {
                tracked = new Tracked(now.Item, now.UserId, TimeSpan.Zero, now.Duration, TimeSpan.Zero, Active: true);
            }

            tracked = tracked with { Active = true, Duration = now.Duration ?? tracked.Duration, Position = now.Position ?? tracked.Position };
            var paused = status.State == PlaybackState.Paused;
            if (save is null && (paused || (tracked.Position - tracked.SavedPosition).Duration() >= SaveInterval) && tracked.Position != tracked.SavedPosition)
            {
                save = tracked.ToSave(ended: false);
                tracked = tracked with { SavedPosition = tracked.Position };
            }

            _tracked[player.Id] = tracked;
        }

        RunSave(save);
    }

    private void OnPlaybackEnded(object? sender, (IPlayer Player, PlaybackEndReason Reason) e)
    {
        Save? save = null;
        lock (_lock)
        {
            // The status may already be idle: the last item is still known.
            if (e.Reason == PlaybackEndReason.Finished && _tracked.TryRemove(e.Player.Id, out var tracked))
            {
                save = tracked.ToSave(ended: true);
            }
        }

        RunSave(save);
    }

    /// <summary>The item the player plays that has progress to keep, with the user who started it.</summary>
    private (MediaItemRef Item, string UserId, TimeSpan? Position, TimeSpan? Duration)? CurrentItem(string playerId, PlayerStatus status)
    {
        if (status.State is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering)
            || status.ExternalSource is not null
            || status.NowPlaying is not { IsLive: false, ItemUri: { } uri } nowPlaying
            || !MediaItemRef.TryParse(uri, out var item)
            || _queues.GetPlaybackUser(playerId) is not { } userId
            || ProviderFor(item) is not { } provider
            || !provider.TracksProgress(item))
        {
            return null;
        }

        return (item, userId, nowPlaying.Position, nowPlaying.Duration);
    }

    private IPlaybackProgressProvider? ProviderFor(MediaItemRef item) =>
        _library.Providers.FirstOrDefault(p => string.Equals(p.Id, item.Provider, StringComparison.OrdinalIgnoreCase)) as IPlaybackProgressProvider;

    private void RunSave(Save? save)
    {
        if (save is null || (!save.Ended && save.Position < MinPosition) || ProviderFor(save.Item) is not { } provider)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await provider.SaveProgressAsync(save.Item, save.UserId, save.Position, save.Duration, save.Ended, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving the progress of {Item} failed", save.Item);
            }
        });
    }

    private sealed record Save(MediaItemRef Item, string UserId, TimeSpan Position, TimeSpan? Duration, bool Ended);

    private sealed record Tracked(MediaItemRef Item, string UserId, TimeSpan Position, TimeSpan? Duration, TimeSpan SavedPosition, bool Active)
    {
        public Save ToSave(bool ended) => new(Item, UserId, Position, Duration, ended);
    }
}
