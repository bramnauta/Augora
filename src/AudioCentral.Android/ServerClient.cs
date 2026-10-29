using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AudioCentral.Contracts;

namespace AudioCentral.Android;

/// <summary>
/// REST calls of the native player: token exchange and refresh, and player commands from the notification.
/// </summary>
internal sealed class ServerClient(AppSettings settings) : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public bool HasTokens => !string.IsNullOrEmpty(settings.RefreshToken);

    /// <summary>Checks that an AudioCentral server answers at <paramref name="serverUrl"/>.</summary>
    public static async Task CheckServerAsync(string serverUrl, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{serverUrl}/api/auth/status", cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadFromJsonAsync<AuthStatusDto>(JsonSerializerOptions.Web, cancellationToken);
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
