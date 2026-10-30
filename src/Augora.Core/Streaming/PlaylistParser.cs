using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Augora.Core.Streaming;

/// <summary>
/// Extracts stream urls from the playlist formats internet radio uses (PLS, M3U, ASX).
/// </summary>
internal static partial class PlaylistParser
{
    private static readonly string[] PlaylistContentTypes =
    [
        "audio/x-scpls", "audio/scpls", "application/pls+xml",
        "audio/x-mpegurl", "audio/mpegurl", "application/x-mpegurl", "application/vnd.apple.mpegurl",
        "video/x-ms-asf", "video/x-ms-asx", "audio/x-ms-asx",
    ];

    private static readonly string[] PlaylistExtensions = [".pls", ".m3u", ".m3u8", ".asx"];

    public static bool LooksLikePlaylist(Uri url, string? contentType) =>
        (contentType is not null && PlaylistContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        || PlaylistExtensions.Any(ext => url.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>HLS playlists are handed to the player as-is: their segments must be fetched by the player.</summary>
    public static bool IsHls(string content) => content.Contains("#EXT-X-", StringComparison.Ordinal);

    public static IReadOnlyList<Uri> Parse(string content, Uri baseUrl)
    {
        var trimmed = content.TrimStart();
        IEnumerable<string> entries = trimmed.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)
            ? ParsePls(trimmed)
            : trimmed.StartsWith('<')
                ? ParseAsx(trimmed)
                : ParseM3u(trimmed);

        return entries
            .Select(e => Uri.TryCreate(baseUrl, e.Trim(), out var uri) ? uri : null)
            .Where(u => u is not null && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            .Select(u => u!)
            .ToList();
    }

    private static IEnumerable<string> ParsePls(string content) =>
        content.Split('\n')
            .Select(line => PlsFileLine().Match(line.Trim()))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value);

    private static IEnumerable<string> ParseM3u(string content) =>
        content.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));

    private static List<string> ParseAsx(string content)
    {
        try
        {
            return XDocument.Parse(content)
                .Descendants()
                .Where(e => e.Name.LocalName.Equals("ref", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value)
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^File\d+\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlsFileLine();
}
