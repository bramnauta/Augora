namespace AudioCentral.Contracts;

/// <param name="Uri">Media uri to browse the podcast (<c>podcast:podcast:...</c>).</param>
public sealed record PodcastSubscriptionDto(Guid Id, string FeedUrl, string Title, string? Author, string? ImageUrl, string Uri);

public sealed record SubscribePodcastRequest(string FeedUrl);

public sealed record PodcastSearchResultDto(string Title, string? Author, string FeedUrl, string? ImageUrl, string Uri);

/// <param name="Folders">Configured music folders (name and path).</param>
public sealed record LocalLibraryStatusDto(bool Enabled, int TrackCount, IReadOnlyList<string> Folders);
