using System.Security.Claims;
using Augora.Contracts;
using Microsoft.AspNetCore.Components.Authorization;

namespace Augora.Web.Services;

/// <summary>
/// Asks the server who is logged in (the auth cookie is HttpOnly, so the client cannot read it).
/// </summary>
public sealed class ServerAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly ApiClient _api;
    private Task<AuthStatusDto>? _status;

    public ServerAuthenticationStateProvider(ApiClient api)
    {
        _api = api;
        _api.Unauthorized += Refresh;
    }

    public Task<AuthStatusDto> GetStatusAsync() => _status ??= _api.GetAuthStatusAsync();

    public async Task<UserDto?> GetUserAsync() => (await GetStatusAsync()).User;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        AuthStatusDto status;
        try
        {
            status = await GetStatusAsync();
        }
        catch (HttpRequestException)
        {
            _status = null;
            return Anonymous;
        }

        if (status.User is not { } user)
        {
            return Anonymous;
        }

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.DisplayName ?? user.UserName)];
        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, Roles.Admin));
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "Augora")));
    }

    /// <summary>Re-reads the login state, e.g. after logging in or when the server answered 401.</summary>
    public void Refresh()
    {
        _status = null;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
