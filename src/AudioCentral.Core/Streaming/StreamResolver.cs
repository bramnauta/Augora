using System.Text;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Core.Streaming;

/// <summary>
/// Follows playlists (PLS/M3U/ASX) until it reaches actual audio, and determines the content type.
/// </summary>
public sealed class StreamResolver(HttpClient httpClient, ILogger<StreamResolver> logger)
{
    public const string HttpClientName = "streaming";
    private const int MaxDepth = 5;
    private const int MaxPlaylistBytes = 256 * 1024;

    public async Task<ResolvedStream> ResolveAsync(HttpStreamSource source, CancellationToken cancellationToken)
    {
        var url = source.Url;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (!PlaylistParser.LooksLikePlaylist(finalUrl, contentType))
            {
                return new ResolvedStream(finalUrl, AudioContentTypes.Normalize(contentType, finalUrl), source.IsLive, Direct: false);
            }

            var content = await ReadLimitedAsync(response, cancellationToken);
            if (PlaylistParser.IsHls(content))
            {
                return new ResolvedStream(finalUrl, "application/x-mpegURL", source.IsLive, Direct: true);
            }

            var entries = PlaylistParser.Parse(content, finalUrl);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException($"Playlist '{finalUrl}' does not contain any stream.");
            }

            logger.LogDebug("Playlist {Playlist} resolved to {Entry}", finalUrl, entries[0]);
            url = entries[0];
        }

        throw new InvalidOperationException($"Playlists nested too deep for '{source.Url}'.");
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxPlaylistBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}

internal static class AudioContentTypes
{
    public static string Normalize(string? contentType, Uri url)
    {
        var type = contentType?.ToLowerInvariant();
        return type switch
        {
            "audio/aacp" or "audio/x-aac" or "audio/aac" => "audio/aac",
            "audio/mpeg" or "audio/mp3" or "audio/x-mpeg" => "audio/mpeg",
            "audio/ogg" or "application/ogg" => "audio/ogg",
            "audio/flac" or "audio/x-flac" => "audio/flac",
            "audio/mp4" or "audio/x-m4a" => "audio/mp4",
            _ when type?.StartsWith("audio/", StringComparison.Ordinal) == true => type,
            _ => FromExtension(url),
        };
    }

    private static string FromExtension(Uri url) => Path.GetExtension(url.AbsolutePath).ToLowerInvariant() switch
    {
        ".aac" => "audio/aac",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".m4a" or ".mp4" => "audio/mp4",
        ".wav" => "audio/wav",
        _ => "audio/mpeg",
    };
}
