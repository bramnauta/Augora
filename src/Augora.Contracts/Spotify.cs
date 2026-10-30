using System.Text.Json.Serialization;

namespace Augora.Contracts;

/// <summary>What plays the audio of a Spotify account.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpotifyEngine>))]
public enum SpotifyEngine
{
    /// <summary>go-librespot, bundled. Spotify refuses its track keys for many (newer) accounts.</summary>
    Librespot,

    /// <summary>
    /// Spotify Soloist, Spotify's own headless client: works with all Premium accounts. Downloaded from Spotify after
    /// consent, needs an API key, and Spotify's terms do not clearly allow this use.
    /// </summary>
    Soloist,
}

/// <param name="OwnerUserId">The Augora user who linked the account; <c>null</c> for a shared account.</param>
/// <param name="PlayerId">The player currently streaming with this account (Spotify allows one stream per account).</param>
public sealed record SpotifyAccountDto(
    string Id,
    string DisplayName,
    string SpotifyUserId,
    string? OwnerUserId,
    string? OwnerName,
    bool HasCustomClientId,
    string? PlayerId,
    string? PlayerName,
    string? Error,
    SpotifyEngine Engine = SpotifyEngine.Librespot);

/// <param name="Shared">Link as a shared account usable by everyone equally, instead of as your own (admins only).</param>
/// <param name="ClientId">Spotify app client id for this account; empty uses the server's default.</param>
public sealed record LinkSpotifyAccountRequest(bool Shared = false, string? ClientId = null);

public sealed record LinkSpotifyAccountResponse(string AuthorizeUrl, string RedirectUri);

/// <param name="DefaultClientId">The server's default client id (not a secret).</param>
/// <param name="DefaultClientIdFromSettings">The default was set in the UI instead of the server configuration.</param>
public sealed record SpotifyStatusDto(
    bool Enabled,
    bool HasDefaultClientId,
    string RedirectUri,
    IReadOnlyList<SpotifyAccountDto> Accounts,
    string? DefaultClientId = null,
    bool DefaultClientIdFromSettings = false);

/// <param name="ClientId">The new default client id; empty goes back to the server configuration.</param>
public sealed record SetSpotifyClientIdRequest(string? ClientId);

/// <param name="SoloistApiKey">Required for <see cref="SpotifyEngine.Soloist"/> unless one is stored; from developer.spotify.com/dashboard/soloist.</param>
/// <param name="AcceptSoloistTerms">
/// Required for Soloist: Augora downloads it from Spotify for you, and Spotify's terms do not clearly allow this use.
/// </param>
public sealed record SetSpotifyEngineRequest(SpotifyEngine Engine, string? SoloistApiKey = null, bool AcceptSoloistTerms = false);

[JsonConverter(typeof(JsonStringEnumConverter<SpotifyPairingMethod>))]
public enum SpotifyPairingMethod
{
    /// <summary>Open <see cref="SpotifyPairingDto.Url"/> (spotify.com/pair) and approve; go-librespot.</summary>
    Code,

    /// <summary>Select <see cref="SpotifyPairingDto.DeviceName"/> in the Spotify app on the same network; Soloist.</summary>
    Connect,
}

/// <summary>
/// Playback login of an account. The audio engine logs in on its own and must be approved once with the same Spotify
/// account. Returned in the <c>pairing</c> extension of <see cref="ErrorCodes.SpotifyPairingRequired"/>.
/// </summary>
public sealed record SpotifyPairingDto(
    string AccountId,
    string AccountName,
    bool Paired,
    string? Url,
    string? Code,
    DateTimeOffset? ExpiresAt,
    SpotifyPairingMethod Method = SpotifyPairingMethod.Code,
    string? DeviceName = null);

/// <summary>A player streaming with a Spotify account, returned with <see cref="ErrorCodes.SpotifyNoFreeAccount"/>.</summary>
/// <param name="CanTakeOver">The requesting user may stop this stream (their own or a shared account).</param>
public sealed record SpotifyAccountHolderDto(string AccountId, string AccountName, string PlayerId, string PlayerName, bool CanTakeOver);
