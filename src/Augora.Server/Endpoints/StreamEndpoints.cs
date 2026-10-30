using Augora.Core.Streaming;

namespace Augora.Server.Endpoints;

internal static class StreamEndpoints
{
    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        // Players (Cast devices) cannot authenticate; the unguessable token is the authorisation.
        app.MapMethods("/api/stream/{token}", [HttpMethods.Get, HttpMethods.Head], async (string token, HttpContext context, StreamSessionManager sessions, StreamRelay relay) =>
            {
                if (sessions.Get(token) is not { } session)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await relay.ServeAsync(session, context);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }
}
