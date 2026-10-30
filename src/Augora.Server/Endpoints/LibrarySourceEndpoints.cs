using Augora.Contracts;
using Augora.Core;
using Augora.Providers.LocalFiles;
using Augora.Providers.Podcasts;
using Augora.Providers.TuneIn;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Augora.Server.Endpoints;

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
        MapTuneIn(app.MapGroup("/api/tunein").WithTags("TuneIn"));
        return app;
    }

    private static void MapTuneIn(RouteGroupBuilder tunein)
    {
        tunein.MapGet("/", (IServiceProvider services) =>
            services.GetService<TuneInProvider>() is { } provider
                ? new TuneInSettingsDto(true, provider.Username, provider.UsernameFromSettings)
                : new TuneInSettingsDto(false, null, false));

        tunein.MapPut("/username", async Task<Results<NoContent, NotFound>> (
                SetTuneInUsernameRequest request, IServiceProvider services, CancellationToken ct) =>
            {
                if (services.GetService<TuneInProvider>() is not { } provider)
                {
                    return TypedResults.NotFound();
                }

                await provider.SetUsernameAsync(request.Username, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Roles.Admin)
            .WithSummary("Set the TuneIn username for favorites; empty goes back to the server configuration.");
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

        local.MapGet("/", (IServiceProvider services) =>
        {
            var provider = services.GetService<LocalFilesProvider>();
            return new LocalLibraryStatusDto(
                provider is not null,
                provider?.TrackCount ?? 0,
                provider?.Folders.Select(f => new LocalFolderDto(f.Name, f.Path)).ToList() ?? [],
                provider?.FoldersFromSettings ?? false,
                (int)(provider?.RescanInterval.TotalMinutes ?? 0),
                provider?.RescanIntervalFromSettings ?? false);
        });

        local.MapPut("/rescan-interval", async Task<Results<NoContent, NotFound>> (
                SetLocalRescanRequest request, IServiceProvider services, CancellationToken ct) =>
            {
                if (services.GetService<LocalFilesProvider>() is not { } provider)
                {
                    return TypedResults.NotFound();
                }

                await provider.SetRescanMinutesAsync(request.Minutes, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Roles.Admin)
            .WithSummary("Set how often the music folders are scanned, in minutes; null goes back to the server configuration.");

        local.MapPut("/folders", async Task<Results<NoContent, NotFound>> (
                SetLocalFoldersRequest request, IServiceProvider services, CancellationToken ct) =>
            {
                if (services.GetService<LocalFilesProvider>() is not { } provider)
                {
                    return TypedResults.NotFound();
                }

                if (request.Folders is null)
                {
                    await provider.SetFoldersAsync(null, ct);
                    return TypedResults.NoContent();
                }

                // The name is part of every item id, so it must be unique and cannot contain the id separator.
                var folders = request.Folders
                    .Select(f => new LocalFolderOptions { Name = f.Name.Trim(), Path = f.Path.Trim() })
                    .ToList();
                if (folders.Any(f => f.Name.Length == 0 || f.Path.Length == 0 || f.Name.Contains('/')))
                {
                    throw new AugoraException(ErrorCodes.InvalidRequest, "Every folder needs a name (without '/') and a path.", 400);
                }

                if (folders.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Count)
                {
                    throw new AugoraException(ErrorCodes.InvalidRequest, "Folder names must be unique.", 400);
                }

                await provider.SetFoldersAsync(folders, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Roles.Admin)
            .WithSummary("Set the music folders (saved in the database); null goes back to the server configuration.");

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
