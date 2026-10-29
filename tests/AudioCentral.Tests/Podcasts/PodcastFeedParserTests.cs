using System.Xml.Linq;
using AudioCentral.Providers.Podcasts;

namespace AudioCentral.Tests.Podcasts;

public sealed class PodcastFeedParserTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>De Test Podcast</title>
            <itunes:author>Bram</itunes:author>
            <itunes:image href="https://example.com/cover.jpg" />
            <item>
              <title>Aflevering 1</title>
              <guid>ep-1</guid>
              <pubDate>Mon, 06 Jan 2025 08:00:00 GMT</pubDate>
              <itunes:duration>1:02:03</itunes:duration>
              <enclosure url="https://example.com/1.mp3" type="audio/mpeg" length="1" />
            </item>
            <item>
              <title>Aflevering 2</title>
              <pubDate>Mon, 13 Jan 2025 08:00:00 EST</pubDate>
              <itunes:duration>754</itunes:duration>
              <itunes:image href="https://example.com/2.jpg" />
              <enclosure url="https://example.com/2.mp3" type="audio/mpeg" length="1" />
            </item>
            <item>
              <title>Zonder audio</title>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Parses_episodes_newest_first()
    {
        var feed = PodcastFeedParser.Parse(XDocument.Parse(Feed));

        Assert.Equal("De Test Podcast", feed.Title);
        Assert.Equal("Bram", feed.Author);
        Assert.Equal(["Aflevering 2", "Aflevering 1"], feed.Episodes.Select(e => e.Title));

        var second = feed.Episodes[0];
        Assert.Equal(TimeSpan.FromSeconds(754), second.Duration);
        Assert.Equal("https://example.com/2.jpg", second.ImageUrl);
        Assert.Equal(TimeSpan.FromHours(-5), second.PublishedAt!.Value.Offset);

        var first = feed.Episodes[1];
        Assert.Equal(new TimeSpan(1, 2, 3), first.Duration);
        Assert.Equal("https://example.com/cover.jpg", first.ImageUrl);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void Episode_keys_are_stable()
    {
        var a = PodcastFeedParser.Parse(XDocument.Parse(Feed));
        var b = PodcastFeedParser.Parse(XDocument.Parse(Feed));

        Assert.Equal(a.Episodes.Select(e => e.Key), b.Episodes.Select(e => e.Key));
    }

    [Theory]
    [InlineData("45:10", 2710)]
    [InlineData("3600", 3600)]
    [InlineData("", null)]
    [InlineData("soon", null)]
    public void Parses_durations(string value, int? seconds) =>
        Assert.Equal(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), PodcastFeedParser.ParseDuration(value));

    [Fact]
    public void Feed_keys_round_trip()
    {
        const string url = "https://feeds.example.com/show.xml?format=mp3&x=ü";

        Assert.Equal(url, PodcastProvider.DecodeFeedKey(PodcastProvider.FeedKey(url)));
        Assert.Null(PodcastProvider.DecodeFeedKey("!!not-base64"));
    }
}
