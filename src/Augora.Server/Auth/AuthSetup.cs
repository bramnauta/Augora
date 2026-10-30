using Augora.Contracts;
using Augora.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Augora.Server.Auth;

public sealed class OidcOptions
{
    public const string SectionName = "Authentication:Oidc";
    public const string Scheme = "oidc";

    /// <summary>Issuer url, e.g. <c>https://auth.example.com/application/o/augora/</c>. Empty disables OIDC.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string DisplayName { get; set; } = "Single sign-on";

    /// <summary>Create a local account on first external login. When off, an admin must link accounts (not yet supported).</summary>
    public bool AutoProvision { get; set; } = true;
}

/// <summary>
/// Authentication: Identity cookie for the web UI, Identity bearer + refresh tokens for apps (Android),
/// long-lived API tokens for Home Assistant, optional OpenID Connect. A policy scheme picks per request.
/// </summary>
internal static class AuthSetup
{
    private const string SelectorScheme = "Augora";
    private const string AccessTokenQuery = "access_token";

    public static void AddAugoraAuth(this WebApplicationBuilder builder, string dataDirectory)
    {
        var services = builder.Services;

        // Without persisted keys every restart would log everybody out and invalidate refresh tokens.
        // The application name is part of the encryption: it keeps its pre-rename value, or the Spotify tokens and
        // sessions saved by earlier versions can no longer be decrypted.
        services.AddDataProtection()
            .SetApplicationName("AudioCentral")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.User.AllowedUserNameCharacters += " ";
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AugoraDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        var authentication = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = SelectorScheme;
                options.DefaultChallengeScheme = SelectorScheme;
            })
            // No display name: Identity lists every scheme with one as an external login provider.
            .AddPolicyScheme(SelectorScheme, displayName: null, options => options.ForwardDefaultSelector = SelectScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null)
            .AddBearerToken(IdentityConstants.BearerScheme, options =>
            {
                options.BearerTokenExpiration = TimeSpan.FromHours(1);
                options.RefreshTokenExpiration = TimeSpan.FromDays(60);
                options.Events.OnMessageReceived = context =>
                {
                    context.Token = GetBearerToken(context.HttpContext);
                    return Task.CompletedTask;
                };
            });

        authentication.AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(options =>
        {
            options.Cookie.Name = "Augora.Auth";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;

            // The UI is a SPA: answer API calls with status codes instead of redirects.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        }));

        var oidc = builder.Configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        services.Configure<OidcOptions>(builder.Configuration.GetSection(OidcOptions.SectionName));
        if (!string.IsNullOrWhiteSpace(oidc.Authority))
        {
            authentication.AddOpenIdConnect(OidcOptions.Scheme, oidc.DisplayName, options =>
            {
                options.SignInScheme = IdentityConstants.ExternalScheme;
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SaveTokens = false;
                options.Scope.Add("email");
                options.Scope.Add("profile");
                options.CallbackPath = "/signin-oidc";
            });
        }

        services.AddScoped<ApiTokenService>();
        services.AddSingleton<AppLoginCodes>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Roles.Admin, policy => policy.RequireRole(Roles.Admin));
    }

    /// <summary>
    /// The bearer token from the Authorization header, or from the <c>access_token</c> query string on the hub
    /// (browsers cannot send headers on WebSocket connections).
    /// </summary>
    public static string? GetBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        return context.Request.Path.StartsWithSegments(EventsHub.Path) && context.Request.Query.TryGetValue(AccessTokenQuery, out var token)
            ? token.ToString()
            : null;
    }

    private static string SelectScheme(HttpContext context)
    {
        var token = GetBearerToken(context);
        if (string.IsNullOrEmpty(token))
        {
            return IdentityConstants.ApplicationScheme;
        }

        return ApiTokenService.LooksLikeApiToken(token) ? ApiTokenAuthenticationHandler.SchemeName : IdentityConstants.BearerScheme;
    }
}
