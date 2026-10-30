using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Augora.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Augora.Tests.Server;

public sealed class AuthTests : IDisposable
{
    private const string AdminPassword = "correct horse battery";
    private readonly AugoraFactory _factory = new();

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
    public async Task An_app_exchanges_the_one_time_code_of_a_browser_login_for_tokens_once()
    {
        // The system browser holds the cookie session; the app only ever sees the code in the redirect.
        var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        (await browser.PostAsJsonAsync("/api/auth/setup", new SetupRequest("admin", AdminPassword), Ct)).EnsureSuccessStatusCode();

        var complete = await browser.GetAsync(AppAuth.CompletePath, Ct);
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        var location = complete.Headers.Location!.OriginalString;
        Assert.StartsWith($"{AppAuth.RedirectScheme}://auth?code=", location, StringComparison.Ordinal);
        var code = location[(location.IndexOf("code=", StringComparison.Ordinal) + 5)..];

        var app = _factory.CreateClient();
        var exchange = await app.PostAsJsonAsync("/api/auth/app/token", new AppTokenRequest(code), Ct);
        exchange.EnsureSuccessStatusCode();
        var tokens = await exchange.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/players", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/app/token", new AppTokenRequest(code), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/app/token", new AppTokenRequest("made-up"), Ct)).StatusCode);
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

    [Fact]
    public async Task Administrators_cannot_demote_or_delete_themselves_but_can_do_so_to_other_administrators()
    {
        var admin = await SetupAdminAsync();
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("second", "second password", "Second", IsAdmin: true), Ct)).EnsureSuccessStatusCode();
        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users", Ct);
        var adminId = users!.Single(u => u.UserName == "admin").Id;
        var secondId = users!.Single(u => u.UserName == "second").Id;

        var demote = await admin.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest(IsAdmin: false), Ct);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Contains(ErrorCodes.CannotChangeSelf, await demote.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var delete = await admin.DeleteAsync($"/api/users/{adminId}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains(ErrorCodes.CannotChangeSelf, await delete.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Other settings of yourself still work, and another administrator can be demoted and deleted.
        (await admin.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest(DisplayName: "Boss", IsAdmin: true), Ct)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/users/{secondId}", new UpdateUserRequest(IsAdmin: false), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{secondId}", Ct)).StatusCode);
    }
}
