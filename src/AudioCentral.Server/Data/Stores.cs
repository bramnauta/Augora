using System.Text.Json;
using AudioCentral.Contracts;
using AudioCentral.Core.Queue;
using AudioCentral.Players.GoogleCast;
using AudioCentral.Providers.Spotify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AudioCentral.Server.Data;

internal sealed class EfCastDeviceStore(AudioCentralDbContext db) : ICastDeviceStore
{
    public async Task<IReadOnlyList<ManualCastDeviceDto>> GetAllAsync(CancellationToken cancellationToken) =>
        await db.ManualCastDevices
            .OrderBy(d => d.Name)
            .Select(d => new ManualCastDeviceDto(d.Id, d.Name, d.Host, d.Port))
            .ToListAsync(cancellationToken);

    public async Task<ManualCastDeviceDto> AddAsync(AddManualCastDeviceRequest request, CancellationToken cancellationToken)
    {
        var device = new ManualCastDevice { Id = Guid.CreateVersion7(), Name = request.Name.Trim(), Host = request.Host.Trim(), Port = request.Port };
        db.ManualCastDevices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        return new ManualCastDeviceDto(device.Id, device.Name, device.Host, device.Port);
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken) =>
        await db.ManualCastDevices.Where(d => d.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
}

/// <summary>Singleton, hence the context factory. Refresh tokens are stored encrypted.</summary>
internal sealed class EfSpotifyAccountStore(IDbContextFactory<AudioCentralDbContext> dbFactory, IDataProtectionProvider dataProtection, TimeProvider timeProvider)
    : ISpotifyAccountStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("AudioCentral.Spotify.RefreshToken");

    public async Task<IReadOnlyList<SpotifyAccount>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.SpotifyAccounts.AsNoTracking().ToListAsync(cancellationToken);
        // SQLite cannot order by DateTimeOffset; there are only a handful of accounts.
        return stored
            .OrderBy(a => a.LinkedAt)
            .Select(a => new SpotifyAccount(a.Id, a.DisplayName, a.OwnerUserId, a.ClientId, _protector.Unprotect(a.ProtectedRefreshToken)))
            .ToList();
    }

    public async Task SaveAsync(SpotifyAccount account, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.SpotifyAccounts.FirstOrDefaultAsync(a => a.Id == account.Id, cancellationToken);
        if (stored is null)
        {
            stored = new StoredSpotifyAccount
            {
                Id = account.Id,
                DisplayName = account.DisplayName,
                ProtectedRefreshToken = "",
                LinkedAt = timeProvider.GetUtcNow(),
            };
            db.SpotifyAccounts.Add(stored);
        }

        stored.DisplayName = account.DisplayName;
        stored.OwnerUserId = account.OwnerUserId;
        stored.ClientId = account.ClientId;
        stored.ProtectedRefreshToken = _protector.Protect(account.RefreshToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.SpotifyAccounts.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>Singleton (used by <see cref="QueueManager"/>), hence the context factory.</summary>
internal sealed class EfQueueStore(IDbContextFactory<AudioCentralDbContext> dbFactory, TimeProvider timeProvider) : IQueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<QueueDto?> LoadAsync(string playerId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Queues.AsNoTracking().FirstOrDefaultAsync(q => q.PlayerId == playerId, cancellationToken);
        return stored is null ? null : JsonSerializer.Deserialize<QueueDto>(stored.Json, JsonOptions);
    }

    public async Task SaveAsync(QueueDto queue, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var json = JsonSerializer.Serialize(queue, JsonOptions);
        var stored = await db.Queues.FirstOrDefaultAsync(q => q.PlayerId == queue.PlayerId, cancellationToken);
        if (stored is null)
        {
            db.Queues.Add(new StoredQueue { PlayerId = queue.PlayerId, Json = json, UpdatedAt = timeProvider.GetUtcNow() });
        }
        else
        {
            stored.Json = json;
            stored.UpdatedAt = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
