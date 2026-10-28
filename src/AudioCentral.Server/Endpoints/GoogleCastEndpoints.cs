using AudioCentral.Contracts;
using AudioCentral.Players.GoogleCast;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AudioCentral.Server.Endpoints;

internal static class GoogleCastEndpoints
{
    public static IEndpointRouteBuilder MapGoogleCastEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/googlecast/devices").WithTags("Google Cast");

        devices.MapGet("/", (CastPlayerProvider cast, CancellationToken ct) => cast.GetManualDevicesAsync(ct))
            .WithSummary("Manually added Cast devices.");

        devices.MapPost("/", async Task<Results<Created<ManualCastDeviceDto>, ValidationProblem>> (
            AddManualCastDeviceRequest request, CastPlayerProvider cast, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors[nameof(request.Name)] = ["Name is required."];
            }

            if (Uri.CheckHostName(request.Host?.Trim()) == UriHostNameType.Unknown)
            {
                errors[nameof(request.Host)] = ["Host must be a valid host name or IP address."];
            }

            if (request.Port is < 1 or > 65535)
            {
                errors[nameof(request.Port)] = ["Port must be between 1 and 65535."];
            }

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            var device = await cast.AddManualDeviceAsync(request, ct);
            return TypedResults.Created($"/api/googlecast/devices/{device.Id}", device);
        });

        devices.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, CastPlayerProvider cast, CancellationToken ct) =>
            await cast.RemoveManualDeviceAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        return app;
    }
}
