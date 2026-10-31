using Augora.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Augora.Tests.Server;

public sealed class PodcastMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"augora-migration-{Guid.NewGuid():N}.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Shared_subscriptions_become_a_copy_for_every_user()
    {
        var options = new DbContextOptionsBuilder<AugoraDbContext>().UseSqlite($"Data Source={_path}").Options;
        await using var db = new AugoraDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("RuntimeSettings", Ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO AspNetUsers (Id, AccessFailedCount, EmailConfirmed, LockoutEnabled, PhoneNumberConfirmed, TwoFactorEnabled) VALUES ('alice', 0, 0, 0, 0, 0), ('bob', 0, 0, 0, 0, 0);
            INSERT INTO Podcasts (Id, FeedUrl, Title, SubscribedAt) VALUES ('11111111-1111-1111-1111-111111111111', 'https://a/feed', 'A', '2025-01-01 00:00:00+00:00'), ('22222222-2222-2222-2222-222222222222', 'https://b/feed', 'B', '2025-01-01 00:00:00+00:00');
            """,
            Ct);

        await db.Database.MigrateAsync(Ct);

        var subscriptions = await db.Podcasts.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(4, subscriptions.Count);
        Assert.Equal(2, subscriptions.Count(s => s.UserId == "alice"));
        Assert.Equal(2, subscriptions.Count(s => s.UserId == "bob"));
        Assert.Equal(4, subscriptions.Select(s => s.Id).Distinct().Count());
        Assert.All(subscriptions, s => Assert.Null(s.LatestEpisodeKey));
    }
}
