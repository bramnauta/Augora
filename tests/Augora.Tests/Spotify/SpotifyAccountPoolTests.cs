using Augora.Providers.Spotify;
using Microsoft.Extensions.Time.Testing;

namespace Augora.Tests.Spotify;

public sealed class SpotifyAccountPoolTests
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMinutes(2);

    private static readonly SpotifyAccount Alice = new("alice", "Alice", "user-alice", null, "r1");
    private static readonly SpotifyAccount Shared = new("family", "Family", null, null, "r2");
    private static readonly SpotifyAccount Bob = new("bob", "Bob", "user-bob", null, "r3");

    private readonly FakeTimeProvider _time = new();
    private readonly SpotifyAccountPool _pool;

    public SpotifyAccountPoolTests() => _pool = new SpotifyAccountPool(_time, HoldTime);

    [Fact]
    public void Prefers_own_then_shared_then_other_accounts()
    {
        SpotifyAccount[] accounts = [Bob, Shared, Alice];

        Assert.Equal("alice", _pool.Acquire(accounts, "kitchen", "user-alice", null).Account?.Id);
        Assert.Equal("family", _pool.Acquire(accounts, "living", "user-alice", null).Account?.Id);
        Assert.Equal("bob", _pool.Acquire(accounts, "bedroom", "user-alice", null).Account?.Id);
    }

    [Fact]
    public void A_player_keeps_its_account()
    {
        SpotifyAccount[] accounts = [Alice, Shared];
        _pool.Acquire(accounts, "kitchen", "user-bob", null);

        var again = _pool.Acquire(accounts, "kitchen", "user-alice", null);

        Assert.Equal("family", again.Account?.Id);
    }

    [Fact]
    public void Without_a_free_account_the_holders_are_returned()
    {
        SpotifyAccount[] accounts = [Alice, Bob];
        _pool.Acquire(accounts, "kitchen", "user-alice", null);
        _pool.Acquire(accounts, "living", "user-bob", null);

        var result = _pool.Acquire(accounts, "bedroom", "user-alice", null);

        Assert.Null(result.Account);
        Assert.Equal(["kitchen", "living"], result.Holders.Select(h => h.PlayerId).Order());
    }

    [Fact]
    public void Only_own_and_shared_accounts_can_be_taken_over()
    {
        SpotifyAccount[] accounts = [Alice, Bob];
        _pool.Acquire(accounts, "kitchen", "user-alice", null);
        _pool.Acquire(accounts, "living", "user-bob", null);

        Assert.Null(_pool.Acquire(accounts, "bedroom", "user-alice", takeoverPlayerId: "living").Account);

        var takeover = _pool.Acquire(accounts, "bedroom", "user-alice", takeoverPlayerId: "kitchen");
        Assert.Equal("alice", takeover.Account?.Id);
        Assert.Equal("kitchen", takeover.TakenFromPlayerId);
        Assert.False(_pool.StreamStarted("alice", "kitchen"));
        Assert.True(_pool.StreamStarted("alice", "bedroom"));
    }

    [Fact]
    public void An_account_is_free_again_after_the_hold_time_unless_it_streams()
    {
        SpotifyAccount[] accounts = [Alice];
        _pool.Acquire(accounts, "kitchen", "user-alice", null);
        Assert.Null(_pool.Acquire(accounts, "living", "user-alice", null).Account);

        _time.Advance(HoldTime + TimeSpan.FromSeconds(1));
        Assert.Equal("alice", _pool.Acquire(accounts, "living", "user-alice", null).Account?.Id);

        Assert.True(_pool.StreamStarted("alice", "living"));
        _time.Advance(HoldTime * 3);
        Assert.Null(_pool.Acquire(accounts, "kitchen", "user-alice", null).Account);

        _pool.StreamEnded("alice", "living");
        _time.Advance(HoldTime + TimeSpan.FromSeconds(1));
        Assert.Equal("alice", _pool.Acquire(accounts, "kitchen", "user-alice", null).Account?.Id);
    }
}
