using Augora.Contracts;
using Augora.Core.Media;

namespace Augora.Providers.TuneIn;

internal static class TuneInMapper
{
    private static readonly HashSet<string> IgnoredQueryKeys = new(StringComparer.OrdinalIgnoreCase) { "formats", "render", "partnerId", "serial" };

    public static IEnumerable<MediaItem> MapOutlines(string providerId, IEnumerable<OpmlOutline> outlines)
    {
        foreach (var outline in outlines)
        {
            // Sections ("Stations", "Shows", ...) only group their children.
            if (outline.Type is null && outline.Children is { Count: > 0 } children)
            {
                foreach (var child in MapOutlines(providerId, children))
                {
                    yield return child;
                }

                continue;
            }

            if (Map(providerId, outline) is { } item)
            {
                yield return item;
            }
        }
    }

    public static MediaItem? Map(string providerId, OpmlOutline outline)
    {
        if (string.IsNullOrWhiteSpace(outline.Text))
        {
            return null;
        }

        var image = SecureImageUrl(outline.Image);
        switch (outline.Type)
        {
            case "link" when ToFolderId(outline.Url) is { } folderId:
                var type = outline.Item == "show" ? MediaType.Podcast : MediaType.Folder;
                return new MediaItem(new MediaItemRef(providerId, type, folderId), outline.Text, outline.Subtext, image, IsBrowsable: true);

            case "audio" when !string.IsNullOrEmpty(outline.GuideId):
                var isEpisode = outline.Item == "topic" || outline.StreamType == "download";
                var duration = outline.TopicDuration is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
                return new MediaItem(
                    new MediaItemRef(providerId, isEpisode ? MediaType.Episode : MediaType.Radio, outline.GuideId),
                    outline.Text,
                    outline.Subtext,
                    image,
                    IsPlayable: true,
                    Duration: duration);

            default:
                return null;
        }
    }

    /// <summary>
    /// Turns an OPML url such as <c>http://opml.radiotime.com/Browse.ashx?c=local&amp;formats=mp3</c>
    /// into the stable folder id <c>Browse.ashx?c=local</c>.
    /// </summary>
    public static string? ToFolderId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IgnoredQueryKeys.Contains(pair.Split('=', 2)[0]));

        var path = uri.AbsolutePath.TrimStart('/');
        var joined = string.Join('&', query);
        return joined.Length == 0 ? path : $"{path}?{joined}";
    }

    /// <summary>TuneIn's CDNs serve the same images over HTTPS; this avoids mixed content in the UI.</summary>
    public static string? SecureImageUrl(string? url) =>
        url is not null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && url.Contains(".tunein.com/", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;

    /// <summary>Picks the most reliable, highest quality stream, preferring progressive over HLS.</summary>
    public static OpmlStream? SelectStream(IEnumerable<OpmlStream> streams) =>
        streams
            .Where(s => Uri.TryCreate(s.Url, UriKind.Absolute, out _))
            .OrderBy(s => string.Equals(s.MediaType, "hls", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(s => s.Reliability ?? 0)
            .ThenByDescending(s => s.Bitrate ?? 0)
            .FirstOrDefault();
}
