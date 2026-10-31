using System.Net.Http.Json;
using System.Text.Json;
using Augora.Contracts;

namespace Augora.Client;

/// <summary>
/// Thin typed wrapper around the REST API, shared by the web UI (cookie) and the native apps (bearer token).
/// Failures surface as <see cref="ApiException"/> with the Problem Details title.
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

    public Task<QueueDto> PlayAsync(
        string playerId, string uri, QueueOption option = QueueOption.Replace, string? takeoverPlayerId = null, IReadOnlyList<string>? following = null) =>
        SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/play", new PlayRequest(uri, option, takeoverPlayerId, following));

    public Task<SpotifyStatusDto> GetSpotifyAsync() => GetAsync<SpotifyStatusDto>("api/spotify");

    public Task<LinkSpotifyAccountResponse> LinkSpotifyAccountAsync(LinkSpotifyAccountRequest request) =>
        SendAsync<LinkSpotifyAccountResponse>(HttpMethod.Post, "api/spotify/link", request);

    public Task<List<PodcastSubscriptionDto>> GetPodcastsAsync() => GetAsync<List<PodcastSubscriptionDto>>("api/podcasts");

    public Task<PodcastSubscriptionDto> SubscribePodcastAsync(string feedUrl) =>
        SendAsync<PodcastSubscriptionDto>(HttpMethod.Post, "api/podcasts", new SubscribePodcastRequest(feedUrl));

    public Task UnsubscribePodcastAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/podcasts/{id}");

    public Task<List<NewPodcastEpisodeDto>> GetNewPodcastEpisodesAsync() => GetAsync<List<NewPodcastEpisodeDto>>("api/podcasts/new");

    public Task<List<PodcastSearchResultDto>> SearchPodcastDirectoryAsync(string query) =>
        GetAsync<List<PodcastSearchResultDto>>($"api/podcasts/search?q={Uri.EscapeDataString(query)}");

    public Task<List<SyncGroupDto>> GetGroupsAsync() => GetAsync<List<SyncGroupDto>>("api/groups");

    public Task<SyncGroupDto> SaveGroupAsync(Guid? id, SaveSyncGroupRequest request) =>
        id is { } existing
            ? SendAsync<SyncGroupDto>(HttpMethod.Put, $"api/groups/{existing}", request)
            : SendAsync<SyncGroupDto>(HttpMethod.Post, "api/groups", request);

    public Task DeleteGroupAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/groups/{id}");

    public Task<LocalLibraryStatusDto> GetLocalLibraryAsync() => GetAsync<LocalLibraryStatusDto>("api/local");

    public Task SetLocalFoldersAsync(IReadOnlyList<LocalFolderDto>? folders) =>
        SendAsync(HttpMethod.Put, "api/local/folders", new SetLocalFoldersRequest(folders));

    public Task SetLocalRescanMinutesAsync(int? minutes) =>
        SendAsync(HttpMethod.Put, "api/local/rescan-interval", new SetLocalRescanRequest(minutes));

    public Task<TuneInSettingsDto> GetTuneInAsync() => GetAsync<TuneInSettingsDto>("api/tunein");

    public Task SetTuneInUsernameAsync(string? username) =>
        SendAsync(HttpMethod.Put, "api/tunein/username", new SetTuneInUsernameRequest(username));

    public Task SetSpotifyClientIdAsync(string? clientId) =>
        SendAsync(HttpMethod.Put, "api/spotify/client-id", new SetSpotifyClientIdRequest(clientId));

    public Task RescanLocalLibraryAsync() => SendAsync(HttpMethod.Post, "api/local/rescan");

    public Task<SpotifyEngine> SetSpotifyEngineAsync(string accountId, SetSpotifyEngineRequest request) =>
        SendAsync<SpotifyEngine>(HttpMethod.Put, $"api/spotify/accounts/{Escape(accountId)}/engine", request);

    public Task<SpotifyPairingDto> GetSpotifyPairingAsync(string accountId) =>
        SendAsync<SpotifyPairingDto>(HttpMethod.Post, $"api/spotify/accounts/{Escape(accountId)}/pairing");

    public Task RemoveSpotifyAccountAsync(string id) => SendAsync(HttpMethod.Delete, $"api/spotify/accounts/{Escape(id)}");

    public Task PauseAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/pause");

    public Task<QueueDto> ResumeAsync(string playerId) => SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/resume");

    public Task StopAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/stop");

    public Task NextAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/next");

    public Task PreviousAsync(string playerId) => SendAsync(HttpMethod.Post, $"api/players/{Escape(playerId)}/previous");

    public Task SetVolumeAsync(string playerId, double level) =>
        SendAsync(HttpMethod.Put, $"api/players/{Escape(playerId)}/volume", new VolumeRequest(level));

    public Task<QueueDto> SeekAsync(string playerId, double positionSeconds) =>
        SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/seek", new SeekRequest(positionSeconds));

    public Task SetMutedAsync(string playerId, bool muted) =>
        SendAsync(HttpMethod.Put, $"api/players/{Escape(playerId)}/mute", new MuteRequest(muted));

    public Task<QueueDto> GetQueueAsync(string playerId) => GetAsync<QueueDto>($"api/players/{Escape(playerId)}/queue");

    public Task<QueueDto> PlayQueueItemAsync(string playerId, string itemId) =>
        SendAsync<QueueDto>(HttpMethod.Post, $"api/players/{Escape(playerId)}/queue/items/{Escape(itemId)}/play");

    public Task<QueueDto> RemoveQueueItemAsync(string playerId, string itemId) =>
        SendAsync<QueueDto>(HttpMethod.Delete, $"api/players/{Escape(playerId)}/queue/items/{Escape(itemId)}");

    public Task<QueueDto> ClearQueueAsync(string playerId) => SendAsync<QueueDto>(HttpMethod.Delete, $"api/players/{Escape(playerId)}/queue");

    public Task<QueueDto> SetShuffleAsync(string playerId, bool enabled) =>
        SendAsync<QueueDto>(HttpMethod.Put, $"api/players/{Escape(playerId)}/queue/shuffle?enabled={(enabled ? "true" : "false")}");

    public Task<QueueDto> SetRepeatAsync(string playerId, RepeatMode mode) =>
        SendAsync<QueueDto>(HttpMethod.Put, $"api/players/{Escape(playerId)}/queue/repeat?mode={mode}");

    public Task<QueueDto> SetAutoplayAsync(string playerId, bool enabled) =>
        SendAsync<QueueDto>(HttpMethod.Put, $"api/players/{Escape(playerId)}/queue/autoplay?enabled={(enabled ? "true" : "false")}");

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

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/api/auth/", StringComparison.Ordinal) != true)
        {
            Unauthorized?.Invoke();
        }

        string? title = null;
        string? code = null;
        JsonElement? details = null;
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            details = problem.RootElement.Clone();
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

        throw new ApiException(title ?? response.ReasonPhrase ?? response.StatusCode.ToString(), code, (int)response.StatusCode, details);
    }
}

public sealed class ApiException(string message, string? code, int statusCode, JsonElement? problem = null) : Exception(message)
{
    public string? Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    /// <summary>Reads an extension member of the Problem Details, e.g. <c>holders</c>.</summary>
    public T? GetExtension<T>(string name) =>
        problem is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value)
            ? value.Deserialize<T>(JsonSerializerOptions.Web)
            : default;
}
