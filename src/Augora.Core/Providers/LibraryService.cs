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
        var tasks = Providers.OrderBy(p => p.SearchOrder).Select(async provider =>
        {
            try
            {
                return (Items: await provider.SearchAsync(query, cancellationToken), Error: (ProviderSearchErrorDto?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Search in provider {Provider} failed", provider.Id);
                return (Items: [], Error: new ProviderSearchErrorDto(provider.Id, provider.Name, ex.Message));
            }
        });
        var results = await Task.WhenAll(tasks);

        // One list, best match first. The sort is stable: equal matches keep the order of the provider (streaming before
        // radio) and, within a provider, its own order.
        var items = results
            .SelectMany(r => r.Items)
            .OrderByDescending(i => SearchRanking.Score(query, i))
            .Select(i => i.ToDto())
            .ToList();
        return new SearchResultDto(query, items, results.Select(r => r.Error).OfType<ProviderSearchErrorDto>().ToList());
    }

    public Task<IReadOnlyList<MediaItem>> GetQueueItemsAsync(MediaItem item, CancellationToken cancellationToken) =>
        GetProvider(item.Ref.Provider).GetQueueItemsAsync(item, cancellationToken);

    public async Task<MediaItem> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
        await GetProvider(item.Provider).GetItemAsync(item, cancellationToken)
        ?? throw new AugoraException(ErrorCodes.ItemNotFound, $"Item '{item}' does not exist.", 404);
}
