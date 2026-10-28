using Microsoft.EntityFrameworkCore;

namespace AudioCentral.Server.Data;

public sealed class AudioCentralDbContext(DbContextOptions<AudioCentralDbContext> options) : DbContext(options)
{
    public DbSet<ManualCastDevice> ManualCastDevices => Set<ManualCastDevice>();

    public DbSet<StoredQueue> Queues => Set<StoredQueue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManualCastDevice>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(200);
            e.Property(d => d.Host).HasMaxLength(255);
        });

        modelBuilder.Entity<StoredQueue>(e =>
        {
            e.HasKey(q => q.PlayerId);
            e.Property(q => q.PlayerId).HasMaxLength(200);
        });
    }
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
