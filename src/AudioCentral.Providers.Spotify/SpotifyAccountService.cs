using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AudioCentral.Contracts;
using AudioCentral.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioCentral.Providers.Spotify;

/// <summary>
/// Linked Spotify accounts: the OAuth (authorization code + PKCE) link flow and access tokens that stay fresh.
/// </summary>
public sealed class SpotifyAccountService(
    ISpotifyAccountStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<SpotifyOptions> options,
    TimeProvider timeProvider,
    ILogger<SpotifyAccountService> logger) : IDisposable
{
    public const string AccountsClientName = "spotify-accounts";
    public const string ApiClientName = "spotify-api";

    internal const string Scopes =
        "streaming user-read-private playlist-read-private playlist-read-collaborative user-library-read user-follow-read";

    private static readonly TimeSpan PendingLinkLifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, PendingLink> _pending = new();
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SpotifyOptions _options = options.Value;
    private List<SpotifyAccount>? _accounts;

    /// <summary>Raised after an account was linked, updated or removed.</summary>
    public event EventHandler? AccountsChanged;

    public bool HasDefaultClientId => !string.IsNullOrWhiteSpace(_options.ClientId);

    /// <summary>Last token refresh error per account, shown in the UI.</summary>
    public string? GetError(string accountId) => _tokens.TryGetValue(accountId, out var token) ? token.Error : null;

    public async Task<IReadOnlyList<SpotifyAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        if (_accounts is { } loaded)
        {
            return loaded;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            return _accounts ??= [.. await store.GetAllAsync(cancellationToken)];
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<SpotifyAccount?> GetAccountAsync(string id, CancellationToken cancellationToken) =>
        (await GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts linking: returns the Spotify url the browser must open.</summary>
    public string CreateAuthorizeUrl(string? ownerUserId, string? clientId, Uri redirectUri, string returnUrl)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? _options.ClientId : clientId.Trim();
        if (string.IsNullOrWhiteSpace(effectiveClientId))
        {
            throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured, "No Spotify client id is configured.", 400);
        }

        RemoveExpiredLinks();
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        _pending[state] = new PendingLink(
            ownerUserId,
            string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim(),
            effectiveClientId,
            verifier,
            redirectUri,
            returnUrl,
            timeProvider.GetUtcNow());

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = effectiveClientId,
            ["scope"] = Scopes,
            ["redirect_uri"] = redirectUri.ToString(),
            ["state"] = state,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
        };
        return "https://accounts.spotify.com/authorize?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <summary>Finishes linking after Spotify redirected back. Returns the account and the UI url to go back to.</summary>
    public async Task<(SpotifyAccount Account, string ReturnUrl)> CompleteLinkAsync(string state, string code, CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(state, out var link) || timeProvider.GetUtcNow() - link.CreatedAt > PendingLinkLifetime)
        {
            throw new AudioCentralException(ErrorCodes.SpotifyAuthFailed, "The Spotify login expired; please try again.", 400);
        }

        var token = await RequestTokenAsync(link.EffectiveClientId, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = link.RedirectUri.ToString(),
            ["code_verifier"] = link.Verifier,
        }, cancellationToken);

        if (token.RefreshToken is null)
        {
            throw new AudioCentralException(ErrorCodes.SpotifyAuthFailed, "Spotify did not return a refresh token.", 502);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "me");
        request.Headers.Authorization = new("Bearer", token.AccessToken);
        using var response = await httpClientFactory.CreateClient(ApiClientName).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync(SpotifyJsonContext.Default.SpotifyUser, cancellationToken)
            ?? throw new AudioCentralException(ErrorCodes.SpotifyAuthFailed, "Spotify returned no user profile.", 502);

        var account = new SpotifyAccount(user.Id, user.DisplayName ?? user.Id, link.OwnerUserId, link.ClientId, token.RefreshToken);
        await store.SaveAsync(account, cancellationToken);
        Remember(account, token);
        await ReloadAsync(cancellationToken);
        logger.LogInformation("Linked Spotify account {Account}", account.Id);
        return (account, link.ReturnUrl);
    }

    public async Task RemoveAccountAsync(string id, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(id, cancellationToken);
        _tokens.TryRemove(id, out _);
        await ReloadAsync(cancellationToken);
    }

    /// <summary>A valid access token for the Web API and librespot, refreshed when it is about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_tokens.TryGetValue(account.Id, out var cached) && cached.AccessToken is not null && cached.ExpiresAt - now > TimeSpan.FromMinutes(5))
        {
            return cached.AccessToken;
        }

        var clientId = account.ClientId ?? _options.ClientId
            ?? throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured, "No Spotify client id is configured.", 400);
        try
        {
            var token = await RequestTokenAsync(clientId, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = account.RefreshToken,
            }, cancellationToken);

            // With PKCE Spotify rotates refresh tokens: the old one stops working.
            if (token.RefreshToken is { } rotated && rotated != account.RefreshToken)
            {
                account = account with { RefreshToken = rotated };
                await store.SaveAsync(account, cancellationToken);
                await ReloadAsync(cancellationToken, notify: false);
            }

            Remember(account, token);
            return token.AccessToken;
        }
        catch (AudioCentralException ex)
        {
            _tokens[account.Id] = new CachedToken(null, now, ex.Message);
            throw;
        }
    }

    private void Remember(SpotifyAccount account, SpotifyTokenResponse token) =>
        _tokens[account.Id] = new CachedToken(token.AccessToken, timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn), null);

    private async Task<SpotifyTokenResponse> RequestTokenAsync(string clientId, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        form["client_id"] = clientId;
        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClientFactory.CreateClient(AccountsClientName).PostAsync("api/token", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Spotify token request failed with {Status}: {Body}", (int)response.StatusCode, body);
            throw new AudioCentralException(ErrorCodes.SpotifyAuthFailed, $"Spotify refused the login ({(int)response.StatusCode}). Link the account again.", 502);
        }

        return await response.Content.ReadFromJsonAsync(SpotifyJsonContext.Default.SpotifyTokenResponse, cancellationToken)
            ?? throw new AudioCentralException(ErrorCodes.SpotifyAuthFailed, "Spotify returned an empty token response.", 502);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken, bool notify = true)
    {
        var accounts = await store.GetAllAsync(cancellationToken);
        _accounts = [.. accounts];
        if (notify)
        {
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveExpiredLinks()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (state, link) in _pending)
        {
            if (now - link.CreatedAt > PendingLinkLifetime)
            {
                _pending.TryRemove(state, out _);
            }
        }
    }

    public void Dispose() => _loadLock.Dispose();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PendingLink(
        string? OwnerUserId,
        string? ClientId,
        string EffectiveClientId,
        string Verifier,
        Uri RedirectUri,
        string ReturnUrl,
        DateTimeOffset CreatedAt);

    private sealed record CachedToken(string? AccessToken, DateTimeOffset ExpiresAt, string? Error);
}
