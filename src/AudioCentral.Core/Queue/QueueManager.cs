using System.Collections.Concurrent;
using AudioCentral.Contracts;
using AudioCentral.Core.Events;
using AudioCentral.Core.Media;
using AudioCentral.Core.Players;
using AudioCentral.Core.Providers;
using AudioCentral.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Core.Queue;

/// <summary>
/// Owns the queue of every player: resolves items to streams, tells players what to play and
/// advances when an item finishes.
/// </summary>
public sealed class QueueManager
{
    private readonly ConcurrentDictionary<string, PlayerQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlayerManager _players;
    private readonly LibraryService _library;
    private readonly StreamResolver _resolver;
    private readonly StreamSessionManager _sessions;
    private readonly IStreamUrlProvider _urls;
    private readonly IQueueStore _store;
    private readonly IEventPublisher _events;
    private readonly ILogger<QueueManager> _logger;

    public QueueManager(
        PlayerManager players,
        LibraryService library,
        StreamResolver resolver,
        StreamSessionManager sessions,
        IStreamUrlProvider urls,
        IQueueStore store,
        IEventPublisher events,
        ILogger<QueueManager> logger)
    {
        _players = players;
        _library = library;
        _resolver = resolver;
        _sessions = sessions;
        _urls = urls;
        _store = store;
        _events = events;
        _logger = logger;

        _players.PlaybackEnded += OnPlaybackEnded;
    }

    public Task<QueueDto> GetQueueAsync(string playerId, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, (queue, _) => Task.FromResult(queue.ToDto()), persist: false, cancellationToken);

    public Task<QueueDto> PlayAsync(string playerId, string uri, QueueOption option, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            var player = _players.GetAvailable(playerId);
            var item = await _library.GetItemAsync(MediaItemRef.Parse(uri), ct);
            if (!item.IsPlayable)
            {
                throw new AudioCentralException(ErrorCodes.ItemNotPlayable, $"'{item.Name}' cannot be played.");
            }

            if (queue.Enqueue([item], option) is { } index)
            {
                await PlayIndexAsync(player, queue, index, ct);
            }

            return queue.ToDto();
        }, persist: true, cancellationToken);

    public Task<QueueDto> PlayItemAsync(string playerId, string queueItemId, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            var index = queue.Items.ToList().FindIndex(i => i.Id == queueItemId);
            if (index < 0)
            {
                throw new AudioCentralException(ErrorCodes.ItemNotFound, $"Queue item '{queueItemId}' does not exist.", 404);
            }

            await PlayIndexAsync(_players.GetAvailable(playerId), queue, index, ct);
            return queue.ToDto();
        }, persist: true, cancellationToken);

    /// <summary>Resumes a paused player, or restarts the current queue item when the player is idle (e.g. stopped live radio).</summary>
    public Task<QueueDto> ResumeAsync(string playerId, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            var player = _players.GetAvailable(playerId);
            if (player.Status.State == PlaybackState.Paused)
            {
                await player.ResumeAsync(ct);
            }
            else if (player.Status.State == PlaybackState.Idle && (queue.CurrentIndex ?? (queue.Items.Count > 0 ? 0 : null)) is { } index)
            {
                await PlayIndexAsync(player, queue, index, ct);
            }

            return queue.ToDto();
        }, persist: false, cancellationToken);

    public Task<QueueDto> NextAsync(string playerId, CancellationToken cancellationToken) =>
        MoveAsync(playerId, q => q.GetNextIndex(), cancellationToken);

    public Task<QueueDto> PreviousAsync(string playerId, CancellationToken cancellationToken) =>
        MoveAsync(playerId, q => q.GetPreviousIndex(), cancellationToken);

    public Task<QueueDto> SetShuffleAsync(string playerId, bool shuffle, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, (queue, _) =>
        {
            queue.SetShuffle(shuffle);
            return Task.FromResult(queue.ToDto());
        }, persist: true, cancellationToken);

    public Task<QueueDto> SetRepeatAsync(string playerId, RepeatMode repeat, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, (queue, _) =>
        {
            queue.Repeat = repeat;
            return Task.FromResult(queue.ToDto());
        }, persist: true, cancellationToken);

    public Task<QueueDto> RemoveAsync(string playerId, string queueItemId, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, (queue, _) =>
        {
            queue.Remove(queueItemId);
            return Task.FromResult(queue.ToDto());
        }, persist: true, cancellationToken);

    public Task<QueueDto> ClearAsync(string playerId, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, (queue, _) =>
        {
            queue.Clear();
            return Task.FromResult(queue.ToDto());
        }, persist: true, cancellationToken);

    private Task<QueueDto> MoveAsync(string playerId, Func<PlayerQueue, int?> selectIndex, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            if (selectIndex(queue) is { } index)
            {
                await PlayIndexAsync(_players.GetAvailable(playerId), queue, index, ct);
            }

            return queue.ToDto();
        }, persist: true, cancellationToken);

    private async Task PlayIndexAsync(IPlayer player, PlayerQueue queue, int index, CancellationToken cancellationToken)
    {
        queue.SetCurrent(index);
        var item = queue.Items[index].Item;
        var provider = _library.GetProvider(item.Ref.Provider);

        ResolvedStream stream;
        try
        {
            stream = await provider.ResolveStreamAsync(item.Ref, cancellationToken) switch
            {
                HttpStreamSource http => await _resolver.ResolveAsync(http, cancellationToken),
                var other => throw new NotSupportedException($"Stream source {other.GetType().Name} is not supported yet."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or NotSupportedException)
        {
            throw new AudioCentralException(ErrorCodes.PlaybackFailed, $"Could not open the stream for '{item.Name}': {ex.Message}", 502, ex);
        }

        Uri url;
        string path;
        if (stream.Direct)
        {
            url = stream.Url;
            path = stream.Url.ToString();
        }
        else
        {
            var session = _sessions.Create(player.Id, stream);
            url = _urls.GetStreamUrl(session);
            path = session.RelativePath;
        }

        _logger.LogInformation("Playing {Item} on {Player} via {Url}", item.Ref, player.Name, url);

        await player.PlayAsync(
            new PlayMedia(url, path, stream.ContentType, stream.IsLive, item.Ref.ToString(), item.Name, item.Subtitle, item.ImageUrl, item.Duration),
            cancellationToken);
    }

    private async void OnPlaybackEnded(object? sender, (IPlayer Player, PlaybackEndReason Reason) e)
    {
        try
        {
            await WithQueueAsync(e.Player.Id, async (queue, ct) =>
            {
                if (e.Reason == PlaybackEndReason.Error || queue.GetNextIndexAfterEnd() is not { } next)
                {
                    return queue.ToDto();
                }

                await PlayIndexAsync(e.Player, queue, next, ct);
                return queue.ToDto();
            }, persist: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advancing the queue of {Player} failed", e.Player.Name);
        }
    }

    private async Task<QueueDto> WithQueueAsync(
        string playerId,
        Func<PlayerQueue, CancellationToken, Task<QueueDto>> action,
        bool persist,
        CancellationToken cancellationToken)
    {
        var player = _players.Get(playerId);
        var gate = _locks.GetOrAdd(player.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_queues.TryGetValue(player.Id, out var queue))
            {
                var stored = await _store.LoadAsync(player.Id, cancellationToken);
                queue = stored is null ? new PlayerQueue(player.Id) : PlayerQueue.FromDto(stored);
                _queues[player.Id] = queue;
            }

            try
            {
                return await action(queue, cancellationToken);
            }
            finally
            {
                if (persist)
                {
                    var dto = queue.ToDto();
                    await _store.SaveAsync(dto, CancellationToken.None);
                    await _events.QueueUpdatedAsync(dto);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
