using System.Text.Encodings.Web;
using AudioCentral.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AudioCentral.Server.Auth;

internal sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens,
    SignInManager<AppUser> signInManager)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = AuthSetup.GetBearerToken(Context);
        if (!ApiTokenService.LooksLikeApiToken(secret))
        {
            return AuthenticateResult.NoResult();
        }

        var user = await tokens.ValidateAsync(secret!, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid API token.");
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
