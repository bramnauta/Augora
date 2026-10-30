using Augora.Contracts;

namespace Augora.Core.Media;

public sealed record MediaItem(
    MediaItemRef Ref,
    string Name,
    string? Subtitle = null,
    string? ImageUrl = null,
    bool IsPlayable = false,
    bool IsBrowsable = false,
    TimeSpan? Duration = null)
{
    public MediaType Type => Ref.Type;

    public MediaItemDto ToDto() =>
        new(Ref.ToString(), Ref.Type, Name, Subtitle, ImageUrl, IsPlayable, IsBrowsable, Ref.Provider, Duration);

    public static MediaItem FromDto(MediaItemDto dto) =>
        new(MediaItemRef.Parse(dto.Uri), dto.Name, dto.Subtitle, dto.ImageUrl, dto.IsPlayable, dto.IsBrowsable, dto.Duration);
}

public sealed record BrowseResult(string Title, IReadOnlyList<MediaItem> Items);
