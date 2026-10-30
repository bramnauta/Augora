using System.Security.Claims;
using Augora.Core.Providers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Augora.Server.Auth;

/// <summary>The logged-in user of the current request, for providers that prefer the user's own account (Spotify).</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor, IOptions<IdentityOptions> identityOptions) : ICurrentUser
{
    public string? UserId => accessor.HttpContext?.User.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType);
}
