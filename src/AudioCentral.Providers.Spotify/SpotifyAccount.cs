namespace AudioCentral.Providers.Spotify;

/// <param name="Id">The Spotify user id; linking the same Spotify account again updates it.</param>
/// <param name="OwnerUserId">The AudioCentral user who prefers this account; <c>null</c> for a shared account.</param>
/// <param name="ClientId">Overrides <see cref="SpotifyOptions.ClientId"/> for this account.</param>
public sealed record SpotifyAccount(string Id, string DisplayName, string? OwnerUserId, string? ClientId, string RefreshToken);

/// <summary>Persists linked accounts. The server implementation encrypts the refresh token.</summary>
public interface ISpotifyAccountStore
{
    Task<IReadOnlyList<SpotifyAccount>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(SpotifyAccount account, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}
