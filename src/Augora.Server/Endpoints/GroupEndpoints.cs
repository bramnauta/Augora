using Augora.Contracts;
using Augora.Players.Groups;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Augora.Server.Endpoints;

internal static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        // Groups are part of playing music, not administration: every user may manage them.
        var groups = app.MapGroup("/api/groups").WithTags("Sync groups");

        groups.MapGet("/", (GroupPlayerProvider provider) => provider.Groups)
            .WithSummary("Augora's own groups; each one is also a player (playerId).");

        groups.MapPost("/", (SaveSyncGroupRequest request, GroupPlayerProvider provider, CancellationToken ct) =>
                provider.SaveAsync(null, request, ct))
            .WithSummary("Create a group of at least two players of any kind.");

        groups.MapPut("/{id:guid}", (Guid id, SaveSyncGroupRequest request, GroupPlayerProvider provider, CancellationToken ct) =>
            provider.SaveAsync(id, request, ct));

        groups.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, GroupPlayerProvider provider, CancellationToken ct) =>
            await provider.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        return app;
    }
}
