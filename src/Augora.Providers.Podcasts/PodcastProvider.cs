using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Augora.Contracts;
using Augora.Core;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Providers;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace Augora.Providers.Podcasts;

/// <summary>
/// Podcasts from RSS feeds: subscriptions and listening progress are per user, search uses the iTunes directory.
/// </summary>
/// <remarks>
/// Ids: <c>podcast:podcast:{feed}</c> and <c>podcast:episode:{feed}/{episode}</c>, where <c>{feed}</c> is the
/// base64url feed url, so search results can be browsed without subscribing.
/// </remarks>
public sealed class PodcastProvider(
    IPodcastStore store,
    IHttpClientFactory httpClientFactory,
    ICurrentUser currentUser,
    IEventPublisher events,
    TimeProvider timeProvider,
    ILogger<PodcastProvider> logger)
    : IMusicProvider, IPlaybackProgressProvider
{
    public const string ProviderId = "podcast";
    public const string FeedClientName = "podcast-feeds";
    public const string DirectoryClientName = "podcast-directory";
    private static readonly TimeSpan FeedLifetime = TimeSpan.FromMinutes(30);
    private const int MaxEpisodes = 500;

    /// <summary>At most this many episodes are announced per podcast and check, so a feed that changed ids does not flood.</summary>
    private const int MaxAnnounced = 5;

    /// <summary>An episode with this little left counts as heard (outros, ads).</summary>
    private static readonly TimeSpan HeardMargin = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (PodcastFeed Feed, DateTimeOffset FetchedAt)> _feeds = new(StringComparer.Ordinal);

    public string Id => ProviderId;

    public string Name => "Podcasts";

    public string Type => ProviderId;

    public int SearchOrder => 50;

    public static string FeedKey(string feedUrl) => Convert.ToBase64String(Encoding.UTF8.GetBytes(feedUrl)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string PodcastUri(string feedUrl) => new MediaItemRef(ProviderId, MediaType.Podcast, FeedKey(feedUrl)).ToString();

    /// <summary>Fetches and validates a feed before subscribing the current user.</summary>
    public async Task<PodcastSubscription> SubscribeAsync(string feedUrl, CancellationToken cancellationToken)
    {
        var userId = RequireUser();
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new AugoraException(ErrorCodes.InvalidUri, "Enter the http(s) url of the podcast's RSS feed.");
        }

        var feed = await GetFeedAsync(url.ToString(), forceRefresh: true, cancellationToken);
        var existing = (await store.GetAllAsync(userId, cancellationToken)).FirstOrDefault(s => s.FeedUrl == url.ToString());

        // The episodes that exist now are not new; only what appears after subscribing is announced.
        var newest = feed.Episodes is [var first, ..] ? first : null;
        return existing ?? await store.AddAsync(
            new PodcastSubscription(Guid.CreateVersion7(), userId, url.ToString(), feed.Title, feed.Author, feed.ImageUrl, newest?.Key, newest?.PublishedAt),
            cancellationToken);
    }

    /// <summary>The subscriptions of the current user.</summary>
    public async Task<IReadOnlyList<PodcastSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId ? await store.GetAllAsync(userId, cancellationToken) : [];

    public Task<bool> UnsubscribeAsync(Guid id, CancellationToken cancellationToken) => store.RemoveAsync(RequireUser(), id, cancellationToken);

    /// <summary>
    /// The newest episode of every podcast the current user subscribed to, unless fully heard, newest first. Playing one
    /// continues where the user stopped.
    /// </summary>
    public async Task<IReadOnlyList<NewPodcastEpisodeDto>> GetNewEpisodesAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var subscriptions = await store.GetAllAsync(userId, cancellationToken);
        var latest = (await Task.WhenAll(subscriptions.Select(async subscription =>
        {
            try
            {
                var feed = await GetFeedAsync(subscription.FeedUrl, forceRefresh: false, cancellationToken);
                return feed.Episodes is [var episode, ..] ? new LatestEpisode(subscription.FeedUrl, feed, episode) : null;
            }
            catch (AugoraException ex)
            {
                logger.LogWarning(ex, "Reading podcast feed {Feed} for the new episodes failed", subscription.FeedUrl);
                return null;
            }
        }))).OfType<LatestEpisode>().ToList();

        var progress = await store.GetProgressAsync(userId, latest.Select(l => EpisodeId(l.FeedUrl, l.Episode)).ToList(), cancellationToken);
        return latest
            .Select(l => (Latest: l, Progress: progress.GetValueOrDefault(EpisodeId(l.FeedUrl, l.Episode))))
            .Where(l => l.Progress is not { Completed: true })
            .OrderByDescending(l => l.Latest.Episode.PublishedAt ?? DateTimeOffset.MinValue)
            .Select(l => new NewPodcastEpisodeDto(
                Episode(l.Latest.FeedUrl, l.Latest.Feed, l.Latest.Episode).ToDto(),
                l.Latest.Feed.Title,
                l.Latest.Episode.PublishedAt,
                l.Progress is { Position.Ticks: > 0 } p ? p.Position : null))
            .ToList();
    }

    private sealed record LatestEpisode(string FeedUrl, PodcastFeed Feed, PodcastEpisode Episode);

    /// <summary>Checks the feeds of all subscriptions and tells the subscribers about episodes published since the last check.</summary>
    public async Task CheckForNewEpisodesAsync(CancellationToken cancellationToken)
    {
        foreach (var feedSubscriptions in (await store.GetEveryoneAsync(cancellationToken)).GroupBy(s => s.FeedUrl))
        {
            PodcastFeed feed;
            try
            {
                feed = await GetFeedAsync(feedSubscriptions.Key, forceRefresh: true, cancellationToken);
            }
            catch (AugoraException ex)
            {
                logger.LogWarning(ex, "Checking podcast feed {Feed} for new episodes failed", feedSubscriptions.Key);
                continue;
            }

            if (feed.Episodes is not [var newest, ..])
            {
                continue;
            }

            foreach (var subscription in feedSubscriptions)
            {
                if (subscription.LatestEpisodeKey == newest.Key)
                {
                    continue;
                }

                // Subscriptions from before new episodes were followed only get their starting point.
                var added = subscription.LatestEpisodeKey is null
                    ? []
                    : feed.Episodes
                        .TakeWhile(e => e.Key != subscription.LatestEpisodeKey && (subscription.LatestPublishedAt is not { } since || e.PublishedAt > since))
                        .Take(MaxAnnounced)
                        .Reverse()
                        .ToList();

                await store.SetLatestEpisodeAsync(subscription.Id, newest.Key, newest.PublishedAt, cancellationToken);
                foreach (var episode in added)
                {
                    logger.LogInformation("New episode of {Podcast}: {Episode}", feed.Title, episode.Title);
                    try
                    {
                        await events.PodcastEpisodeAddedAsync(subscription.UserId, new PodcastEpisodeAddedDto(
                            PodcastUri(subscription.FeedUrl),
                            feed.Title,
                            Episode(subscription.FeedUrl, feed, episode).Ref.ToString(),
                            episode.Title,
                            episode.PublishedAt,
                            episode.Duration,
                            episode.ImageUrl ?? feed.ImageUrl));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Publishing the new episode {Episode} of {Podcast} failed", episode.Title, feed.Title);
                    }
                }
            }
        }
    }

    public bool TracksProgress(MediaItemRef item) => item.Provider == ProviderId && item.Type == MediaType.Episode;

    public async Task<TimeSpan?> GetResumePositionAsync(MediaItemRef item, string userId, CancellationToken cancellationToken) =>
        await store.GetProgressAsync(userId, item.Id, cancellationToken) is { Completed: false } progress && progress.Position > TimeSpan.Zero
            ? progress.Position
            : null;

    public Task SaveProgressAsync(MediaItemRef item, string userId, TimeSpan position, TimeSpan? duration, bool ended, CancellationToken cancellationToken)
    {
        var heard = ended || (duration is { } length && length > HeardMargin && position >= length - HeardMargin);
        return store.SaveProgressAsync(
            userId, new PodcastProgress(item.Id, heard ? TimeSpan.Zero : position, duration, heard), cancellationToken);
    }

    private string RequireUser() =>
        currentUser.UserId ?? throw new AugoraException(ErrorCodes.Forbidden, "Log in to use podcast subscriptions.", 401);

    private static string EpisodeId(string feedUrl, PodcastEpisode episode) => $"{FeedKey(feedUrl)}/{episode.Key}";

    /// <summary>Podcasts in the iTunes directory.</summary>
    public async Task<IReadOnlyList<PodcastSearchResultDto>> SearchDirectoryAsync(string query, CancellationToken cancellationToken)
    {
        var response = await httpClientFactory.CreateClient(DirectoryClientName).GetFromJsonAsync(
            $"search?media=podcast&entity=podcast&limit=25&term={Uri.EscapeDataString(query)}",
            PodcastJsonContext.Default.ItunesSearchResponse,
            cancellationToken);
        return (response?.Results ?? [])
            .Where(r => !string.IsNullOrEmpty(r.FeedUrl) && !string.IsNullOrEmpty(r.CollectionName))
            .Select(r => new PodcastSearchResultDto(r.CollectionName!, r.ArtistName, r.FeedUrl!, r.ArtworkUrl600 ?? r.ArtworkUrl100, PodcastUri(r.FeedUrl!)))
            .ToList();
    }

    public async Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken)
    {
        if (folder is null)
        {
            var subscriptions = await GetSubscriptionsAsync(cancellationToken);
            return new BrowseResult(Name, subscriptions
                .OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => Podcast(s.FeedUrl, s.Title, s.Author, s.ImageUrl))
                .ToList());
        }

        if (folder.Value.Type != MediaType.Podcast || DecodeFeedKey(folder.Value.Id) is not { } feedUrl)
        {
            return new BrowseResult(Name, []);
        }

        var feed = await GetFeedAsync(feedUrl, forceRefresh: false, cancellationToken);
        return new BrowseResult(feed.Title, feed.Episodes.Take(MaxEpisodes).Select(e => Episode(feedUrl, feed, e)).ToList());
    }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        // Subscribed podcasts and their recent episodes first, then the directory.
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<MediaItem>();
        foreach (var subscription in await GetSubscriptionsAsync(cancellationToken))
        {
            if (words.All(w => subscription.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            {
                results.Add(Podcast(subscription.FeedUrl, subscription.Title, subscription.Author, subscription.ImageUrl));
            }

            if (_feeds.TryGetValue(subscription.FeedUrl, out var cached))
            {
                results.AddRange(cached.Feed.Episodes
                    .Where(e => words.All(w => e.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                    .Take(10)
                    .Select(e => Episode(subscription.FeedUrl, cached.Feed, e)));
            }
        }

        try
        {
            var directory = await SearchDirectoryAsync(query, cancellationToken);
            results.AddRange(directory
                .Where(d => results.All(r => r.Ref.ToString() != d.Uri))
                .Select(d => Podcast(d.FeedUrl, d.Title, d.Author, d.ImageUrl)));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Searching the podcast directory failed");
        }

        return results;
    }

    public async Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken)
    {
        switch (item.Type)
        {
            case MediaType.Podcast when DecodeFeedKey(item.Id) is { } feedUrl:
            {
                var feed = await GetFeedAsync(feedUrl, forceRefresh: false, cancellationToken);
                return Podcast(feedUrl, feed.Title, feed.Author, feed.ImageUrl);
            }

            case MediaType.Episode when SplitEpisodeId(item.Id) is ({ } feedUrl, { } key):
            {
                var feed = await GetFeedAsync(feedUrl, forceRefresh: false, cancellationToken);
                return feed.Episodes.FirstOrDefault(e => e.Key == key) is { } episode ? Episode(feedUrl, feed, episode) : null;
            }

            default:
                return null;
        }
    }

    public async Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken)
    {
        if (item.Type != MediaType.Episode || SplitEpisodeId(item.Id) is not ({ } feedUrl, { } key))
        {
            throw new AugoraException(ErrorCodes.ItemNotPlayable, "Only podcast episodes can be played.");
        }

        var feed = await GetFeedAsync(feedUrl, forceRefresh: false, cancellationToken);
        var episode = feed.Episodes.FirstOrDefault(e => e.Key == key)
            ?? throw new AugoraException(ErrorCodes.ItemNotFound, "The episode is no longer in the feed.", 404);
        return new HttpStreamSource(episode.AudioUrl, IsLive: false, episode.ContentType);
    }

    private async Task<PodcastFeed> GetFeedAsync(string feedUrl, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _feeds.TryGetValue(feedUrl, out var cached) && timeProvider.GetUtcNow() - cached.FetchedAt < FeedLifetime)
        {
            return cached.Feed;
        }

        try
        {
            await using var stream = await httpClientFactory.CreateClient(FeedClientName).GetStreamAsync(feedUrl, cancellationToken);
            var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(stream, settings);
            var feed = PodcastFeedParser.Parse(await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken));
            _feeds[feedUrl] = (feed, timeProvider.GetUtcNow());
            return feed;
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or FormatException)
        {
            if (_feeds.TryGetValue(feedUrl, out var stale))
            {
                logger.LogWarning(ex, "Refreshing podcast feed {Feed} failed; using the cached copy", feedUrl);
                return stale.Feed;
            }

            throw new AugoraException(ErrorCodes.ItemNotFound, $"The podcast feed could not be read: {ex.Message}", 502, ex);
        }
    }

    private static MediaItem Podcast(string feedUrl, string title, string? author, string? imageUrl) =>
        new(new MediaItemRef(ProviderId, MediaType.Podcast, FeedKey(feedUrl)), title, author, imageUrl, IsBrowsable: true);

    private static MediaItem Episode(string feedUrl, PodcastFeed feed, PodcastEpisode episode) => new(
        new MediaItemRef(ProviderId, MediaType.Episode, $"{FeedKey(feedUrl)}/{episode.Key}"),
        episode.Title,
        episode.PublishedAt is { } date ? $"{feed.Title} · {date:yyyy-MM-dd}" : feed.Title,
        episode.ImageUrl,
        IsPlayable: true,
        Duration: episode.Duration);

    internal static string? DecodeFeedKey(string key)
    {
        try
        {
            var base64 = key.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? url : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static (string? FeedUrl, string? Key) SplitEpisodeId(string id) =>
        id.LastIndexOf('/') is var slash and > 0 ? (DecodeFeedKey(id[..slash]), id[(slash + 1)..]) : (null, null);
}

internal sealed record ItunesSearchResponse([property: JsonPropertyName("results")] IReadOnlyList<ItunesPodcast>? Results);

internal sealed record ItunesPodcast(
    [property: JsonPropertyName("collectionName")] string? CollectionName,
    [property: JsonPropertyName("artistName")] string? ArtistName,
    [property: JsonPropertyName("feedUrl")] string? FeedUrl,
    [property: JsonPropertyName("artworkUrl100")] string? ArtworkUrl100,
    [property: JsonPropertyName("artworkUrl600")] string? ArtworkUrl600);

[JsonSerializable(typeof(ItunesSearchResponse))]
internal sealed partial class PodcastJsonContext : JsonSerializerContext;
