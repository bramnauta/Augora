using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Augora.Contracts;

namespace Augora.Client;

/// <summary>
/// REST calls of the native player: token exchange and refresh, and player commands from the lock screen or notification.
/// </summary>
public sealed class ServerClient(AppSettings settings) : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public bool HasTokens => !string.IsNullOrEmpty(settings.RefreshToken);

    /// <summary>Checks that an Augora server answers at <paramref name="serverUrl"/>, and says how it wants users to log in.</summary>
    public static async Task<AuthStatusDto> CheckServerAsync(string serverUrl, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{serverUrl}/api/auth/status", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthStatusDto>(JsonSerializerOptions.Web, cancellationToken)
            ?? throw new HttpRequestException("The server did not answer like an Augora server.");
    }

    /// <summary>Logs in with a password; <c>false</c> when the user name or password is wrong.</summary>
    public async Task<bool> LoginAsync(string userName, string password, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsJsonAsync($"{settings.ServerUrl}/api/auth/login", new LoginRequest(userName, password, UseCookies: false), JsonSerializerOptions.Web, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ApiException("Too many failed attempts, try again later.", ErrorCodes.LockedOut, (int)response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        Save(await response.Content.ReadFromJsonAsync<AccessTokenResponse>(JsonSerializerOptions.Web, cancellationToken));
        return true;
    }

    /// <summary>Exchanges the one-time code that an OIDC login in the system browser ended with for tokens.</summary>
    public async Task<bool> ExchangeAppCodeAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsJsonAsync($"{settings.ServerUrl}/api/auth/app/token", new AppTokenRequest(code), JsonSerializerOptions.Web, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        Save(await response.Content.ReadFromJsonAsync<AccessTokenResponse>(JsonSerializerOptions.Web, cancellationToken));
        return true;
    }

    public void Logout() => settings.ClearTokens();

    /// <summary>Turns what the user typed into a server address: scheme added, trailing slash removed.</summary>
    public static string NormalizeServerUrl(string? input)
    {
        var url = (input ?? "").Trim().TrimEnd('/');
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "http://" + url;
    }

    /// <summary>Exchanges the WebView's login cookie for tokens; <c>false</c> when the WebView is not logged in.</summary>
    public async Task<bool> ExchangeCookieAsync(string cookieHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ServerUrl}/api/auth/token");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        Save(await response.Content.ReadFromJsonAsync<AccessTokenResponse>(JsonSerializerOptions.Web, cancellationToken));
        return true;
    }

    /// <summary>A valid access token, refreshed when needed; <c>null</c> when the login is gone.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (settings.AccessToken is { } token && settings.AccessTokenExpires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return token;
        }

        await _refreshLock.WaitAsync();
        try
        {
            if (settings.AccessToken is { } fresh && settings.AccessTokenExpires > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return fresh;
            }

            if (settings.RefreshToken is not { } refresh)
            {
                return null;
            }

            using var response = await Http.PostAsJsonAsync($"{settings.ServerUrl}/api/auth/refresh", new RefreshRequest(refresh), JsonSerializerOptions.Web);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                settings.ClearTokens();
                return null;
            }

            response.EnsureSuccessStatusCode();
            Save(await response.Content.ReadFromJsonAsync<AccessTokenResponse>(JsonSerializerOptions.Web));
            return settings.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Sends a player command (pause, resume, next, previous, stop) through the server, so the queue follows.</summary>
    public async Task SendPlayerCommandAsync(string playerId, string command)
    {
        if (await GetAccessTokenAsync() is not { } token)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ServerUrl}/api/players/{Uri.EscapeDataString(playerId)}/{command}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
    }

    public void Dispose() => _refreshLock.Dispose();

    private void Save(AccessTokenResponse? tokens)
    {
        if (tokens is not null)
        {
            settings.SaveTokens(tokens.AccessToken, tokens.ExpiresIn, tokens.RefreshToken);
        }
    }
}
