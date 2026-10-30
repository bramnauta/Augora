using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Augora.Server.Auth;

/// <summary>
/// One-time codes for apps that log in through the system browser (OIDC): the browser session proves who the user is,
/// the code carries that over to the app, which exchanges it for tokens. A code works once and expires quickly.
/// </summary>
internal sealed class AppLoginCodes(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (string UserId, DateTimeOffset Expires)> _codes = new();

    public string Create(string userId)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in _codes.Where(c => c.Value.Expires <= now).Select(c => c.Key))
        {
            _codes.TryRemove(expired, out _);
        }

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _codes[code] = (userId, now + Lifetime);
        return code;
    }

    /// <summary>The user the code was made for; the code is used up.</summary>
    public string? Redeem(string code) =>
        _codes.TryRemove(code, out var entry) && entry.Expires > timeProvider.GetUtcNow() ? entry.UserId : null;
}
