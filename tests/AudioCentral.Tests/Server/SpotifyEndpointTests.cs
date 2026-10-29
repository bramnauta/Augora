using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AudioCentral.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AudioCentral.Tests.Server;

public sealed class SpotifyEndpointTests : IDisposable
{
    private readonly AudioCentralFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SetupAdminAsync()
    {
        var client = _factory.CreateCookieClient();
        (await client.PostAsJsonAsync("/api/auth/setup", new SetupRequest("admin", "correct horse battery"), Ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Status_lists_no_accounts_and_the_redirect_uri()
    {
        var client = await SetupAdminAsync();

        var status = await client.GetFromJsonAsync<SpotifyStatusDto>("/api/spotify", Ct);

        Assert.True(status!.Enabled);
        Assert.False(status.HasDefaultClientId);
        Assert.Empty(status.Accounts);
        Assert.EndsWith("/api/spotify/callback", status.RedirectUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linking_needs_a_client_id_and_returns_a_pkce_authorize_url()
    {
        var client = await SetupAdminAsync();

        var missing = await client.PostAsJsonAsync("/api/spotify/link", new LinkSpotifyAccountRequest(), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ErrorCodes.SpotifyNotConfigured, problem.RootElement.GetProperty("code").GetString());

        var link = await client.PostAsJsonAsync("/api/spotify/link", new LinkSpotifyAccountRequest(ClientId: "my-client"), Ct);
        link.EnsureSuccessStatusCode();
        var response = await link.Content.ReadFromJsonAsync<LinkSpotifyAccountResponse>(Ct);
        Assert.StartsWith("https://accounts.spotify.com/authorize?", response!.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains("client_id=my-client", response.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", response.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains("streaming", response.AuthorizeUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_callback_with_an_unknown_state_goes_back_with_an_error()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/spotify/callback?code=abc&state=unknown", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/spotify?error=", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }
}
