using System.Net;
using System.Net.Http.Json;
using Augora.Contracts;

namespace Augora.Tests.Server;

public sealed class LibrarySourceEndpointTests : IDisposable
{
    private readonly AugoraFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SetupAdminAsync()
    {
        var client = _factory.CreateCookieClient();
        (await client.PostAsJsonAsync("/api/auth/setup", new SetupRequest("admin", "correct horse battery"), Ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Without_music_folders_the_local_library_has_no_folders_and_stays_out_of_the_library()
    {
        var client = await SetupAdminAsync();

        var status = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);

        Assert.True(status!.Enabled);
        Assert.Empty(status.Folders);
        var roots = await client.GetFromJsonAsync<BrowseResultDto>("/api/browse", Ct);
        Assert.DoesNotContain(roots!.Items, i => i.Uri.StartsWith("local:", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/artwork/local/unknown", Ct)).StatusCode);
    }

    [Fact]
    public async Task Music_folders_can_be_set_in_the_ui_and_reset()
    {
        var client = await SetupAdminAsync();
        var folder = Directory.CreateTempSubdirectory("augora-music").FullName;

        (await client.PutAsJsonAsync("/api/local/folders", new SetLocalFoldersRequest([new LocalFolderDto("Music", folder)]), Ct)).EnsureSuccessStatusCode();
        var status = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);
        Assert.Equal([new LocalFolderDto("Music", folder)], status!.Folders);
        Assert.True(status.FoldersFromSettings);

        var duplicate = new SetLocalFoldersRequest([new LocalFolderDto("Music", folder), new LocalFolderDto("music", folder)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/local/folders", duplicate, Ct)).StatusCode);

        (await client.PutAsJsonAsync("/api/local/folders", new SetLocalFoldersRequest(null), Ct)).EnsureSuccessStatusCode();
        status = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);
        Assert.Empty(status!.Folders);
        Assert.False(status.FoldersFromSettings);
    }

    [Fact]
    public async Task The_rescan_interval_can_be_set_in_the_ui_within_limits_and_reset()
    {
        var client = await SetupAdminAsync();
        var before = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);
        Assert.Equal(360, before!.RescanMinutes);

        (await client.PutAsJsonAsync("/api/local/rescan-interval", new SetLocalRescanRequest(30), Ct)).EnsureSuccessStatusCode();
        var status = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);
        Assert.Equal(30, status!.RescanMinutes);
        Assert.True(status.RescanFromSettings);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/local/rescan-interval", new SetLocalRescanRequest(1), Ct)).StatusCode);

        (await client.PutAsJsonAsync("/api/local/rescan-interval", new SetLocalRescanRequest(null), Ct)).EnsureSuccessStatusCode();
        status = await client.GetFromJsonAsync<LocalLibraryStatusDto>("/api/local", Ct);
        Assert.Equal(360, status!.RescanMinutes);
        Assert.False(status.RescanFromSettings);
    }

    [Fact]
    public async Task TuneIn_settings_report_a_disabled_provider()
    {
        var client = await SetupAdminAsync();

        var status = await client.GetFromJsonAsync<TuneInSettingsDto>("/api/tunein", Ct);

        Assert.False(status!.Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/tunein/username", new SetTuneInUsernameRequest("me"), Ct)).StatusCode);
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
