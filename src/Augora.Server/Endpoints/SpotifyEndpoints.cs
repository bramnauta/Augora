using System.Security.Claims;
using Augora.Contracts;
using Augora.Core;
using Augora.Core.Players;
using Augora.Providers.Spotify;
using Augora.Server.Auth;
using Augora.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Augora.Server.Endpoints;

internal static class SpotifyEndpoints
{
    public const string CallbackPath = "/api/spotify/callback";

    public static IEndpointRouteBuilder MapSpotifyEndpoints(this IEndpointRouteBuilder app)
    {
        var spotify = app.MapGroup("/api/spotify").WithTags("Spotify");

        spotify.MapGet("/", async (
            HttpContext context,
            [FromServices] SpotifyAccountService accounts,
            [FromServices] SpotifyProvider provider,
            PlayerManager players,
            UserManager<AppUser> users,
            IOptions<AugoraOptions> options,
            CancellationToken ct) =>
        {
            var holders = provider.GetAccountHolders();
            var names = users.Users.Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName }).ToDictionary(u => u.Id, u => u.Name);
            var list = (await accounts.GetAccountsAsync(ct)).Select(a =>
            {
                var playerId = holders.GetValueOrDefault(a.Id);
                var playerName = playerId is null ? null : players.Players.FirstOrDefault(p => p.Id == playerId)?.Name ?? playerId;
                return new SpotifyAccountDto(a.Id, a.DisplayName, a.Id, a.OwnerUserId,
                    a.OwnerUserId is null ? null : names.GetValueOrDefault(a.OwnerUserId), a.ClientId is not null, playerId, playerName, accounts.GetError(a.Id), a.Engine);
            }).ToList();
            return new SpotifyStatusDto(true, accounts.HasDefaultClientId, RedirectUri(context, options.Value).ToString(), list,
                accounts.DefaultClientId, accounts.DefaultClientIdFromSettings);
        }).WithSummary("Linked Spotify accounts and which player streams with each.");

        spotify.MapPut("/client-id", async (SetSpotifyClientIdRequest request, [FromServices] SpotifyAccountService accounts, CancellationToken ct) =>
            {
                await accounts.SetDefaultClientIdAsync(request.ClientId, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Roles.Admin)
            .WithSummary("Set the default Spotify client id; empty goes back to the server configuration.");

        spotify.MapPost("/link", Results<Ok<LinkSpotifyAccountResponse>, ProblemHttpResult> (
            LinkSpotifyAccountRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            UserManager<AppUser> users,
            [FromServices] SpotifyAccountService accounts,
            IOptions<AugoraOptions> options) =>
        {
            if (request.Shared && !principal.IsInRole(Roles.Admin))
            {
                return AuthEndpoints.Problem(ErrorCodes.Forbidden, "Only administrators can link shared accounts.", StatusCodes.Status403Forbidden);
            }

            var redirectUri = RedirectUri(context, options.Value);
            var url = accounts.CreateAuthorizeUrl(request.Shared ? null : users.GetUserId(principal), request.ClientId, redirectUri, "/spotify");
            return TypedResults.Ok(new LinkSpotifyAccountResponse(url, redirectUri.ToString()));
        }).WithSummary("Start linking a Spotify account; open the returned url in the browser.");

        // Spotify redirects the browser here. The state is the unguessable link id, so no login is needed (the app may use bearer tokens).
        spotify.MapGet("/callback", async (HttpContext context, string? code, string? state, string? error, [FromServices] SpotifyAccountService accounts, CancellationToken ct) =>
        {
            // Behind a path prefix (Home Assistant ingress) the web app lives under that prefix.
            var pathBase = context.Request.PathBase.Value;
            if (error is not null || code is null || state is null)
            {
                return Results.Redirect($"{pathBase}/spotify?error={Uri.EscapeDataString(error ?? "missing_code")}");
            }

            try
            {
                var (_, returnUrl) = await accounts.CompleteLinkAsync(state, code, ct);
                return Results.Redirect(pathBase + AuthEndpoints.LocalUrl(returnUrl) + "?linked=1");
            }
            catch (AugoraException ex)
            {
                return Results.Redirect($"{pathBase}/spotify?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).AllowAnonymous().ExcludeFromDescription();

        spotify.MapDelete("/accounts/{id}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            string id,
            ClaimsPrincipal principal,
            UserManager<AppUser> users,
            [FromServices] SpotifyAccountService accounts,
            [FromServices] SpotifyProvider provider,
            CancellationToken ct) =>
        {
            if (await accounts.GetAccountAsync(id, ct) is not { } account)
            {
                return TypedResults.NotFound();
            }

            if (!principal.IsInRole(Roles.Admin) && account.OwnerUserId != users.GetUserId(principal))
            {
                return AuthEndpoints.Problem(ErrorCodes.Forbidden, "You can only remove your own Spotify accounts.", StatusCodes.Status403Forbidden);
            }

            await provider.RemoveAccountAsync(account.Id, ct);
            return TypedResults.NoContent();
        }).WithSummary("Unlink a Spotify account (your own, or any as administrator).");

        spotify.MapPut("/accounts/{id}/engine", async Task<Results<Ok<SpotifyEngine>, NotFound, ProblemHttpResult>> (
            string id,
            SetSpotifyEngineRequest request,
            ClaimsPrincipal principal,
            UserManager<AppUser> users,
            [FromServices] SpotifyAccountService accounts,
            [FromServices] SpotifyProvider provider,
            CancellationToken ct) =>
        {
            if (await accounts.GetAccountAsync(id, ct) is not { } account)
            {
                return TypedResults.NotFound();
            }

            if (!principal.IsInRole(Roles.Admin) && account.OwnerUserId != users.GetUserId(principal))
            {
                return AuthEndpoints.Problem(ErrorCodes.Forbidden, "You can only change your own Spotify accounts.", StatusCodes.Status403Forbidden);
            }

            return TypedResults.Ok(await provider.SetEngineAsync(account.Id, request, ct));
        }).WithSummary("Choose the playback engine of an account: go-librespot (bundled) or Spotify Soloist (downloaded from Spotify).");

        spotify.MapPost("/accounts/{id}/pairing", (string id, [FromServices] SpotifyProvider provider, CancellationToken ct) =>
                provider.GetPairingAsync(id, ct))
            .WithSummary("Start or check the playback approval of an account.")
            .WithDescription("Spotify's audio engine logs in on its own. When paired is false, open url (spotify.com/pair) "
                + "with the same Spotify account and approve; call again to check.");

        return app;
    }

    /// <summary>The callback url to register in the Spotify app; Spotify requires https except for 127.0.0.1.</summary>
    private static Uri RedirectUri(HttpContext context, AugoraOptions options)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.PublicBaseUrl.TrimEnd('/')
            : $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
        return new Uri(baseUrl + CallbackPath);
    }
}
