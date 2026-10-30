using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Players;
using AudioCentral.Core.Queue;
using AudioCentral.Players.Remote;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AudioCentral.Server.Endpoints;

internal static class PlayerEndpoints
{
    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        var players = app.MapGroup("/api/players").WithTags("Players");

        players.MapGet("/", (PlayerManager manager) => manager.Players.Select(manager.ToDto).ToList());

        players.MapGet("/{id}", (string id, PlayerManager manager) => manager.ToDto(manager.Get(id)));

        players.MapDelete("/{id}", Results<NoContent, NotFound, ProblemHttpResult> (string id, PlayerManager manager, RemotePlayerProvider remote) =>
            {
                var player = manager.Get(id);
                if (player.ProviderId != RemotePlayerProvider.ProviderId)
                {
                    return TypedResults.Problem("Only browser and app players can be removed.", statusCode: StatusCodes.Status400BadRequest);
                }

                if (player.Status.Available)
                {
                    return TypedResults.Problem("The player is still connected.", statusCode: StatusCodes.Status409Conflict);
                }

                return remote.Remove(id) ? TypedResults.NoContent() : TypedResults.NotFound();
            })
            .WithSummary("Forget a disconnected browser or app player.");

        players.MapPost("/{id}/play", (string id, PlayRequest request, QueueManager queues, CancellationToken ct) =>
                queues.PlayAsync(id, request.Uri, request.Option, ct, request.TakeoverPlayerId, request.Following))
            .WithSummary("Play or enqueue a media item. Albums and playlists enqueue their tracks.")
            .WithDescription("Returns 409 spotify_no_free_account (with the holders) when every Spotify account is streaming; "
                + "repeat the request with takeoverPlayerId to stop one of them.");

        players.MapPost("/{id}/pause", (string id, PlayerManager manager, CancellationToken ct) => Command(manager.GetAvailable(id).PauseAsync(ct)));
        players.MapPost("/{id}/resume", (string id, QueueManager queues, CancellationToken ct) => queues.ResumeAsync(id, ct))
            .WithSummary("Resume when paused; restart the current queue item when idle.");
        players.MapPost("/{id}/stop", (string id, PlayerManager manager, CancellationToken ct) => Command(manager.GetAvailable(id).StopAsync(ct)));
        players.MapPost("/{id}/next", (string id, QueueManager queues, CancellationToken ct) => queues.NextAsync(id, ct));
        players.MapPost("/{id}/previous", (string id, QueueManager queues, CancellationToken ct) => queues.PreviousAsync(id, ct));

        players.MapPost("/{id}/seek", (string id, SeekRequest request, QueueManager queues, CancellationToken ct) =>
                double.IsFinite(request.PositionSeconds)
                    ? queues.SeekAsync(id, TimeSpan.FromSeconds(request.PositionSeconds), ct)
                    : throw new AudioCentralException(ErrorCodes.NotSeekable, "The position must be a number of seconds.", 400))
            .WithSummary("Seek in the current item.")
            .WithDescription("Returns 409 not_seekable for live streams and players that cannot seek. Streams that cannot seek "
                + "themselves (Spotify in flow mode) restart at the position.");

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
