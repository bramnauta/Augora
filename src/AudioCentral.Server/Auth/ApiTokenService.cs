using System.Security.Cryptography;
using System.Text;
using AudioCentral.Contracts;
using AudioCentral.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AudioCentral.Server.Auth;

/// <summary>
/// Long-lived API tokens (for Home Assistant and scripts). The secret is <c>act_</c> + 43 random
/// characters; only its SHA-256 is stored, so a leaked database does not leak usable tokens.
/// </summary>
public sealed class ApiTokenService(AudioCentralDbContext db, TimeProvider timeProvider)
{
    public const string SecretPrefix = "act_";
    private const int PrefixLength = 12;
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(5);

    public static bool LooksLikeApiToken(string? value) => value?.StartsWith(SecretPrefix, StringComparison.Ordinal) == true;

    public async Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(string userId, CancellationToken cancellationToken) =>
        (await db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(cancellationToken))
            .OrderByDescending(t => t.CreatedAt)
            .Select(ToDto)
            .ToList();

    public async Task<CreatedApiTokenDto> CreateAsync(string userId, string name, CancellationToken cancellationToken)
    {
        var secret = SecretPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            Prefix = secret[..PrefixLength],
            SecretHash = Hash(secret),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiTokenDto(ToDto(token), secret);
    }

    public async Task<bool> RevokeAsync(string userId, Guid id, CancellationToken cancellationToken) =>
        await db.ApiTokens.Where(t => t.Id == id && t.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0;

    /// <summary>Returns the owner of a valid token, or <c>null</c>.</summary>
    public async Task<AppUser?> ValidateAsync(string secret, CancellationToken cancellationToken)
    {
        if (!LooksLikeApiToken(secret))
        {
            return null;
        }

        var hash = Hash(secret);
        var token = await db.ApiTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.SecretHash == hash, cancellationToken);
        if (token?.User is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedPrecision)
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return token.User;
    }

    internal static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ApiTokenDto ToDto(ApiToken token) => new(token.Id, token.Name, token.Prefix, token.CreatedAt, token.LastUsedAt);
}
