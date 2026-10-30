using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Augora.Contracts;

namespace Augora.Tests.Server;

public sealed class GroupEndpointTests : IDisposable
{
    private readonly AugoraFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Groups_are_validated()
    {
        var client = _factory.CreateCookieClient();
        (await client.PostAsJsonAsync("/api/auth/setup", new SetupRequest("admin", "correct horse battery"), Ct)).EnsureSuccessStatusCode();

        Assert.Empty((await client.GetFromJsonAsync<List<SyncGroupDto>>("/api/groups", Ct))!);

        var tooSmall = await client.PostAsJsonAsync("/api/groups", new SaveSyncGroupRequest("Solo", ["cast:a"]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
        using var problem = JsonDocument.Parse(await tooSmall.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ErrorCodes.InvalidGroup, problem.RootElement.GetProperty("code").GetString());

        var unknown = await client.PostAsJsonAsync("/api/groups", new SaveSyncGroupRequest("Duo", ["cast:a", "cast:b"]), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
