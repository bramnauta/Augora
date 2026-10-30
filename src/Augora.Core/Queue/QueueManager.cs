using System.Collections.Concurrent;
using Augora.Contracts;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace Augora.Core.Queue;

/// <summary>
/// Owns the queue of every player: resolves items to streams, tells players what to play and
/// advances when an item finishes.
/// </summary>
public sealed class QueueManager
{
    private const int MaxFollowing = 500;
    private const int AutoplayBatch = 10;
    private const int SimilarCandidates = 15;
    private static readonly TimeSpan MaxAnnouncement = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MinResumeSeek = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, PlayerQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISimilarTrackSource? _similarSource;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _announceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _announcing = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlayerManager _players;
    private readonly LibraryService _library;
    private readonly StreamResolver _resolver;
    private readonly StreamSessionManager _sessions;
    private readonly IStreamUrlProvider _urls;
    private readonly IQueueStore _store;
    private readonly IEventPublisher _events;
    private readonly ILogger<QueueManager> _logger;
    private readonly ICurrentUser _currentUser;
    private readonly GroupMembership _membership;
    private readonly ConcurrentDictionary<string, string> _lastUsers = new(StringComparer.OrdinalIgnoreCase);

    public QueueManager(
        PlayerManager players,
        LibraryService library,
        StreamResolver resolver,
        StreamSessionManager sessions,
        IStreamUrlProvider urls,
        IQueueStore store,
        IEventPublisher events,
        ICurrentUser currentUser,
        GroupMembership membership,
        ILogger<QueueManager> logger,
        ISimilarTrackSource? similarSource = null)
    {
        _similarSource = similarSource;
        _currentUser = currentUser;
        _membership = membership;
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

    /// <param name="takeoverPlayerId">Player whose provider resource (Spotify account) may be taken when none is free.</param>
    /// <param name="following">"Play from here": items queued after <paramref name="uri"/>; unknown or unplayable ones are skipped.</param>
    public Task<QueueDto> PlayAsync(
        string playerId,
        string uri,
        QueueOption option,
        CancellationToken cancellationToken,
        string? takeoverPlayerId = null,
        IReadOnlyList<string>? following = null,
        bool? autoplay = null) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            var player = _players.GetAvailable(playerId);
            var item = await _library.GetItemAsync(MediaItemRef.Parse(uri), ct);
            var items = (await _library.GetQueueItemsAsync(item, ct)).ToList();
            if (items.Count == 0)
            {
                throw new AugoraException(ErrorCodes.ItemNotPlayable, $"'{item.Name}' cannot be played.");
            }

            foreach (var next in (following ?? []).Take(MaxFollowing))
            {
                if (!MediaItemRef.TryParse(next, out var reference))
                {
                    continue;
                }

                try
                {
                    var followingItem = await _library.GetItemAsync(reference, ct);
                    if (followingItem.IsPlayable && !followingItem.IsBrowsable)
                    {
                        items.Add(followingItem);
                    }
                }
                catch (AugoraException ex)
                {
                    _logger.LogDebug(ex, "Skipping {Uri} after {Item}", next, uri);
                }
            }

            if (autoplay is { } enabled)
            {
                queue.Autoplay = enabled;
            }

            if (queue.Enqueue(items, option) is { } index)
            {
                await PlayIndexAsync(player, queue, index, ct, takeoverPlayerId);
            }

            return queue.ToDto();
        }, persist: true, cancellationToken);

    public Task<QueueDto> PlayItemAsync(string playerId, string queueItemId, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            var index = queue.Items.ToList().FindIndex(i => i.Id == queueItemId);
            if (index < 0)
            {
                throw new AugoraException(ErrorCodes.ItemNotFound, $"Queue item '{queueItemId}' does not exist.", 404);
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

    /// <summary>
    /// Seeks in the current item. Players seek seekable streams themselves; a produced stream without a known length
    /// (flow mode) is started again at <paramref name="position"/>.
    /// </summary>
    public Task<QueueDto> SeekAsync(string playerId, TimeSpan position, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            var player = _players.GetAvailable(playerId);
            if (!player.Features.HasFlag(PlayerFeatures.Seek) || player.Status.NowPlaying is not { IsLive: false } nowPlaying)
            {
                throw new AugoraException(ErrorCodes.NotSeekable, $"'{player.Name}' cannot seek in what it plays.", 409);
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }
            else if (nowPlaying.Duration is { } duration && position > duration)
            {
                position = duration;
            }

            var stream = _sessions.GetForPlayer(player.Id)?.Stream;
            if (stream is ProducedStreamSource { IsSeekable: false })
            {
                // Flow mode makes the next item current just before the audio gets there: seek in the item that is heard.
                var index = queue.CurrentIndex is { } current
                    ? Enumerable.Range(0, current + 1).Reverse().Where(i => queue.Items[i].Item.Ref.ToString() == nowPlaying.ItemUri).Cast<int?>().FirstOrDefault() ?? current
                    : throw new AugoraException(ErrorCodes.NotSeekable, $"'{player.Name}' has no current queue item.", 409);

                _logger.LogInformation("Seek on {Player}: restarting the stream at {Position}", player.Name, position);
                await PlayIndexAsync(player, queue, index, ct, startPosition: position);
            }
            else
            {
                await player.SeekAsync(position, ct);
            }

            return queue.ToDto();
        }, persist: false, cancellationToken);

    /// <summary>
    /// Plays <see cref="AnnounceRequest.Url"/> on the player (or group) and returns when it has ended. The queue is left alone:
    /// afterwards the volume goes back and, when asked and the player was playing a queue item, that item continues where it was.
    /// </summary>
    public async Task AnnounceAsync(string playerId, AnnounceRequest request, CancellationToken cancellationToken)
    {
        var player = _players.GetAvailable(playerId);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new AugoraException(ErrorCodes.ItemNotPlayable, "The announcement must be an absolute http(s) url.", 400);
        }

        if (request.Volume is { } requested && (!double.IsFinite(requested) || requested is < 0 or > 1))
        {
            throw new AugoraException(ErrorCodes.ItemNotPlayable, "The volume must be between 0 and 1.", 400);
        }

        if (_membership.GetGroup(player.Id) is { } group)
        {
            throw new AugoraException(ErrorCodes.PlayerUnavailable, $"'{player.Name}' plays as a member of a group; announce on the group ({group}).", 409);
        }

        // One announcement at a time per player; the next one waits for this one to finish.
        var gate = _announceLocks.GetOrAdd(player.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        _announcing[player.Id] = 0;
        try
        {
            ResumePoint? resume = null;
            if (request.Resume)
            {
                await WithQueueAsync(player.Id, (queue, _) =>
                {
                    resume = ResumePointOf(player, queue);
                    return Task.FromResult(queue.ToDto());
                }, persist: false, cancellationToken);
            }

            var volume = request.Volume is not null && player.Features.HasFlag(PlayerFeatures.Volume) ? player.Status.Volume : (double?)null;
            try
            {
                if (volume is not null)
                {
                    await player.SetVolumeAsync(request.Volume!.Value, cancellationToken);
                }

                await PlayAnnouncementAsync(player, url, request.ContentType ?? "audio/mpeg", cancellationToken);
            }
            finally
            {
                if (volume is { } original)
                {
                    await RestoreAsync(() => player.SetVolumeAsync(original, CancellationToken.None), player, "volume");
                }
            }

            if (resume is { } point)
            {
                await RestoreAsync(() => ResumeAfterAnnouncementAsync(player, point), player, "playback");
            }
        }
        finally
        {
            _announcing.TryRemove(player.Id, out _);
            gate.Release();
        }
    }

    private async Task PlayAnnouncementAsync(IPlayer player, Uri url, string contentType, CancellationToken cancellationToken)
    {
        // A group reads the source of its own session, also for a stream the members fetch themselves.
        _sessions.Create(player.Id, new ResolvedStream(url, contentType, false, Direct: true));
        var media = new PlayMedia(url, url.ToString(), contentType, false, "augora:announcement", "Announcement", null, null, null);

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded(object? sender, PlaybackEndedEventArgs e) => ended.TrySetResult();
        player.PlaybackEnded += OnEnded;
        try
        {
            _logger.LogInformation("Announcing {Url} on {Player}", url, player.Name);
            await player.PlayAsync(media, cancellationToken);
            await ended.Task.WaitAsync(MaxAnnouncement, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The announcement on {Player} did not end within {Limit}; stopping it", player.Name, MaxAnnouncement);
            await player.StopAsync(CancellationToken.None);
        }
        finally
        {
            player.PlaybackEnded -= OnEnded;
        }
    }

    /// <summary>What the player plays from its queue right now, or null when there is nothing to continue (idle, paused, another app).</summary>
    private ResumePoint? ResumePointOf(IPlayer player, PlayerQueue queue)
    {
        var status = player.Status;
        if (status.State is not (PlaybackState.Playing or PlaybackState.Buffering) || status.ExternalSource is not null
            || status.NowPlaying is not { } nowPlaying || queue.CurrentIndex is not { } current)
        {
            return null;
        }

        // Flow mode makes the next item current just before the audio gets there: continue with the item that is heard.
        var index = Enumerable.Range(0, current + 1).Reverse().Where(i => queue.Items[i].Item.Ref.ToString() == nowPlaying.ItemUri).Cast<int?>().FirstOrDefault() ?? current;
        return new ResumePoint(queue.Items[index].Id, nowPlaying.IsLive ? null : nowPlaying.Position, _sessions.GetForPlayer(player.Id)?.Stream);
    }

    private Task<QueueDto> ResumeAfterAnnouncementAsync(IPlayer player, ResumePoint point) =>
        WithQueueAsync(player.Id, async (queue, ct) =>
        {
            // The queue may have been changed while the announcement played: then the user has moved on.
            var index = queue.Items.ToList().FindIndex(i => i.Id == point.QueueItemId);
            if (index < 0 || queue.CurrentIndex != index)
            {
                return queue.ToDto();
            }

            // Streams that cannot seek themselves (Spotify in flow mode) restart at the position; the others are sought afterwards.
            var restartsAt = point.Stream is ProducedStreamSource { IsSeekable: false } && point.Position is { } at ? at : TimeSpan.Zero;
            await PlayIndexAsync(player, queue, index, ct, startPosition: restartsAt);
            if (restartsAt == TimeSpan.Zero && point.Position is { } position && position > MinResumeSeek && player.Features.HasFlag(PlayerFeatures.Seek))
            {
                await player.SeekAsync(position, ct);
            }

            return queue.ToDto();
        }, persist: false, CancellationToken.None);

    private async Task RestoreAsync(Func<Task> restore, IPlayer player, string what)
    {
        try
        {
            await restore();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Restoring the {What} of {Player} after the announcement failed", what, player.Name);
        }
    }

    private sealed record ResumePoint(string QueueItemId, TimeSpan? Position, StreamSource? Stream);

    public Task<QueueDto> NextAsync(string playerId, CancellationToken cancellationToken) =>
        MoveAsync(playerId, q => q.GetNextIndex(), cancellationToken, explicitNext: true);

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

    public Task<QueueDto> SetAutoplayAsync(string playerId, bool autoplay, CancellationToken cancellationToken) =>
        WithQueueAsync(playerId, (queue, _) =>
        {
            queue.Autoplay = autoplay;
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

    /// <summary>
    /// Autoplay: at the end of the queue, adds tracks similar to the last item so there is a next one. Does nothing when
    /// autoplay is off, the queue goes on by itself, or the provider has no similar tracks (radio stations).
    /// </summary>
    /// <returns>Whether items were added.</returns>
    private async Task<bool> FillAutoplayAsync(PlayerQueue queue, bool explicitNext, CancellationToken cancellationToken)
    {
        var next = explicitNext ? queue.GetNextIndex() : queue.GetNextIndexAfterEnd();
        if (!queue.Autoplay || next is not null || queue.Current is not { } seed)
        {
            return false;
        }

        try
        {
            var provider = _library.GetProvider(seed.Item.Ref.Provider);
            var exclude = queue.Items.Select(i => i.Item.Ref).ToHashSet();
            var similar = await FindWithSimilarSourceAsync(provider, seed.Item, exclude, cancellationToken);
            if (similar.Count == 0)
            {
                similar = await provider.GetSimilarAsync(seed.Item, AutoplayBatch, exclude, cancellationToken);
            }

            var items = similar.Where(i => i.IsPlayable && !i.IsBrowsable && !exclude.Contains(i.Ref)).DistinctBy(i => i.Ref).ToList();
            if (items.Count == 0)
            {
                return false;
            }

            _logger.LogInformation("Autoplay on {Player}: {Count} tracks after {Item}", queue.PlayerId, items.Count, seed.Item.Ref);
            queue.Enqueue(items, QueueOption.Add);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or AugoraException)
        {
            _logger.LogWarning(ex, "Autoplay on {Player} could not find tracks after {Item}", queue.PlayerId, seed.Item.Ref);
            return false;
        }
    }

    /// <summary>
    /// Autoplay through an outside source (Last.fm): the tracks that go with the seed, as far as the provider has them.
    /// Empty when there is no source, it knows nothing, or it fails: the provider then chooses itself.
    /// </summary>
    private async Task<IReadOnlyList<MediaItem>> FindWithSimilarSourceAsync(
        IMusicProvider provider, MediaItem seed, HashSet<MediaItemRef> exclude, CancellationToken cancellationToken)
    {
        // The subtitle of a track starts with its artist(s): "Artist, Artist · Album".
        var artist = seed.Subtitle?.Split(" · ")[0].Split(", ")[0];
        if (_similarSource is not { IsConfigured: true } source || seed.Type != MediaType.Track || string.IsNullOrWhiteSpace(artist))
        {
            return [];
        }

        try
        {
            var candidates = await source.GetSimilarAsync(artist, seed.Name, SimilarCandidates, cancellationToken);
            using var gate = new SemaphoreSlim(4);
            var found = await Task.WhenAll(candidates.Select(async candidate =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await provider.FindTrackAsync(candidate.Artist, candidate.Title, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or AugoraException)
                {
                    _logger.LogDebug(ex, "Looking up {Artist} - {Title} failed", candidate.Artist, candidate.Title);
                    return null;
                }
                finally
                {
                    gate.Release();
                }
            }));

            return found.OfType<MediaItem>().Where(i => !exclude.Contains(i.Ref)).DistinctBy(i => i.Ref).Take(AutoplayBatch).ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "The similar tracks of {Item} could not be fetched", seed.Ref);
            return [];
        }
    }

    private Task<QueueDto> MoveAsync(string playerId, Func<PlayerQueue, int?> selectIndex, CancellationToken cancellationToken, bool explicitNext = false) =>
        WithQueueAsync(playerId, async (queue, ct) =>
        {
            if (explicitNext)
            {
                await FillAutoplayAsync(queue, explicitNext: true, ct);
            }

            if (selectIndex(queue) is { } index)
            {
                await PlayIndexAsync(_players.GetAvailable(playerId), queue, index, ct);
            }

            return queue.ToDto();
        }, persist: true, cancellationToken);

    private async Task PlayIndexAsync(
        IPlayer player,
        PlayerQueue queue,
        int index,
        CancellationToken cancellationToken,
        string? takeoverPlayerId = null,
        TimeSpan startPosition = default)
    {
        queue.SetCurrent(index);
        var item = queue.Items[index].Item;
        var provider = _library.GetProvider(item.Ref.Provider);

        // Playing on a member directly takes it out of its group.
        _membership.Leave(player.Id);

        // Remember who started playback, so the queue advancing on its own keeps using that user's accounts.
        var userId = _currentUser.UserId ?? _lastUsers.GetValueOrDefault(player.Id);
        if (userId is not null)
        {
            _lastUsers[player.Id] = userId;
        }

        var continuation = new QueueContinuation(this, player, queue.Items[index].Id);
        StreamSource stream;
        try
        {
            stream = await provider.ResolveStreamAsync(item.Ref, new PlaybackContext(player.Id, userId, takeoverPlayerId, continuation, startPosition), cancellationToken) switch
            {
                HttpStreamSource http => await _resolver.ResolveAsync(http, cancellationToken),
                var other => other,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or NotSupportedException)
        {
            throw new AugoraException(ErrorCodes.PlaybackFailed, $"Could not open the stream for '{item.Name}': {ex.Message}", 502, ex);
        }

        Uri url;
        string path;
        if (stream is ResolvedStream { Direct: true } direct)
        {
            url = direct.Url;
            path = direct.Url.ToString();
        }
        else
        {
            var session = _sessions.Create(player.Id, stream);
            url = _urls.GetStreamUrl(session);
            path = session.RelativePath;
        }

        _logger.LogInformation("Playing {Item} on {Player} via {Url}", item.Ref, player.Name, url);

        var continuous = stream is ProducedStreamSource { Continuous: true };
        var media = MediaFor(item, url, path, stream.GetContentType(), stream.IsLive, continuous) with
        {
            // Only producers start further in; other streams start at the beginning.
            StartPosition = stream is ProducedStreamSource ? startPosition : TimeSpan.Zero,
        };
        continuation.Media = media;
        await player.PlayAsync(media, cancellationToken);
    }

    private PlayMedia MediaFor(MediaItem item, Uri url, string path, string contentType, bool isLive, bool continuous)
    {
        // Server artwork (local files) is relative; Cast devices need it absolute.
        var imageUrl = item.ImageUrl is ['/', ..] relativeImage ? _urls.GetAbsoluteUrl(relativeImage).ToString() : item.ImageUrl;
        return new PlayMedia(url, path, contentType, isLive, item.Ref.ToString(), item.Name, item.Subtitle, imageUrl, item.Duration, item.ImageUrl, continuous);
    }

    /// <summary>
    /// Flow mode for one stream: moves the queue on to the next item when the producer can play it in the same stream.
    /// Does nothing once the queue changed under the stream (another item was started).
    /// </summary>
    private sealed class QueueContinuation(QueueManager manager, IPlayer player, string queueItemId) : IQueueContinuation
    {
        private string _queueItemId = queueItemId;

        /// <summary>The media of the stream as the player got it; set once the stream session exists.</summary>
        public PlayMedia? Media { get; set; }

        public async Task<MediaItem?> ContinueAsync(Func<MediaItem, bool> canPlay, TimeSpan streamOffset, CancellationToken cancellationToken)
        {
            MediaItem? next = null;
            await manager.WithQueueAsync(player.Id, async (queue, ct) =>
            {
                var current = queue.CurrentIndex is { } index ? queue.Items[index].Id : null;
                if (current == _queueItemId)
                {
                    await manager.FillAutoplayAsync(queue, explicitNext: false, ct);
                }

                if (current == _queueItemId && queue.GetNextIndexAfterEnd() is { } nextIndex && canPlay(queue.Items[nextIndex].Item))
                {
                    queue.SetCurrent(nextIndex);
                    _queueItemId = queue.Items[nextIndex].Id;
                    next = queue.Items[nextIndex].Item;
                }

                return queue.ToDto();
            }, persist: true, cancellationToken);

            if (next is not null && Media is { } media)
            {
                manager._logger.LogInformation("Flow: {Item} continues the stream on {Player} at {Offset}", next.Ref, player.Name, streamOffset);
                if (player is IContinuousStreamPlayer continuous)
                {
                    continuous.AddSegment(manager.MediaFor(next, media.StreamUrl, media.StreamPath, media.ContentType, media.IsLive, true), streamOffset);
                }
            }

            return next;
        }
    }

    private async void OnPlaybackEnded(object? sender, (IPlayer Player, PlaybackEndReason Reason) e)
    {
        if (_announcing.ContainsKey(e.Player.Id))
        {
            // The announcement ended, or the music it interrupted did: AnnounceAsync decides what continues.
            return;
        }

        if (_membership.GetGroup(e.Player.Id) is not null)
        {
            // The group's queue advances; the member's own queue stays where it was.
            return;
        }

        try
        {
            await WithQueueAsync(e.Player.Id, async (queue, ct) =>
            {
                if (e.Reason == PlaybackEndReason.Error)
                {
                    return queue.ToDto();
                }

                await FillAutoplayAsync(queue, explicitNext: false, ct);
                if (queue.GetNextIndexAfterEnd() is not { } next)
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
