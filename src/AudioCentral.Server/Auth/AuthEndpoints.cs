using System.Security.Claims;
using AudioCentral.Contracts;
using AudioCentral.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AudioCentral.Server.Auth;

internal static class AuthEndpoints
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Authentication");

        auth.MapGet("/status", async (ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var setupRequired = !await users.Users.AnyAsync();
            var user = principal.Identity?.IsAuthenticated == true ? await users.GetUserAsync(principal) : null;
            var external = (await signIn.GetExternalAuthenticationSchemesAsync())
                .Select(s => new ExternalProviderDto(s.Name, s.DisplayName ?? s.Name))
                .ToList();
            return new AuthStatusDto(setupRequired, user is null ? null : await ToDtoAsync(user, users), external);
        }).AllowAnonymous();

        auth.MapPost("/setup", async Task<Results<Ok<UserDto>, ProblemHttpResult, ValidationProblem>> (
            SetupRequest request, UserManager<AppUser> users, RoleManager<IdentityRole> roles, SignInManager<AppUser> signIn) =>
        {
            await SetupLock.WaitAsync();
            try
            {
                if (await users.Users.AnyAsync())
                {
                    return Problem(ErrorCodes.SetupCompleted, "Setup has already been completed.", StatusCodes.Status409Conflict);
                }

                var user = new AppUser { UserName = request.UserName.Trim(), DisplayName = request.DisplayName?.Trim() };
                var result = await users.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return ValidationProblem(result);
                }

                await EnsureAdminAsync(user, users, roles);
                await signIn.SignInAsync(user, isPersistent: true);
                return TypedResults.Ok(await ToDtoAsync(user, users));
            }
            finally
            {
                SetupLock.Release();
            }
        }).AllowAnonymous();

        auth.MapPost("/login", async Task<Results<EmptyHttpResult, ProblemHttpResult>> (LoginRequest request, SignInManager<AppUser> signIn) =>
        {
            // Cookie for the web UI, bearer + refresh token (written by the sign-in itself) for apps.
            signIn.AuthenticationScheme = request.UseCookies ? IdentityConstants.ApplicationScheme : IdentityConstants.BearerScheme;
            var result = await signIn.PasswordSignInAsync(request.UserName.Trim(), request.Password, isPersistent: request.UseCookies, lockoutOnFailure: true);
            if (result.IsLockedOut)
            {
                return Problem(ErrorCodes.LockedOut, "Too many failed attempts, try again later.", StatusCodes.Status429TooManyRequests);
            }

            return result.Succeeded
                ? TypedResults.Empty
                : Problem(ErrorCodes.InvalidCredentials, "Invalid user name or password.", StatusCodes.Status401Unauthorized);
        }).AllowAnonymous();

        auth.MapPost("/refresh", async Task<Results<SignInHttpResult, ChallengeHttpResult>> (
            RefreshRequest request,
            SignInManager<AppUser> signIn,
            IOptionsMonitor<BearerTokenOptions> bearerOptions,
            TimeProvider timeProvider) =>
        {
            var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
            var ticket = protector.Unprotect(request.RefreshToken);
            if (ticket?.Properties.ExpiresUtc is not { } expires
                || timeProvider.GetUtcNow() >= expires
                || await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user)
            {
                return TypedResults.Challenge();
            }

            var principal = await signIn.CreateUserPrincipalAsync(user);
            return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        }).AllowAnonymous();

        auth.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        });

        auth.MapPost("/password", async Task<Results<NoContent, ValidationProblem>> (
            ChangePasswordRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new InvalidOperationException("Authenticated user not found.");
            var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                return ValidationProblem(result);
            }

            await signIn.RefreshSignInAsync(user);
            return TypedResults.NoContent();
        });

        auth.MapGet("/external/{scheme}", async Task<Results<ChallengeHttpResult, NotFound>> (string scheme, string? returnUrl, SignInManager<AppUser> signIn) =>
        {
            if ((await signIn.GetExternalAuthenticationSchemesAsync()).All(s => s.Name != scheme))
            {
                return TypedResults.NotFound();
            }

            var callback = $"/api/auth/external/callback?returnUrl={Uri.EscapeDataString(LocalUrl(returnUrl))}";
            return TypedResults.Challenge(signIn.ConfigureExternalAuthenticationProperties(scheme, callback), [scheme]);
        }).AllowAnonymous();

        auth.MapGet("/external/callback", ExternalCallbackAsync).AllowAnonymous().ExcludeFromDescription();

        return app;
    }

    private static async Task<RedirectHttpResult> ExternalCallbackAsync(
        string? returnUrl,
        SignInManager<AppUser> signIn,
        UserManager<AppUser> users,
        RoleManager<IdentityRole> roles,
        IOptions<OidcOptions> oidc,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints));
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return TypedResults.LocalRedirect("/login?error=external");
        }

        var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
        if (result.Succeeded)
        {
            return TypedResults.LocalRedirect(LocalUrl(returnUrl));
        }

        if (!oidc.Value.AutoProvision)
        {
            return TypedResults.LocalRedirect("/login?error=no_account");
        }

        var claims = info.Principal;
        var userName = claims.FindFirstValue("preferred_username") ?? claims.FindFirstValue("email") ?? claims.FindFirstValue("sub") ?? info.ProviderKey;
        var isFirstUser = !await users.Users.AnyAsync();
        var user = new AppUser
        {
            UserName = await UniqueUserNameAsync(userName, users),
            Email = claims.FindFirstValue("email"),
            DisplayName = claims.FindFirstValue("name"),
        };

        var created = await users.CreateAsync(user);
        if (!created.Succeeded || !(await users.AddLoginAsync(user, info)).Succeeded)
        {
            logger.LogWarning("Provisioning external user {UserName} failed: {Errors}", userName, string.Join("; ", created.Errors.Select(e => e.Description)));
            return TypedResults.LocalRedirect("/login?error=external");
        }

        if (isFirstUser)
        {
            await EnsureAdminAsync(user, users, roles);
        }

        logger.LogInformation("Provisioned user {UserName} from {Provider}", user.UserName, info.LoginProvider);
        await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
        return TypedResults.LocalRedirect(LocalUrl(returnUrl));
    }

    internal static async Task<UserDto> ToDtoAsync(AppUser user, UserManager<AppUser> users) =>
        new(user.Id, user.UserName ?? "", user.DisplayName, await users.IsInRoleAsync(user, Roles.Admin));

    internal static async Task EnsureAdminAsync(AppUser user, UserManager<AppUser> users, RoleManager<IdentityRole> roles)
    {
        if (!await roles.RoleExistsAsync(Roles.Admin))
        {
            await roles.CreateAsync(new IdentityRole(Roles.Admin));
        }

        await users.AddToRoleAsync(user, Roles.Admin);
    }

    internal static ProblemHttpResult Problem(string code, string title, int status) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    internal static ValidationProblem ValidationProblem(IdentityResult result) =>
        TypedResults.ValidationProblem(
            result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()),
            title: string.Join(" ", result.Errors.Select(e => e.Description)),
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.IdentityError });

    private static string LocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";

    private static async Task<string> UniqueUserNameAsync(string userName, UserManager<AppUser> users)
    {
        var candidate = userName;
        for (var i = 2; await users.FindByNameAsync(candidate) is not null; i++)
        {
            candidate = $"{userName}{i}";
        }

        return candidate;
    }
}
