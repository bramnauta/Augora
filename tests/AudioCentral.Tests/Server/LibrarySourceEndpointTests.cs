using System.Net;
using System.Net.Http.Json;
using AudioCentral.Contracts;

namespace AudioCentral.Tests.Server;

public sealed class LibrarySourceEndpointTests : IDisposable
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
    public async Task Without_music_folders_the_local_library_is_disabled()
    {
        var client = await SetupAdminAsync();

        var status = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);

        Assert.False(status!.Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/local/rescan", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/artwork/local/unknown", Ct)).StatusCode);
    }

    [Fact]
    public async Task Podcast_subscriptions_start_empty_and_reject_invalid_urls()
    {
        var client = await SetupAdminAsync();

        Assert.Empty((await client.GetFromJsonAsync<List<PodcastSubscriptionDto>>("/api/podcasts", Ct))!);
        var invalid = await client.PostAsJsonAsync("/api/podcasts", new SubscribePodcastRequest("ftp://example.com/feed"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
