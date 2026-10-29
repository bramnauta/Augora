using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AudioCentral.Server.Data;

public sealed class AudioCentralDbContext(DbContextOptions<AudioCentralDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<ManualCastDevice> ManualCastDevices => Set<ManualCastDevice>();

    public DbSet<StoredQueue> Queues => Set<StoredQueue>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    public DbSet<StoredSpotifyAccount> SpotifyAccounts => Set<StoredSpotifyAccount>();

    public DbSet<StoredPodcast> Podcasts => Set<StoredPodcast>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(e => e.Property(u => u.DisplayName).HasMaxLength(200));

        builder.Entity<ManualCastDevice>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(200);
            e.Property(d => d.Host).HasMaxLength(255);
        });

        builder.Entity<StoredQueue>(e =>
        {
            e.HasKey(q => q.PlayerId);
            e.Property(q => q.PlayerId).HasMaxLength(200);
        });

        builder.Entity<ApiToken>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Prefix).HasMaxLength(20);
            e.Property(t => t.SecretHash).HasMaxLength(64);
            e.HasIndex(t => t.SecretHash).IsUnique();
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StoredSpotifyAccount>(e =>
        {
            e.Property(a => a.Id).HasMaxLength(200);
            e.Property(a => a.DisplayName).HasMaxLength(200);
            e.Property(a => a.ClientId).HasMaxLength(100);
            // A user's own accounts become shared when the user is deleted.
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<StoredPodcast>(e =>
        {
            e.Property(p => p.FeedUrl).HasMaxLength(2000);
            e.Property(p => p.Title).HasMaxLength(500);
            e.Property(p => p.Author).HasMaxLength(500);
            e.Property(p => p.ImageUrl).HasMaxLength(2000);
            e.HasIndex(p => p.FeedUrl).IsUnique();
        });
    }
}

/// <summary>A podcast subscription, shared by all users.</summary>
public sealed class StoredPodcast
{
    public Guid Id { get; set; }

    public required string FeedUrl { get; set; }

    public required string Title { get; set; }

    public string? Author { get; set; }

    public string? ImageUrl { get; set; }

    public DateTimeOffset SubscribedAt { get; set; }
}

/// <summary>A linked Spotify account. The refresh token is encrypted with ASP.NET Core data protection.</summary>
public sealed class StoredSpotifyAccount
{
    /// <summary>The Spotify user id.</summary>
    public required string Id { get; set; }

    public required string DisplayName { get; set; }

    public string? OwnerUserId { get; set; }

    public string? ClientId { get; set; }

    public required string ProtectedRefreshToken { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

public sealed class ManualCastDevice
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Host { get; set; }

    public int Port { get; set; }
}

/// <summary>A player's queue, stored as JSON: it is always read and written as a whole.</summary>
public sealed class StoredQueue
{
    public required string PlayerId { get; set; }

    public required string Json { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A long-lived API token. Only the SHA-256 of the secret is stored.</summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public AppUser? User { get; set; }

    public required string Name { get; set; }

    /// <summary>The first characters of the secret, to recognise a token in the UI.</summary>
    public required string Prefix { get; set; }

    public required string SecretHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
