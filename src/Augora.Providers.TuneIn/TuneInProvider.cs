using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Augora.Contracts;
using Augora.Core;
using Augora.Core.Media;
using Augora.Core.Providers;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Augora.Providers.TuneIn;

/// <summary>
/// Internet radio and podcasts from TuneIn, via the (unofficial but long-standing) RadioTime OPML API.
/// </summary>
public sealed class TuneInProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<TuneInOptions> options,
    IRuntimeSettings settings,
    ILogger<TuneInProvider> logger) : IMusicProvider
{
    private const string UsernameSettingKey = "TuneIn:Username";

    public const string ProviderId = "tunein";
    public const string HttpClientName = "tunein";
    private const string FavoritesFolder = "favorites";
    private const int MaxCachedItems = 10_000;

    // Items seen while browsing or searching; the API has no cheap "get item" for episodes.
    private readonly ConcurrentDictionary<MediaItemRef, MediaItem> _seen = new();
    private readonly TuneInOptions _options = options.Value;

    public string Id => ProviderId;

    public string Name => "TuneIn";

    public string Type => ProviderId;

    /// <summary>The username set in the UI, or else the one in the configuration.</summary>
    public string? Username => settings.Get(UsernameSettingKey) ?? (string.IsNullOrWhiteSpace(_options.Username) ? null : _options.Username.Trim());

    /// <summary>True when the username was set in the UI instead of the configuration.</summary>
    public bool UsernameFromSettings => settings.Get(UsernameSettingKey) is not null;

    /// <summary>Saves the username; empty goes back to the configuration.</summary>
    public Task SetUsernameAsync(string? username, CancellationToken cancellationToken) =>
        settings.SetAsync(UsernameSettingKey, username?.Trim(), cancellationToken);

    public async Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken)
    {
        if (folder is null)
        {
            var root = await GetOutlinesAsync("Browse.ashx", cancellationToken);
            var items = Remember(root.Items).ToList();
            if (Username is not null)
            {
                items.Insert(0, new MediaItem(new MediaItemRef(Id, MediaType.Folder, FavoritesFolder), "Favorites", IsBrowsable: true));
            }

            return new BrowseResult(root.Title ?? Name, items);
        }

        var path = folder.Value.Id == FavoritesFolder
            ? $"Browse.ashx?c=presets&username={Uri.EscapeDataString(Username ?? "")}"
            : folder.Value.Id;
        var result = await GetOutlinesAsync(path, cancellationToken);
        return new BrowseResult(result.Title ?? Name, Remember(result.Items).ToList());
    }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var result = await GetOutlinesAsync($"Search.ashx?query={Uri.EscapeDataString(query)}", cancellationToken);
        return Remember(result.Items).ToList();
    }

    public async Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken)
    {
        if (_seen.TryGetValue(item, out var cached))
        {
            return cached;
        }

        if (item.Type != MediaType.Radio)
        {
            return null;
        }

        var response = await GetAsync($"Describe.ashx?id={Uri.EscapeDataString(item.Id)}", TuneInJsonContext.Default.OpmlResponseOpmlDescription, cancellationToken);
        var station = response.Body?.FirstOrDefault();
        if (station?.Name is null)
        {
            return null;
        }

        var result = new MediaItem(item, station.Name, station.Slogan, TuneInMapper.SecureImageUrl(station.Logo), IsPlayable: true);
        return Remember([result]).First();
    }

    public async Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken)
    {
        var response = await GetAsync($"Tune.ashx?id={Uri.EscapeDataString(item.Id)}", TuneInJsonContext.Default.OpmlResponseOpmlStream, cancellationToken);
        var stream = TuneInMapper.SelectStream(response.Body ?? [])
            ?? throw new AugoraException(ErrorCodes.PlaybackFailed, $"TuneIn has no stream for '{item.Id}'.", 404);

        logger.LogDebug("TuneIn {Id} resolved to {Url} ({Type}, {Bitrate} kbps)", item.Id, stream.Url, stream.MediaType, stream.Bitrate);
        return new HttpStreamSource(new Uri(stream.Url!), IsLive: item.Type == MediaType.Radio);
    }

    private async Task<(string? Title, IEnumerable<MediaItem> Items)> GetOutlinesAsync(string path, CancellationToken cancellationToken)
    {
        var response = await GetAsync(path, TuneInJsonContext.Default.OpmlResponseOpmlOutline, cancellationToken);
        return (response.Head?.Title, TuneInMapper.MapOutlines(Id, response.Body ?? []));
    }

    private async Task<OpmlResponse<T>> GetAsync<T>(string path, JsonTypeInfo<OpmlResponse<T>> typeInfo, CancellationToken cancellationToken)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}{separator}render=json&formats={_options.Formats}");
        request.Headers.AcceptLanguage.ParseAdd(_options.Language);

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
            ?? throw new AugoraException(ErrorCodes.PlaybackFailed, "TuneIn returned an empty response.", 502);
        if (result.Head?.Status is { } status && status != "200")
        {
            throw new AugoraException(ErrorCodes.ItemNotFound, $"TuneIn: {result.Head.Fault ?? status}", 404);
        }

        return result;
    }

    private IEnumerable<MediaItem> Remember(IEnumerable<MediaItem> items)
    {
        if (_seen.Count > MaxCachedItems)
        {
            _seen.Clear();
        }

        foreach (var item in items)
        {
            _seen[item.Ref] = item;
            yield return item;
        }
    }
}
