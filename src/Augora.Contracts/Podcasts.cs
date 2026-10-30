namespace Augora.Contracts;

/// <param name="Uri">Media uri to browse the podcast (<c>podcast:podcast:...</c>).</param>
public sealed record PodcastSubscriptionDto(Guid Id, string FeedUrl, string Title, string? Author, string? ImageUrl, string Uri);

public sealed record SubscribePodcastRequest(string FeedUrl);

public sealed record PodcastSearchResultDto(string Title, string? Author, string FeedUrl, string? ImageUrl, string Uri);

public sealed record LocalFolderDto(string Name, string Path);

/// <param name="Folders">The music folders in use.</param>
/// <param name="FoldersFromSettings">The folders were set in the UI instead of the server configuration.</param>
/// <param name="RescanMinutes">How often the folders are scanned.</param>
/// <param name="RescanFromSettings">The interval was set in the UI instead of the server configuration.</param>
public sealed record LocalLibraryStatusDto(
    bool Enabled,
    int TrackCount,
    IReadOnlyList<LocalFolderDto> Folders,
    bool FoldersFromSettings = false,
    int RescanMinutes = 0,
    bool RescanFromSettings = false);

/// <param name="Minutes">Minutes between scans (5 to 10080); <c>null</c> goes back to the server configuration.</param>
public sealed record SetLocalRescanRequest(int? Minutes);

public sealed record TuneInSettingsDto(bool Enabled, string? Username, bool UsernameFromSettings);

/// <param name="Username">The TuneIn username whose presets are shown as favorites; empty goes back to the server configuration.</param>
public sealed record SetTuneInUsernameRequest(string? Username);

/// <param name="Folders">The music folders; <c>null</c> goes back to the server configuration.</param>
public sealed record SetLocalFoldersRequest(IReadOnlyList<LocalFolderDto>? Folders);
