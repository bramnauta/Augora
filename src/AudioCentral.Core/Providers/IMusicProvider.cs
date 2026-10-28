using AudioCentral.Core.Media;
using AudioCentral.Core.Streaming;

namespace AudioCentral.Core.Providers;

/// <summary>
/// A source of music: radio directory, streaming service, local library, podcasts, ...
/// </summary>
public interface IMusicProvider
{
    /// <summary>Stable identifier, used as the first part of every <see cref="MediaItemRef"/>.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Provider type, e.g. <c>tunein</c> or <c>spotify</c>. Several instances may share a type.</summary>
    string Type { get; }

    /// <summary>Browses a folder; <paramref name="folder"/> is <c>null</c> for the provider root.</summary>
    Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken);

    Task<StreamSource> ResolveStreamAsync(MediaItemRef item, CancellationToken cancellationToken);
}
