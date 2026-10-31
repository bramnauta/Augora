using Augora.Contracts;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Core.Queue;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Core;

/// <summary>Flow mode: a producer continuing with the next queue items in the same stream.</summary>
public sealed class QueueFlowTests
{
    private readonly FakeMusicProvider _provider = new();
    private readonly FakePlayer _player = new();
    private readonly QueueManager _queues;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public QueueFlowTests()
    {
        var sessions = new StreamSessionManager(TimeProvider.System);
        var players = new PlayerManager([new FakePlayerProvider(_player)], new NoEvents(), sessions, NullLogger<PlayerManager>.Instance);
        _queues = new QueueManager(
            players,
            new LibraryService([_provider], NullLogger<LibraryService>.Instance),
            new StreamResolver(new HttpClient(), NullLogger<StreamResolver>.Instance),
            sessions,
            new FakeUrls(),
            new MemoryQueueStore(),
            new NoEvents(),
            new NoUser(),
            new GroupMembership(),
            NullLogger<QueueManager>.Instance);
    }

    [Fact]
    public async Task The_stream_continues_with_the_next_item_and_the_player_learns_where_it_starts()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, following: ["fake:track:2", "fake:track:3"]);
        Assert.True(_player.Played!.Continuous);

        var next = await _provider.Context!.Continuation!.ContinueAsync(_ => true, TimeSpan.FromSeconds(183), Ct);

        Assert.Equal("fake:track:2", next?.Ref.ToString());
        Assert.Equal(1, (await _queues.GetQueueAsync(_player.Id, Ct)).CurrentIndex);
        var segment = Assert.Single(_player.Segments);
        Assert.Equal(("Track 2", TimeSpan.FromSeconds(183)), (segment.Media.Title, segment.Offset));
        Assert.Equal(_player.Played.StreamUrl, segment.Media.StreamUrl);
    }

    [Fact]
    public async Task An_item_the_producer_cannot_play_ends_the_stream_without_moving_the_queue()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, following: ["fake:track:2"]);

        var next = await _provider.Context!.Continuation!.ContinueAsync(_ => false, TimeSpan.FromSeconds(10), Ct);

        Assert.Null(next);
        Assert.Equal(0, (await _queues.GetQueueAsync(_player.Id, Ct)).CurrentIndex);
        Assert.Empty(_player.Segments);
    }

    [Fact]
    public async Task An_old_stream_cannot_move_the_queue_after_another_item_was_started()
    {
        var queue = await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, following: ["fake:track:2", "fake:track:3"]);
        var oldStream = _provider.Context!.Continuation!;
        await _queues.PlayItemAsync(_player.Id, queue.Items[2].Id, Ct);

        Assert.Null(await oldStream.ContinueAsync(_ => true, TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(2, (await _queues.GetQueueAsync(_player.Id, Ct)).CurrentIndex);
    }

    [Fact]
    public async Task A_seek_in_a_flow_stream_restarts_the_item_at_the_position()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, following: ["fake:track:2"]);

        await _queues.SeekAsync(_player.Id, TimeSpan.FromSeconds(90), Ct);

        Assert.Null(_player.SeekedTo);
        Assert.Equal(TimeSpan.FromSeconds(90), _provider.Context!.StartPosition);
        Assert.Equal(("fake:track:1", TimeSpan.FromSeconds(90)), (_player.Played!.ItemUri, _player.Played.StartPosition));
        // The player reports the position in the new stream; the item is that much further.
        Assert.Equal(TimeSpan.FromSeconds(100), _player.Played.ItemPosition(TimeSpan.FromSeconds(10)));
    }

    private sealed class FakeMusicProvider : IMusicProvider
    {
        public PlaybackContext? Context { get; private set; }

        public string Id => "fake";

        public string Name => "Fake";

        public string Type => "fake";

        public Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken) => Task.FromResult(new BrowseResult(Name, []));

        public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
            Task.FromResult<MediaItem?>(new MediaItem(item, $"Track {item.Id}", IsPlayable: true, Duration: TimeSpan.FromMinutes(3)));

        public Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult<StreamSource>(new ProducedStreamSource("audio/mpeg", (_, _) => Task.CompletedTask, Continuous: true));
        }
    }

    private sealed class FakePlayer : IPlayer, IContinuousStreamPlayer
    {
        public string Id => "fake:player";

        public string Name => "Fake player";

        public string ProviderId => "fake";

        public PlayerKind Kind => PlayerKind.Speaker;

        public string? Model => null;

        public PlayerFeatures Features => PlayerFeatures.Seek;

        public PlayerStatus Status => new(true, PlaybackState.Playing, 1, false,
            Played is { } media ? new NowPlayingDto(media.ItemUri, media.Title, null, null, false, TimeSpan.Zero, media.Duration) : null, null);

        public PlayMedia? Played { get; private set; }

        public TimeSpan? SeekedTo { get; private set; }

        public List<(PlayMedia Media, TimeSpan Offset)> Segments { get; } = [];

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded
        {
            add { }
            remove { }
        }

        public void AddSegment(PlayMedia media, TimeSpan streamOffset) => Segments.Add((media, streamOffset));

        public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
        {
            Played = media;
            Segments.Clear();
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
        {
            SeekedTo = position;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayerProvider(IPlayer player) : IPlayerProvider
    {
        public string Id => "fake";

        public string Name => "Fake";

        public IReadOnlyCollection<IPlayer> Players => [player];

        public event EventHandler<IPlayer>? PlayerAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<IPlayer>? PlayerRemoved
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeUrls : IStreamUrlProvider
    {
        public Uri GetStreamUrl(StreamSession session) => GetAbsoluteUrl(session.RelativePath);

        public Uri GetAbsoluteUrl(string relativePath) => new(new Uri("http://server/"), relativePath.TrimStart('/'));
    }

    private sealed class MemoryQueueStore : IQueueStore
    {
        private readonly Dictionary<string, QueueDto> _queues = [];

        public Task<QueueDto?> LoadAsync(string playerId, CancellationToken cancellationToken) => Task.FromResult(_queues.GetValueOrDefault(playerId));

        public Task SaveAsync(QueueDto queue, CancellationToken cancellationToken)
        {
            _queues[queue.PlayerId] = queue;
            return Task.CompletedTask;
        }
    }

    private sealed class NoEvents : IEventPublisher
    {
        public Task PlayerUpdatedAsync(PlayerDto player) => Task.CompletedTask;

        public Task PlayerRemovedAsync(string playerId) => Task.CompletedTask;

        public Task QueueUpdatedAsync(QueueDto queue) => Task.CompletedTask;

        public Task PodcastEpisodeAddedAsync(string userId, PodcastEpisodeAddedDto episode) => Task.CompletedTask;
    }

    private sealed class NoUser : ICurrentUser
    {
        public string? UserId => null;
    }
}
