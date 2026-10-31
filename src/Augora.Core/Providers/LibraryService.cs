using Augora.Contracts;
using Augora.Core.Media;
using Microsoft.Extensions.Logging;

namespace Augora.Core.Providers;

/// <summary>
/// Aggregates browse and search over all registered music providers.
/// </summary>
public sealed class LibraryService(IEnumerable<IMusicProvider> providers, ILogger<LibraryService> logger)
{
    private readonly Dictionary<string, IMusicProvider> _providers =
        providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IMusicProvider> Providers => _providers.Values.Where(p => p.IsAvailable).ToList();

    public IMusicProvider GetProvider(string id) =>
        _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new AugoraException(ErrorCodes.ProviderNotFound, $"Provider '{id}' does not exist.", 404);

    public async Task<BrowseResultDto> BrowseAsync(string? uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uri))
        {
            var roots = Providers
                .Select(p => new MediaItem(new MediaItemRef(p.Id, MediaType.Folder, ""), p.Name, IsBrowsable: true).ToDto())
                .ToList();
            return new BrowseResultDto(null, "Library", roots);
        }

        var folder = MediaItemRef.Parse(uri);
        var provider = GetProvider(folder.Provider);
        var isRoot = folder.Id.Length == 0;
        var result = await provider.BrowseAsync(isRoot ? null : folder, cancellationToken);

        return new BrowseResultDto(uri, isRoot ? provider.Name : result.Title, result.Items.Select(i => i.ToDto()).ToList());
    }

    public async Task<SearchResultDto> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var tasks = Providers.Select(async provider =>
        {
            try
            {
                // Best match first within a provider (stable: equal matches keep the provider's own order).
                var items = (await provider.SearchAsync(query, cancellationToken))
                    .Select(i => (Item: i, Score: SearchRanking.Score(query, i)))
                    .OrderByDescending(i => i.Score)
                    .ToList();
                var result = new ProviderSearchResultDto(provider.Id, provider.Name, items.Select(i => i.Item.ToDto()).ToList());
                return (provider.SearchOrder, Best: items.Count == 0 ? 0 : items[0].Score, Result: result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Search in provider {Provider} failed", provider.Id);
                return (provider.SearchOrder, Best: 0, Result: new ProviderSearchResultDto(provider.Id, provider.Name, [], ex.Message));
            }
        });

        // The provider with the best match first; with equal matches the provider order decides (streaming before radio).
        var results = (await Task.WhenAll(tasks)).OrderByDescending(r => r.Best).ThenBy(r => r.SearchOrder).Select(r => r.Result).ToList();
        return new SearchResultDto(query, results);
    }

    public Task<IReadOnlyList<MediaItem>> GetQueueItemsAsync(MediaItem item, CancellationToken cancellationToken) =>
        GetProvider(item.Ref.Provider).GetQueueItemsAsync(item, cancellationToken);

    public async Task<MediaItem> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
        await GetProvider(item.Provider).GetItemAsync(item, cancellationToken)
        ?? throw new AugoraException(ErrorCodes.ItemNotFound, $"Item '{item}' does not exist.", 404);
}
