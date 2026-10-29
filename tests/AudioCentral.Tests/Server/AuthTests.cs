using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AudioCentral.Contracts;

namespace AudioCentral.Tests.Server;

public sealed class AuthTests : IDisposable
{
    private const string AdminPassword = "correct horse battery";
    private readonly AudioCentralFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SetupAdminAsync()
    {
        var client = _factory.CreateCookieClient();
        var response = await client.PostAsJsonAsync("/api/auth/setup", new SetupRequest("admin", AdminPassword), Ct);
        response.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Api_requires_authentication_but_status_and_streams_do_not()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/players", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/stream/unknown", Ct)).StatusCode);

        var status = await client.GetFromJsonAsync<AuthStatusDto>("/api/auth/status", Ct);
        Assert.True(status!.SetupRequired);
        Assert.Null(status.User);
        Assert.Empty(status.ExternalProviders);
    }

    [Fact]
    public async Task Setup_creates_the_first_admin_once()
    {
        var client = await SetupAdminAsync();

        var status = await client.GetFromJsonAsync<AuthStatusDto>("/api/auth/status", Ct);
        Assert.False(status!.SetupRequired);
        Assert.True(status.User!.IsAdmin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/players", Ct)).StatusCode);

        var second = await _factory.CreateClient().PostAsJsonAsync("/api/auth/setup", new SetupRequest("intruder", "another password"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_cookie_session_can_be_exchanged_for_tokens_but_a_bearer_cannot()
    {
        var cookieClient = await SetupAdminAsync();

        var exchange = await cookieClient.PostAsync("/api/auth/token", null, Ct);
        exchange.EnsureSuccessStatusCode();
        var tokens = await exchange.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct);
        Assert.False(string.IsNullOrEmpty(tokens!.RefreshToken));

        var bearerClient = _factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await bearerClient.GetAsync("/api/players", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearerClient.PostAsync("/api/auth/token", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Apps_log_in_with_bearer_and_refresh_tokens()
    {
        await SetupAdminAsync();
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", AdminPassword, UseCookies: false), Ct);
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/players", Ct)).StatusCode);

        var refreshed = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken), Ct);
        refreshed.EnsureSuccessStatusCode();
        Assert.False(string.IsNullOrEmpty((await refreshed.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!.AccessToken));
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        await SetupAdminAsync();

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong password"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Api_tokens_work_until_revoked()
    {
        var admin = await SetupAdminAsync();
        var created = await (await admin.PostAsJsonAsync("/api/tokens", new CreateApiTokenRequest("Home Assistant"), Ct))
            .Content.ReadFromJsonAsync<CreatedApiTokenDto>(Ct);
        Assert.StartsWith("act_", created!.Secret, StringComparison.Ordinal);

        var homeAssistant = _factory.CreateClient();
        homeAssistant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Secret);
        Assert.Equal(HttpStatusCode.OK, (await homeAssistant.GetAsync("/api/players", Ct)).StatusCode);

        (await admin.DeleteAsync($"/api/tokens/{created.Token.Id}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await homeAssistant.GetAsync("/api/players", Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_admins_manage_users_and_the_last_admin_stays()
    {
        var admin = await SetupAdminAsync();
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("bob", "bobs password", "Bob", IsAdmin: false), Ct)).EnsureSuccessStatusCode();

        var bob = _factory.CreateCookieClient();
        (await bob.PostAsJsonAsync("/api/auth/login", new LoginRequest("bob", "bobs password"), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/players", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/googlecast/devices", Ct)).StatusCode);

        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users", Ct);
        var adminId = users!.Single(u => u.UserName == "admin").Id;
        var demote = await admin.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest(IsAdmin: false), Ct);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
    }
}
