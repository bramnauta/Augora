using System.Text.Json;
using Augora.Contracts;
using Augora.Core;
using Augora.Core.Queue;
using Augora.Players.GoogleCast;
using Augora.Players.Groups;
using Augora.Providers.Podcasts;
using Augora.Providers.Spotify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Augora.Server.Data;

internal sealed class EfRuntimeSettingsStore(IDbContextFactory<AugoraDbContext> dbFactory) : IRuntimeSettingsStore
{
    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
    }

    public async Task SaveAsync(string key, string? value, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (value is null)
        {
            await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var stored = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (stored is null)
        {
            db.Settings.Add(new StoredSetting { Key = key, Value = value });
        }
        else
        {
            stored.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class EfCastDeviceStore(AugoraDbContext db) : ICastDeviceStore
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
internal sealed class EfSpotifyAccountStore(IDbContextFactory<AugoraDbContext> dbFactory, IDataProtectionProvider dataProtection, TimeProvider timeProvider)
    : ISpotifyAccountStore
{
    // The purpose is part of the encryption: it keeps its pre-rename value so saved refresh tokens stay readable.
    private readonly IDataProtector _protector = dataProtection.CreateProtector("AudioCentral.Spotify.RefreshToken");

    public async Task<IReadOnlyList<SpotifyAccount>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.SpotifyAccounts.AsNoTracking().ToListAsync(cancellationToken);
        // SQLite cannot order by DateTimeOffset; there are only a handful of accounts.
        return stored
            .OrderBy(a => a.LinkedAt)
            .Select(a => new SpotifyAccount(a.Id, a.DisplayName, a.OwnerUserId, a.ClientId, _protector.Unprotect(a.ProtectedRefreshToken),
                a.Engine, a.ProtectedSoloistApiKey is null ? null : _protector.Unprotect(a.ProtectedSoloistApiKey)))
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
        stored.Engine = account.Engine;
        stored.ProtectedSoloistApiKey = account.SoloistApiKey is null ? null : _protector.Protect(account.SoloistApiKey);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.SpotifyAccounts.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}

internal sealed class EfPodcastStore(IDbContextFactory<AugoraDbContext> dbFactory, TimeProvider timeProvider) : IPodcastStore
{
    public async Task<IReadOnlyList<PodcastSubscription>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Podcasts.AsNoTracking()
            .OrderBy(p => p.Title)
            .Select(p => new PodcastSubscription(p.Id, p.FeedUrl, p.Title, p.Author, p.ImageUrl))
            .ToListAsync(cancellationToken);
    }

    public async Task<PodcastSubscription> AddAsync(PodcastSubscription subscription, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Podcasts.Add(new StoredPodcast
        {
            Id = subscription.Id,
            FeedUrl = subscription.FeedUrl,
            Title = subscription.Title,
            Author = subscription.Author,
            ImageUrl = subscription.ImageUrl,
            SubscribedAt = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return subscription;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Podcasts.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }
}

internal sealed class EfSyncGroupStore(AugoraDbContext db) : ISyncGroupStore
{
    public async Task<IReadOnlyList<SyncGroupDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
        (await db.SyncGroups.AsNoTracking().ToListAsync(cancellationToken))
            .Select(g => new SyncGroupDefinition(g.Id, g.Name, g.MemberIds))
            .ToList();

    public async Task SaveAsync(SyncGroupDefinition group, CancellationToken cancellationToken)
    {
        var stored = await db.SyncGroups.FirstOrDefaultAsync(g => g.Id == group.Id, cancellationToken);
        if (stored is null)
        {
            stored = new StoredSyncGroup { Id = group.Id, Name = group.Name };
            db.SyncGroups.Add(stored);
        }

        stored.Name = group.Name;
        stored.MemberIds = [.. group.MemberIds];
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await db.SyncGroups.Where(g => g.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
}

/// <summary>Singleton (used by <see cref="QueueManager"/>), hence the context factory.</summary>
internal sealed class EfQueueStore(IDbContextFactory<AugoraDbContext> dbFactory, TimeProvider timeProvider) : IQueueStore
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
