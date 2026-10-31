using System.Net;
using System.Text;
using Augora.Contracts;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Providers;
using Augora.Providers.Podcasts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Podcasts;

public sealed class PodcastProviderTests
{
    private const string FeedUrl = "https://example.com/feed.xml";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Feed(params (string Guid, string Date)[] episodes) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>De Test Podcast</title>
            {string.Concat(episodes.Select(e => $"""
              <item><title>Aflevering {e.Guid}</title><guid>{e.Guid}</guid><pubDate>{e.Date}</pubDate>
              <itunes:duration>1000</itunes:duration><enclosure url="https://example.com/{e.Guid}.mp3" type="audio/mpeg" length="1" /></item>
            """))}
          </channel>
        </rss>
        """;

    private sealed class Fixture
    {
        public FakeStore Store { get; } = new();

        public FakeUser User { get; } = new() { UserId = "alice" };

        public FakeEvents Events { get; } = new();

        public FakeFeedHandler Handler { get; } = new();

        public PodcastProvider Provider { get; }

        public Fixture()
        {
            Provider = new PodcastProvider(Store, new FakeHttpClientFactory(Handler), User, Events, TimeProvider.System, NullLogger<PodcastProvider>.Instance);
        }
    }

    [Fact]
    public async Task Subscriptions_are_per_user()
    {
        var f = new Fixture();
        f.Handler.Body = Feed(("a", "Mon, 06 Jan 2025 08:00:00 GMT"));

        await f.Provider.SubscribeAsync(FeedUrl, Ct);
        Assert.Single(await f.Provider.GetSubscriptionsAsync(Ct));

        f.User.UserId = "bob";
        Assert.Empty(await f.Provider.GetSubscriptionsAsync(Ct));
        Assert.Empty((await f.Provider.BrowseAsync(null, Ct)).Items);
        var bobs = await f.Provider.SubscribeAsync(FeedUrl, Ct);
        Assert.Equal("bob", bobs.UserId);

        // Unsubscribing only removes the subscription of the current user.
        Assert.True(await f.Provider.UnsubscribeAsync(bobs.Id, Ct));
        f.User.UserId = "alice";
        Assert.Single(await f.Provider.GetSubscriptionsAsync(Ct));
    }

    [Fact]
    public async Task Another_user_cannot_unsubscribe_someone_elses_podcast()
    {
        var f = new Fixture();
        f.Handler.Body = Feed(("a", "Mon, 06 Jan 2025 08:00:00 GMT"));
        var subscription = await f.Provider.SubscribeAsync(FeedUrl, Ct);

        f.User.UserId = "bob";

        Assert.False(await f.Provider.UnsubscribeAsync(subscription.Id, Ct));
    }

    [Fact]
    public async Task New_episodes_are_announced_once_to_the_subscriber_only()
    {
        var f = new Fixture();
        f.Handler.Body = Feed(("a", "Mon, 06 Jan 2025 08:00:00 GMT"));
        await f.Provider.SubscribeAsync(FeedUrl, Ct);
        f.User.UserId = "bob";
        await f.Provider.SubscribeAsync(FeedUrl + "?other", Ct);
        f.Handler.Body = Feed(("a", "Mon, 06 Jan 2025 08:00:00 GMT"));

        await f.Provider.CheckForNewEpisodesAsync(Ct);
        Assert.Empty(f.Events.Added);

        f.Handler.Body = Feed(("b", "Mon, 13 Jan 2025 08:00:00 GMT"), ("a", "Mon, 06 Jan 2025 08:00:00 GMT"));
        await f.Provider.CheckForNewEpisodesAsync(Ct);
        await f.Provider.CheckForNewEpisodesAsync(Ct);

        // Alice and bob both follow a feed that now has a new episode; each hears about it once.
        Assert.Equal(2, f.Events.Added.Count);
        Assert.Contains(f.Events.Added, e => e.UserId == "alice" && e.Episode.EpisodeTitle == "Aflevering b");
        Assert.Contains(f.Events.Added, e => e.UserId == "bob" && e.Episode.EpisodeTitle == "Aflevering b");
    }

    [Fact]
    public async Task The_new_episodes_list_has_one_episode_per_podcast_and_leaves_out_heard_ones()
    {
        var f = new Fixture();
        f.Handler.Body = Feed(("b", "Mon, 13 Jan 2025 08:00:00 GMT"), ("a", "Mon, 06 Jan 2025 08:00:00 GMT"));
        await f.Provider.SubscribeAsync(FeedUrl, Ct);

        var list = await f.Provider.GetNewEpisodesAsync(Ct);
        var episode = Assert.Single(list);
        Assert.Equal("Aflevering b", episode.Episode.Name);
        Assert.Null(episode.Position);

        var reference = MediaItemRef.Parse(episode.Episode.Uri);
        await f.Provider.SaveProgressAsync(reference, "alice", TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1000), ended: false, Ct);
        Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(await f.Provider.GetNewEpisodesAsync(Ct)).Position);

        await f.Provider.SaveProgressAsync(reference, "alice", TimeSpan.FromSeconds(990), TimeSpan.FromSeconds(1000), ended: false, Ct);
        Assert.Empty(await f.Provider.GetNewEpisodesAsync(Ct));

        f.User.UserId = "bob";
        Assert.Empty(await f.Provider.GetNewEpisodesAsync(Ct));
    }

    [Fact]
    public async Task Progress_is_resumed_per_user_until_the_episode_is_heard()
    {
        var f = new Fixture();
        var episode = new MediaItemRef(PodcastProvider.ProviderId, MediaType.Episode, "feed/ep");

        Assert.Null(await f.Provider.GetResumePositionAsync(episode, "alice", Ct));

        await f.Provider.SaveProgressAsync(episode, "alice", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(40), ended: false, Ct);
        Assert.Equal(TimeSpan.FromMinutes(5), await f.Provider.GetResumePositionAsync(episode, "alice", Ct));
        Assert.Null(await f.Provider.GetResumePositionAsync(episode, "bob", Ct));

        await f.Provider.SaveProgressAsync(episode, "alice", TimeSpan.FromMinutes(40), null, ended: true, Ct);
        Assert.Null(await f.Provider.GetResumePositionAsync(episode, "alice", Ct));
    }

    private sealed class FakeUser : ICurrentUser
    {
        public string? UserId { get; set; }
    }

    private sealed class FakeEvents : IEventPublisher
    {
        public List<(string UserId, PodcastEpisodeAddedDto Episode)> Added { get; } = [];

        public Task PlayerUpdatedAsync(PlayerDto player) => Task.CompletedTask;

        public Task PlayerRemovedAsync(string playerId) => Task.CompletedTask;

        public Task QueueUpdatedAsync(QueueDto queue) => Task.CompletedTask;

        public Task PodcastEpisodeAddedAsync(string userId, PodcastEpisodeAddedDto episode)
        {
            Added.Add((userId, episode));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFeedHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/rss+xml") });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeStore : IPodcastStore
    {
        private readonly List<PodcastSubscription> _subscriptions = [];
        private readonly Dictionary<(string, string), PodcastProgress> _progress = [];

        public Task<IReadOnlyList<PodcastSubscription>> GetAllAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PodcastSubscription>>(_subscriptions.Where(s => s.UserId == userId).ToList());

        public Task<IReadOnlyList<PodcastSubscription>> GetEveryoneAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PodcastSubscription>>(_subscriptions.ToList());

        public Task<PodcastSubscription> AddAsync(PodcastSubscription subscription, CancellationToken cancellationToken)
        {
            _subscriptions.Add(subscription);
            return Task.FromResult(subscription);
        }

        public Task<bool> RemoveAsync(string userId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_subscriptions.RemoveAll(s => s.Id == id && s.UserId == userId) > 0);

        public Task SetLatestEpisodeAsync(Guid id, string episodeKey, DateTimeOffset? publishedAt, CancellationToken cancellationToken)
        {
            var index = _subscriptions.FindIndex(s => s.Id == id);
            _subscriptions[index] = _subscriptions[index] with { LatestEpisodeKey = episodeKey, LatestPublishedAt = publishedAt };
            return Task.CompletedTask;
        }

        public Task<PodcastProgress?> GetProgressAsync(string userId, string episodeId, CancellationToken cancellationToken) =>
            Task.FromResult(_progress.GetValueOrDefault((userId, episodeId)));

        public Task<IReadOnlyDictionary<string, PodcastProgress>> GetProgressAsync(
            string userId, IReadOnlyCollection<string> episodeIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, PodcastProgress>>(
                episodeIds.Where(id => _progress.ContainsKey((userId, id))).ToDictionary(id => id, id => _progress[(userId, id)]));

        public Task SaveProgressAsync(string userId, PodcastProgress progress, CancellationToken cancellationToken)
        {
            _progress[(userId, progress.EpisodeId)] = progress;
            return Task.CompletedTask;
        }
    }
}
