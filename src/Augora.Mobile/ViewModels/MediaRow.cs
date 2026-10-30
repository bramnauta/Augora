using Augora.Contracts;

namespace Augora.Mobile.ViewModels;

/// <summary>What happens when a row is used; each list (a folder, search results) decides for itself.</summary>
public interface IRowHost
{
    Task OpenAsync(MediaRow row);

    Task PlayAsync(MediaRow row);

    Task MoreAsync(MediaRow row);
}

/// <summary>An item as a row or tile: folders open, playable items play.</summary>
public sealed class MediaRow(MediaItemDto item, string? imageUrl, IRowHost? host = null)
{
    public MediaItemDto Item { get; } = item;

    public IRowHost? Host { get; } = host;

    public string Name => Item.Name;

    public string? Subtitle => string.IsNullOrEmpty(Item.Subtitle) ? null : Item.Subtitle;

    public string? ImageUrl { get; } = imageUrl;

    public bool IsPlayable => Item.IsPlayable;

    public bool IsBrowsable => Item.IsBrowsable;

    /// <summary>A playable folder (an album) opens on tap; the play button next to it starts it.</summary>
    public bool ShowPlayButton => Item.IsPlayable;

    public bool ShowChevron => Item.IsBrowsable && !Item.IsPlayable;

    public string? DurationText => Item.Duration is { } d ? Format(d) : null;

    public static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}

/// <summary>Search results of one source.</summary>
public sealed class MediaGroup(string name, IEnumerable<MediaRow> rows) : List<MediaRow>(rows)
{
    public string Name { get; } = name;
}
