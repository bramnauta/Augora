using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Augora.Core.Providers;

/// <summary>A track by artist and title, as a source that knows nothing about our providers names it.</summary>
public sealed record SimilarTrack(string Artist, string Title);

/// <summary>Music that goes with a track, for autoplay. Providers turn the result into playable items.</summary>
public interface ISimilarTrackSource
{
    /// <summary>False when the source is not set up (no API key): autoplay then relies on the provider alone.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<SimilarTrack>> GetSimilarAsync(string artist, string title, int limit, CancellationToken cancellationToken);
}

/// <summary>Last.fm <c>track.getSimilar</c>: similar tracks from listening data. Needs a (free) API key.</summary>
public sealed class LastFmClient(IHttpClientFactory httpClientFactory, IOptions<AugoraOptions> options, ILogger<LastFmClient> logger)
    : ISimilarTrackSource
{
    public const string HttpClientName = "lastfm";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.LastFmApiKey);

    public async Task<IReadOnlyList<SimilarTrack>> GetSimilarAsync(string artist, string title, int limit, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var url = $"?method=track.getsimilar&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}"
            + $"&limit={limit}&autocorrect=1&format=json&api_key={Uri.EscapeDataString(options.Value.LastFmApiKey!.Trim())}";
        using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Last.fm returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return Parse(document.RootElement, logger);
    }

    internal static IReadOnlyList<SimilarTrack> Parse(JsonElement root, ILogger? logger = null)
    {
        // Errors come as 200 with {"error": 6, "message": "Track not found"}.
        if (root.TryGetProperty("error", out var error))
        {
            logger?.LogDebug("Last.fm: {Error} {Message}", error, root.TryGetProperty("message", out var m) ? m.GetString() : null);
            return [];
        }

        if (!root.TryGetProperty("similartracks", out var similar) || !similar.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SimilarTrack>();
        foreach (var track in tracks.EnumerateArray())
        {
            var title = track.TryGetProperty("name", out var n) ? n.GetString() : null;
            var artist = track.TryGetProperty("artist", out var a) && a.TryGetProperty("name", out var an) ? an.GetString() : null;
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            {
                result.Add(new SimilarTrack(artist, title));
            }
        }

        return result;
    }
}
