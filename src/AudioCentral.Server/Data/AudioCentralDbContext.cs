using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AudioCentral.Server.Data;

public sealed class AudioCentralDbContext(DbContextOptions<AudioCentralDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<ManualCastDevice> ManualCastDevices => Set<ManualCastDevice>();

    public DbSet<StoredQueue> Queues => Set<StoredQueue>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

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
    }
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
