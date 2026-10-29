using System.Net.Http.Json;
using System.Text.Json;
using AudioCentral.Contracts;

namespace AudioCentral.Web.Services;

/// <summary>
/// Thin typed wrapper around the REST API. Failures surface as <see cref="ApiException"/> with the Problem Details title.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    /// <summary>Raised when the server answers 401: the session expired or the user was removed.</summary>
    public event Action? Unauthorized;

    public Task<AuthStatusDto> GetAuthStatusAsync() => GetAsync<AuthStatusDto>("api/auth/status");

    public Task<UserDto> SetupAsync(SetupRequest request) => SendAsync<UserDto>(HttpMethod.Post, "api/auth/setup", request);

    public Task LoginAsync(string userName, string password) =>
        SendAsync(HttpMethod.Post, "api/auth/login", new LoginRequest(userName, password, UseCookies: true));

    public Task LogoutAsync() => SendAsync(HttpMethod.Post, "api/auth/logout");

    public Task ChangePasswordAsync(ChangePasswordRequest request) => SendAsync(HttpMethod.Post, "api/auth/password", request);

    public Task<List<ApiTokenDto>> GetApiTokensAsync() => GetAsync<List<ApiTokenDto>>("api/tokens");

    public Task<CreatedApiTokenDto> CreateApiTokenAsync(string name) =>
        SendAsync<CreatedApiTokenDto>(HttpMethod.Post, "api/tokens", new CreateApiTokenRequest(name));

    public Task RevokeApiTokenAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/tokens/{id}");

    public Task<List<UserDto>> GetUsersAsync() => GetAsync<List<UserDto>>("api/users");

    public Task<UserDto> CreateUserAsync(CreateUserRequest request) => SendAsync<UserDto>(HttpMethod.Post, "api/users", request);

    public Task<UserDto> UpdateUserAsync(string id, UpdateUserRequest request) =>
        SendAsync<UserDto>(HttpMethod.Put, $"api/users/{Escape(id)}", request);

    public Task DeleteUserAsync(string id) => SendAsync(HttpMethod.Delete, $"api/users/{Escape(id)}");

    public Task<List<PlayerDto>> GetPlayersAsync() => GetAsync<List<PlayerDto>>("api/players");

    public Task ForgetPlayerAsync(string playerId) => SendAsync(HttpMethod.Delete, $"api/players/{Escape(playerId)}");

    public Task<BrowseResultDto> BrowseAsync(string? uri) =>
        GetAsync<BrowseResultDto>(uri is null ? "api/browse" : $"api/browse?uri={Uri.EscapeDataString(uri)}");

    public Task<SearchResultDto> SearchAsync(string query) => GetAsync<SearchResultDto>($"api/search?q={Uri.EscapeDataString(query)}");

    public Task<QueueDto> PlayAsync(string playerId, string uri, QueueOption option = QueueOption.Replace) =>
        SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/play", new PlayRequest(uri, option));

    public Task PauseAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/pause");

    public Task<QueueDto> ResumeAsync(string playerId) => SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/resume");

    public Task StopAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/stop");

    public Task NextAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/next");

    public Task PreviousAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/previous");

    public Task SetVolumeAsync(string playerId, double level) =>
        SendAsync(HttpMethod.Put, $"api/players/{Escape(playerId)}/volume", new VolumeRequest(level));

    public Task SetMutedAsync(string playerId, bool muted) =>
        SendAsync(HttpMethod.Put, $"api/players/{Escape(playerId)}/mute", new MuteRequest(muted));

    public Task<QueueDto> GetQueueAsync(string playerId) => GetAsync<QueueDto>($"api/players/{Escape(playerId)}/queue");

    public Task<QueueDto> PlayQueueItemAsync(string playerId, string itemId) =>
        SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/queue/items/{Escape(itemId)}/play");

    public Task<QueueDto> RemoveQueueItemAsync(string playerId, string itemId) =>
        SendAsync<QueueDto>(HttpMethod.Delete, $"api/players/{Escape(playerId)}/queue/items/{Escape(itemId)}");

    public Task<QueueDto> ClearQueueAsync(string playerId) => SendAsync<QueueDto>(HttpMethod.Delete, $"api/players/{Escape(playerId)}/queue");

    public Task<List<ManualCastDeviceDto>> GetManualCastDevicesAsync() => GetAsync<List<ManualCastDeviceDto>>("api/googlecast/devices");

    public Task<ManualCastDeviceDto> AddManualCastDeviceAsync(AddManualCastDeviceRequest request) =>
        SendAsync<ManualCastDeviceDto>(HttpMethod.Post, "api/googlecast/devices", request);

    public Task RemoveManualCastDeviceAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/googlecast/devices/{id}");

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private async Task<T> GetAsync<T>(string url)
    {
        using var response = await http.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendCoreAsync(method, url, body);
        await EnsureSuccessAsync(response);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendCoreAsync(method, url, body);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return http.SendAsync(request);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.RequestMessage?.RequestUri?.AbsolutePath.StartsWith("/api/auth/", StringComparison.Ordinal) != true)
        {
            Unauthorized?.Invoke();
        }

        string? title = null;
        string? code = null;
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (problem.RootElement.TryGetProperty("title", out var t))
            {
                title = t.GetString();
            }

            if (problem.RootElement.TryGetProperty("code", out var c))
            {
                code = c.GetString();
            }
        }
        catch (JsonException)
        {
        }

        throw new ApiException(title ?? response.ReasonPhrase ?? response.StatusCode.ToString(), code, (int)response.StatusCode);
    }
}

public sealed class ApiException(string message, string? code, int statusCode) : Exception(message)
{
    public string? Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}
