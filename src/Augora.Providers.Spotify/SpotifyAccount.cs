using Augora.Contracts;

namespace Augora.Providers.Spotify;

/// <param name="Id">The Spotify user id; linking the same Spotify account again updates it.</param>
/// <param name="OwnerUserId">The Augora user who prefers this account; <c>null</c> for a shared account.</param>
/// <param name="ClientId">Overrides <see cref="SpotifyOptions.ClientId"/> for this account.</param>
/// <param name="SoloistApiKey">The account owner's Soloist API key, for <see cref="SpotifyEngine.Soloist"/>.</param>
public sealed record SpotifyAccount(
    string Id,
    string DisplayName,
    string? OwnerUserId,
    string? ClientId,
    string RefreshToken,
    SpotifyEngine Engine = SpotifyEngine.Librespot,
    string? SoloistApiKey = null);

/// <summary>Persists linked accounts. The server implementation encrypts the refresh token and the Soloist API key.</summary>
public interface ISpotifyAccountStore
{
    Task<IReadOnlyList<SpotifyAccount>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(SpotifyAccount account, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}
