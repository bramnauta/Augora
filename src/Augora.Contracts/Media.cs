using System.Text.Json.Serialization;

namespace Augora.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<MediaType>))]
public enum MediaType
{
    Folder,
    Radio,
    Track,
    Album,
    Artist,
    Playlist,
    Podcast,
    Episode,
    Audiobook,
}

/// <summary>
/// A browsable and/or playable item from a music provider.
/// </summary>
/// <param name="Uri">Unique reference, formatted as <c>{provider}:{type}:{id}</c>.</param>
public sealed record MediaItemDto(
    string Uri,
    MediaType Type,
    string Name,
    string? Subtitle,
    string? ImageUrl,
    bool IsPlayable,
    bool IsBrowsable,
    string Provider,
    TimeSpan? Duration = null);

/// <param name="Uri">The browsed folder; <c>null</c> for the library root (one folder per provider).</param>
public sealed record BrowseResultDto(string? Uri, string Title, IReadOnlyList<MediaItemDto> Items);

public sealed record SearchResultDto(string Query, IReadOnlyList<ProviderSearchResultDto> Providers);

public sealed record ProviderSearchResultDto(string Provider, string ProviderName, IReadOnlyList<MediaItemDto> Items, string? Error = null);

public sealed record ProviderDto(string Id, string Name, string Type, bool Available);
