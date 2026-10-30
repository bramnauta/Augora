using System.Globalization;
using System.Xml.Linq;

namespace Augora.Providers.Podcasts;

internal sealed record PodcastFeed(string Title, string? Author, string? ImageUrl, IReadOnlyList<PodcastEpisode> Episodes);

/// <param name="Key">Stable id within the feed: a hash of the guid, or of the audio url without a guid.</param>
internal sealed record PodcastEpisode(
    string Key,
    string Title,
    DateTimeOffset? PublishedAt,
    TimeSpan? Duration,
    Uri AudioUrl,
    string? ContentType,
    string? ImageUrl);

/// <summary>Parses RSS 2.0 podcast feeds with the iTunes extensions.</summary>
internal static class PodcastFeedParser
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    public static PodcastFeed Parse(XDocument document)
    {
        var channel = document.Root?.Element("channel") ?? throw new FormatException("Not an RSS feed: no channel element.");
        var feedImage = channel.Element(Itunes + "image")?.Attribute("href")?.Value ?? channel.Element("image")?.Element("url")?.Value;

        var episodes = new List<PodcastEpisode>();
        foreach (var item in channel.Elements("item"))
        {
            var enclosure = item.Element("enclosure");
            if (!Uri.TryCreate(enclosure?.Attribute("url")?.Value?.Trim(), UriKind.Absolute, out var audio))
            {
                continue;
            }

            var guid = item.Element("guid")?.Value.Trim();
            episodes.Add(new PodcastEpisode(
                Hash(string.IsNullOrEmpty(guid) ? audio.ToString() : guid),
                Text(item.Element("title")) ?? audio.Segments[^1],
                ParseDate(item.Element("pubDate")?.Value),
                ParseDuration(item.Element(Itunes + "duration")?.Value),
                audio,
                enclosure?.Attribute("type")?.Value,
                item.Element(Itunes + "image")?.Attribute("href")?.Value ?? feedImage));
        }

        return new PodcastFeed(
            Text(channel.Element("title")) ?? "Podcast",
            Text(channel.Element(Itunes + "author")),
            feedImage,
            episodes.OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue).ToList());
    }

    /// <summary><c>hh:mm:ss</c>, <c>mm:ss</c> or seconds.</summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split(':');
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            seconds = seconds * 60 + number;
        }

        return seconds > 0 ? TimeSpan.FromSeconds(Math.Round(seconds)) : null;
    }

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // RFC 822 dates; some feeds use named zones (EST, GMT) that DateTimeOffset does not know.
        var text = value.Trim();
        foreach (var (zone, offset) in new[] { ("GMT", "+0000"), ("UT", "+0000"), ("EST", "-0500"), ("EDT", "-0400"), ("CST", "-0600"), ("CDT", "-0500"), ("PST", "-0800"), ("PDT", "-0700") })
        {
            if (text.EndsWith(" " + zone, StringComparison.Ordinal))
            {
                text = text[..^zone.Length] + offset;
                break;
            }
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    private static string? Text(XElement? element) => string.IsNullOrWhiteSpace(element?.Value) ? null : element.Value.Trim();

    private static string Hash(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)).AsSpan(0, 12));
}
