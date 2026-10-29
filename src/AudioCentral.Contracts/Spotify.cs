namespace AudioCentral.Contracts;

/// <param name="OwnerUserId">The AudioCentral user who linked the account; <c>null</c> for a shared account.</param>
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
    string? Error);

/// <param name="Shared">Link as a shared account usable by everyone equally, instead of as your own (admins only).</param>
/// <param name="ClientId">Spotify app client id for this account; empty uses the server's default.</param>
public sealed record LinkSpotifyAccountRequest(bool Shared = false, string? ClientId = null);

public sealed record LinkSpotifyAccountResponse(string AuthorizeUrl, string RedirectUri);

public sealed record SpotifyStatusDto(bool Enabled, bool HasDefaultClientId, string RedirectUri, IReadOnlyList<SpotifyAccountDto> Accounts);

/// <summary>A player streaming with a Spotify account, returned with <see cref="ErrorCodes.SpotifyNoFreeAccount"/>.</summary>
/// <param name="CanTakeOver">The requesting user may stop this stream (their own or a shared account).</param>
public sealed record SpotifyAccountHolderDto(string AccountId, string AccountName, string PlayerId, string PlayerName, bool CanTakeOver);
