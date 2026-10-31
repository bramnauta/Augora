using Augora.Contracts;
using Augora.Core;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Core.Queue;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Core;

/// <summary>Autoplay (track radio): at the end of the queue similar tracks of the provider keep it going.</summary>
public sealed class AutoplayTests
{
    private readonly FakeMusicProvider _provider = new();
    private readonly FakePlayer _player = new();
    private readonly FakeSimilarSource _source = new();
    private readonly QueueManager _queues;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AutoplayTests()
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
            NullLogger<QueueManager>.Instance,
            _source);
    }

    [Fact]
    public async Task Tracks_from_the_similar_source_are_looked_up_in_the_provider_and_preferred()
    {
        _source.Similar = [new("Artist B", "Song B"), new("Artist C", "Song C"), new("Unknown", "Nope")];
        _provider.Known = ["Song B", "Song C"];
        _provider.Similar = ["fake:track:8"];
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, autoplay: true);

        _player.End();
        await WaitForAsync(() => _player.Played.Count == 2);

        var queue = await _queues.GetQueueAsync(_player.Id, Ct);
        Assert.Equal(["fake:track:1", "fake:track:song-b", "fake:track:song-c"], queue.Items.Select(i => i.Item.Uri));
        Assert.Equal(("Artist 1", "Track 1"), _source.Asked);
    }

    [Fact]
    public async Task The_provider_chooses_when_the_similar_source_finds_nothing_usable()
    {
        _source.Similar = [new("Unknown", "Nope")];
        _provider.Similar = ["fake:track:8"];
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, autoplay: true);

        _player.End();
        await WaitForAsync(() => _player.Played.Count == 2);

        Assert.Equal("fake:track:8", _player.Played[1].ItemUri);
    }

    [Fact]
    public async Task A_similar_source_without_a_key_is_not_asked()
    {
        _source.Configured = false;
        _provider.Similar = ["fake:track:8"];
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, autoplay: true);

        _player.End();
        await WaitForAsync(() => _player.Played.Count == 2);

        Assert.Null(_source.Asked);
    }

    [Fact]
    public void Last_fm_answers_are_read()
    {
        using var ok = System.Text.Json.JsonDocument.Parse(
            """{"similartracks":{"track":[{"name":"Song","artist":{"name":"Band"}},{"name":"","artist":{"name":"X"}}]}}""");
        using var error = System.Text.Json.JsonDocument.Parse("""{"error":6,"message":"Track not found"}""");

        Assert.Equal([new SimilarTrack("Band", "Song")], LastFmClient.Parse(ok.RootElement));
        Assert.Empty(LastFmClient.Parse(error.RootElement));
    }

    private sealed class FakeSimilarSource : ISimilarTrackSource
    {
        public bool Configured { get; set; } = true;

        public bool IsConfigured => Configured;

        public SimilarTrack[] Similar { get; set; } = [];

        public (string Artist, string Title)? Asked { get; private set; }

        public Task<IReadOnlyList<SimilarTrack>> GetSimilarAsync(string artist, string title, int limit, CancellationToken cancellationToken)
        {
            Asked = (artist, title);
            return Task.FromResult<IReadOnlyList<SimilarTrack>>(Similar);
        }
    }

    [Fact]
    public async Task At_the_end_of_the_queue_similar_tracks_are_added_and_played()
    {
        _provider.Similar = ["fake:track:8", "fake:track:9"];
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, autoplay: true);

        _player.End();
        await WaitForAsync(() => _player.Played.Count == 2);

        var queue = await _queues.GetQueueAsync(_player.Id, Ct);
        Assert.Equal(["fake:track:1", "fake:track:8", "fake:track:9"], queue.Items.Select(i => i.Item.Uri));
        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal("fake:track:8", _player.Played[1].ItemUri);
        Assert.Equal(["fake:track:1"], _provider.Excluded);
    }

    [Fact]
    public async Task Without_autoplay_the_queue_just_ends()
    {
        _provider.Similar = ["fake:track:8"];
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct);

        _player.End();
        await Task.Delay(100, Ct);

        Assert.Single(_player.Played);
        Assert.Single((await _queues.GetQueueAsync(_player.Id, Ct)).Items);
    }

    [Fact]
    public async Task Autoplay_stops_when_the_provider_has_no_similar_tracks()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, autoplay: true);

        _player.End();
        await Task.Delay(100, Ct);

        Assert.Single(_player.Played);
    }

    [Fact]
    public async Task Next_at_the_end_of_the_queue_also_continues_with_autoplay()
    {
        _provider.Similar = ["fake:track:8"];
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, autoplay: true);

        await _queues.NextAsync(_player.Id, Ct);

        Assert.Equal("fake:track:8", _player.Played[^1].ItemUri);
    }

    [Fact]
    public async Task Autoplay_can_be_switched_on_and_off_and_playing_can_set_it()
    {
        var queue = await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct);
        Assert.False(queue.Autoplay);

        Assert.True((await _queues.SetAutoplayAsync(_player.Id, true, Ct)).Autoplay);
        // Playing without a preference leaves it as it is; with one it is set.
        Assert.True((await _queues.PlayAsync(_player.Id, "fake:track:2", QueueOption.Replace, Ct)).Autoplay);
        Assert.False((await _queues.PlayAsync(_player.Id, "fake:track:3", QueueOption.Replace, Ct, autoplay: false)).Autoplay);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }

    private sealed class FakeMusicProvider : IMusicProvider
    {
        public string Id => "fake";

        public string Name => "Fake";

        public string Type => "fake";

        public string[] Similar { get; set; } = [];

        public List<string> Excluded { get; } = [];

        public Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken) => Task.FromResult(new BrowseResult(Name, []));

        public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public string[] Known { get; set; } = [];

        public Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
            Task.FromResult<MediaItem?>(new MediaItem(item, $"Track {item.Id}", $"Artist {item.Id}", IsPlayable: true, Duration: TimeSpan.FromMinutes(3)));

        public Task<MediaItem?> FindTrackAsync(string artist, string title, CancellationToken cancellationToken) =>
            Task.FromResult<MediaItem?>(Known.Contains(title)
                ? new MediaItem(MediaItemRef.Parse($"fake:track:{title.ToLowerInvariant().Replace(' ', '-')}"), title, IsPlayable: true)
                : null);

        public Task<IReadOnlyList<MediaItem>> GetSimilarAsync(MediaItem seed, int count, IReadOnlySet<MediaItemRef> exclude, CancellationToken cancellationToken)
        {
            Excluded.AddRange(exclude.Select(e => e.ToString()));
            IReadOnlyList<MediaItem> items = Similar.Select(uri => new MediaItem(MediaItemRef.Parse(uri), uri, IsPlayable: true)).ToList();
            return Task.FromResult(items);
        }

        public Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken) =>
            Task.FromResult<StreamSource>(new ProducedStreamSource("audio/mpeg", (_, _) => Task.CompletedTask));
    }

    private sealed class FakePlayer : IPlayer
    {
        public string Id => "fake:player";

        public string Name => "Fake player";

        public string ProviderId => "fake";

        public PlayerKind Kind => PlayerKind.Speaker;

        public string? Model => null;

        public PlayerFeatures Features => PlayerFeatures.None;

        public PlayerStatus Status => new(true, PlaybackState.Playing, 1, false, null, null);

        public List<PlayMedia> Played { get; } = [];

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

        public void End() => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(PlaybackEndReason.Finished));

        public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
        {
            Played.Add(media);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => Task.CompletedTask;
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
