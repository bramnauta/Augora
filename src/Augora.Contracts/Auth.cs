namespace Augora.Contracts;

public static class Roles
{
    public const string Admin = "Admin";
}

/// <summary>Anonymous: tells the UI whether to show setup, login or the app.</summary>
public sealed record AuthStatusDto(bool SetupRequired, UserDto? User, IReadOnlyList<ExternalProviderDto> ExternalProviders);

public sealed record ExternalProviderDto(string Scheme, string DisplayName);

public sealed record UserDto(string Id, string UserName, string? DisplayName, bool IsAdmin);

public sealed record SetupRequest(string UserName, string Password, string? DisplayName = null);

/// <param name="UseCookies">
/// <c>true</c> for the web UI (sets a cookie); <c>false</c> for apps, which receive an
/// <see cref="AccessTokenResponse"/> with a refresh token.
/// </param>
public sealed record LoginRequest(string UserName, string Password, bool UseCookies = true);

/// <summary>Same shape as ASP.NET Core Identity's bearer token response.</summary>
public sealed record AccessTokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>How the native apps log in with OIDC: through the system browser, ending in <c>augora://auth?code=...</c>.</summary>
public static class AppAuth
{
    /// <summary>The URL scheme of the redirect back into the app.</summary>
    public const string RedirectScheme = "augora";

    /// <summary>Open this in the system browser (with <c>?returnUrl=</c> <see cref="CompletePath"/>) to start the login.</summary>
    public const string ExternalPath = "/api/auth/external/";

    public const string CompletePath = "/api/auth/app/complete";
}

/// <summary>The one-time code the app got in the redirect; answered with an <see cref="AccessTokenResponse"/>.</summary>
public sealed record AppTokenRequest(string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CreateUserRequest(string UserName, string Password, string? DisplayName, bool IsAdmin);

/// <summary>Only the fields that are set are changed.</summary>
public sealed record UpdateUserRequest(string? DisplayName = null, bool? IsAdmin = null, string? NewPassword = null);

/// <summary>A long-lived API token, as used by Home Assistant. The secret is only shown once, at creation.</summary>
public sealed record ApiTokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record CreateApiTokenRequest(string Name);

public sealed record CreatedApiTokenDto(ApiTokenDto Token, string Secret);
