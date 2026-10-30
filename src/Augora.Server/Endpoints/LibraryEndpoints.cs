using Augora.Contracts;
using Augora.Core.Providers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Augora.Server.Endpoints;

internal static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Library");

        api.MapGet("/providers", (LibraryService library) =>
            library.Providers.Select(p => new ProviderDto(p.Id, p.Name, p.Type, true)).ToList());

        api.MapGet("/browse", (string? uri, LibraryService library, CancellationToken ct) => library.BrowseAsync(uri, ct))
            .WithSummary("Browse a folder. Without uri: one folder per provider.");

        api.MapGet("/search", async Task<Results<Ok<SearchResultDto>, BadRequest>> (string q, LibraryService library, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? TypedResults.BadRequest() : TypedResults.Ok(await library.SearchAsync(q.Trim(), ct)));

        return app;
    }
}
