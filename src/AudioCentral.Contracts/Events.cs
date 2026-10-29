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
    public const string SetupCompleted = "setup_completed";
    public const string LastAdmin = "last_admin";
    public const string InvalidCredentials = "invalid_credentials";
    public const string LockedOut = "locked_out";
    public const string IdentityError = "identity_error";

    /// <summary>All Spotify accounts are streaming; the <c>holders</c> extension lists <see cref="SpotifyAccountHolderDto"/>s.</summary>
    public const string SpotifyNoFreeAccount = "spotify_no_free_account";
    public const string SpotifyNotConfigured = "spotify_not_configured";
    public const string SpotifyAuthFailed = "spotify_auth_failed";

    /// <summary>The account must first be approved for playback; the <c>pairing</c> extension holds a <see cref="SpotifyPairingDto"/>.</summary>
    public const string SpotifyPairingRequired = "spotify_pairing_required";
    public const string Forbidden = "forbidden";
    public const string InvalidGroup = "invalid_group";
}
