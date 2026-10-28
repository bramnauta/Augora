using AudioCentral.Contracts;
using AudioCentral.Core.Players;
using AudioCentral.Core.Queue;

namespace AudioCentral.Server.Endpoints;

internal static class PlayerEndpoints
{
    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        var players = app.MapGroup("/api/players").WithTags("Players");

        players.MapGet("/", (PlayerManager manager) => manager.Players.Select(p => p.ToDto()).ToList());

        players.MapGet("/{id}", (string id, PlayerManager manager) => manager.Get(id).ToDto());

        players.MapPost("/{id}/play", (string id, PlayRequest request, QueueManager queues, CancellationToken ct) =>
                queues.PlayAsync(id, request.Uri, request.Option, ct))
            .WithSummary("Play or enqueue a media item.");

        players.MapPost("/{id}/pause", (string id, PlayerManager manager, CancellationToken ct) => Command(manager.GetAvailable(id).PauseAsync(ct)));
        players.MapPost("/{id}/resume", (string id, PlayerManager manager, CancellationToken ct) => Command(manager.GetAvailable(id).ResumeAsync(ct)));
        players.MapPost("/{id}/stop", (string id, PlayerManager manager, CancellationToken ct) => Command(manager.GetAvailable(id).StopAsync(ct)));
        players.MapPost("/{id}/next", (string id, QueueManager queues, CancellationToken ct) => queues.NextAsync(id, ct));
        players.MapPost("/{id}/previous", (string id, QueueManager queues, CancellationToken ct) => queues.PreviousAsync(id, ct));

        players.MapPut("/{id}/volume", (string id, VolumeRequest request, PlayerManager manager, CancellationToken ct) =>
            Command(manager.GetAvailable(id).SetVolumeAsync(request.Level, ct)));
        players.MapPut("/{id}/mute", (string id, MuteRequest request, PlayerManager manager, CancellationToken ct) =>
            Command(manager.GetAvailable(id).SetMutedAsync(request.Muted, ct)));

        var queue = players.MapGroup("/{id}/queue").WithTags("Queue");
        queue.MapGet("/", (string id, QueueManager queues, CancellationToken ct) => queues.GetQueueAsync(id, ct));
        queue.MapDelete("/", (string id, QueueManager queues, CancellationToken ct) => queues.ClearAsync(id, ct));
        queue.MapPost("/items/{itemId}/play", (string id, string itemId, QueueManager queues, CancellationToken ct) => queues.PlayItemAsync(id, itemId, ct));
        queue.MapDelete("/items/{itemId}", (string id, string itemId, QueueManager queues, CancellationToken ct) => queues.RemoveAsync(id, itemId, ct));
        queue.MapPut("/shuffle", (string id, bool enabled, QueueManager queues, CancellationToken ct) => queues.SetShuffleAsync(id, enabled, ct));
        queue.MapPut("/repeat", (string id, RepeatMode mode, QueueManager queues, CancellationToken ct) => queues.SetRepeatAsync(id, mode, ct));

        return app;
    }

    private static async Task<IResult> Command(Task command)
    {
        await command;
        return TypedResults.NoContent();
    }
}
