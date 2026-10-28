using AudioCentral.Contracts;
using AudioCentral.Core.Media;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Core.Providers;

/// <summary>
/// Aggregates browse and search over all registered music providers.
/// </summary>
public sealed class LibraryService(IEnumerable<IMusicProvider> providers, ILogger<LibraryService> logger)
{
    private readonly Dictionary<string, IMusicProvider> _providers =
        providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IMusicProvider> Providers => _providers.Values;

    public IMusicProvider GetProvider(string id) =>
        _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new AudioCentralException(ErrorCodes.ProviderNotFound, $"Provider '{id}' does not exist.", 404);

    public async Task<BrowseResultDto> BrowseAsync(string? uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uri))
        {
            var roots = _providers.Values
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
        var tasks = _providers.Values.Select(async provider =>
        {
            try
            {
                var items = await provider.SearchAsync(query, cancellationToken);
                return new ProviderSearchResultDto(provider.Id, provider.Name, items.Select(i => i.ToDto()).ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Search in provider {Provider} failed", provider.Id);
                return new ProviderSearchResultDto(provider.Id, provider.Name, [], ex.Message);
            }
        });

        return new SearchResultDto(query, await Task.WhenAll(tasks));
    }

    public async Task<MediaItem> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
        await GetProvider(item.Provider).GetItemAsync(item, cancellationToken)
        ?? throw new AudioCentralException(ErrorCodes.ItemNotFound, $"Item '{item}' does not exist.", 404);
}
