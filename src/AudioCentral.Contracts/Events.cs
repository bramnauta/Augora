namespace AudioCentral.Contracts;

/// <summary>
/// Names shared between the SignalR hub and its clients (Blazor UI, Android app, Home Assistant).
/// </summary>
public static class EventsHub
{
    public const string Path = "/hubs/events";

    /// <summary>Server → client: <see cref="PlayerDto"/>.</summary>
    public const string PlayerUpdated = "PlayerUpdated";

    /// <summary>Server → client: player id (string).</summary>
    public const string PlayerRemoved = "PlayerRemoved";

    /// <summary>Server → client: <see cref="QueueDto"/>.</summary>
    public const string QueueUpdated = "QueueUpdated";
}

/// <summary>
/// Machine readable error codes, returned in the <c>code</c> extension of Problem Details responses.
/// </summary>
public static class ErrorCodes
{
    public const string PlayerNotFound = "player_not_found";
    public const string PlayerUnavailable = "player_unavailable";
    public const string ProviderNotFound = "provider_not_found";
    public const string ItemNotFound = "item_not_found";
    public const string ItemNotPlayable = "item_not_playable";
    public const string InvalidUri = "invalid_uri";
    public const string PlaybackFailed = "playback_failed";
}
