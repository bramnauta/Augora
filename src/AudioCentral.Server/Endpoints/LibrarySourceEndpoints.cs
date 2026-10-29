using AudioCentral.Contracts;
using AudioCentral.Providers.LocalFiles;
using AudioCentral.Providers.Podcasts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AudioCentral.Server.Endpoints;

/// <summary>Management of the podcast subscriptions and the local music library.</summary>
internal static class LibrarySourceEndpoints
{
    public static IEndpointRouteBuilder MapLibrarySourceEndpoints(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<PodcastProvider>() is not null)
        {
            MapPodcasts(app.MapGroup("/api/podcasts").WithTags("Podcasts"));
        }

        MapLocalFiles(app);
        return app;
    }

    private static void MapPodcasts(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/", async ([FromServices] PodcastProvider provider, CancellationToken ct) =>
            (await provider.GetSubscriptionsAsync(ct)).Select(ToDto).ToList());

        podcasts.MapPost("/", async (SubscribePodcastRequest request, [FromServices] PodcastProvider provider, CancellationToken ct) =>
                ToDto(await provider.SubscribeAsync(request.FeedUrl, ct)))
            .WithSummary("Subscribe to a podcast by its RSS feed url (shared by all users).");

        podcasts.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, [FromServices] PodcastProvider provider, CancellationToken ct) =>
            await provider.UnsubscribeAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        podcasts.MapGet("/search", ([FromQuery] string q, [FromServices] PodcastProvider provider, CancellationToken ct) =>
                provider.SearchDirectoryAsync(q, ct))
            .WithSummary("Search the podcast directory (iTunes).");
    }

    private static void MapLocalFiles(IEndpointRouteBuilder app)
    {
        var local = app.MapGroup("/api/local").WithTags("Local files");

        local.MapGet("/", (IServiceProvider services, IOptions<LocalFilesOptions> options) =>
        {
            var provider = services.GetService<LocalFilesProvider>();
            return new LocalLibraryStatusDto(
                provider is not null,
                provider?.TrackCount ?? 0,
                options.Value.Folders.Select(f => $"{f.Name}: {f.Path}").ToList());
        });

        local.MapPost("/rescan", Results<Accepted, NotFound> (IServiceProvider services, IHostApplicationLifetime lifetime) =>
            {
                if (services.GetService<LocalFilesProvider>() is not { } provider)
                {
                    return TypedResults.NotFound();
                }

                _ = Task.Run(() => provider.RescanAsync(lifetime.ApplicationStopping), CancellationToken.None);
                return TypedResults.Accepted((string?)null);
            })
            .RequireAuthorization(Roles.Admin)
            .WithSummary("Scan the music folders now.");

        // Cast devices fetch artwork without credentials; the token only identifies indexed tracks.
        app.MapGet(LocalFilesProvider.ArtworkPath + "{token}", async Task<Results<FileContentHttpResult, NotFound>> (
                string token, HttpContext context, IServiceProvider services, CancellationToken ct) =>
            {
                if (services.GetService<LocalFilesProvider>() is not { } provider || await provider.GetArtworkAsync(token, ct) is not { } artwork)
                {
                    return TypedResults.NotFound();
                }

                context.Response.Headers.CacheControl = "public, max-age=86400";
                return TypedResults.File(artwork.Data, artwork.ContentType);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static PodcastSubscriptionDto ToDto(PodcastSubscription s) =>
        new(s.Id, s.FeedUrl, s.Title, s.Author, s.ImageUrl, PodcastProvider.PodcastUri(s.FeedUrl));
}
