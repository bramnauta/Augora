namespace AudioCentral.Providers.Spotify;

/// <summary>
/// Spotify allows one stream per account, so every account is leased to at most one player. A lease lasts while the
/// player streams and for <see cref="SpotifyOptions.AccountHoldTime"/> after, so the next queue item keeps the account.
/// </summary>
internal sealed class SpotifyAccountPool(TimeProvider timeProvider, TimeSpan holdTime)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.OrdinalIgnoreCase);

    public static bool CanTakeOver(SpotifyAccount account, string? userId) =>
        account.OwnerUserId is null || account.OwnerUserId == userId;

    /// <summary>The account leased to each player that is currently holding one.</summary>
    public IReadOnlyDictionary<string, string> GetHolders()
    {
        lock (_lock)
        {
            return _leases.Values.Where(IsHeld).ToDictionary(l => l.AccountId, l => l.PlayerId, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Picks an account for <paramref name="playerId"/>: the one it already holds, else a free one (own accounts of
    /// <paramref name="userId"/> first, then shared ones, then those of others), else the one of
    /// <paramref name="takeoverPlayerId"/> when allowed.
    /// </summary>
    public Allocation Acquire(IReadOnlyList<SpotifyAccount> accounts, string playerId, string? userId, string? takeoverPlayerId)
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            var current = _leases.Values.FirstOrDefault(l => l.PlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase) && IsHeld(l));
            if (current is not null && accounts.FirstOrDefault(a => a.Id == current.AccountId) is { } held)
            {
                current.LastActivity = now;
                return new Allocation(held, null, []);
            }

            var free = accounts
                .Where(a => !_leases.TryGetValue(a.Id, out var lease) || !IsHeld(lease))
                .OrderBy(a => a.OwnerUserId is not null && a.OwnerUserId == userId ? 0 : a.OwnerUserId is null ? 1 : 2)
                .FirstOrDefault();
            if (free is not null)
            {
                Release(playerId);
                _leases[free.Id] = new Lease(free.Id, playerId) { LastActivity = now };
                return new Allocation(free, null, []);
            }

            if (takeoverPlayerId is not null)
            {
                var victim = _leases.Values.FirstOrDefault(l => l.PlayerId.Equals(takeoverPlayerId, StringComparison.OrdinalIgnoreCase) && IsHeld(l));
                if (victim is not null && accounts.FirstOrDefault(a => a.Id == victim.AccountId) is { } account && CanTakeOver(account, userId))
                {
                    Release(playerId);
                    _leases[account.Id] = new Lease(account.Id, playerId) { LastActivity = now };
                    return new Allocation(account, victim.PlayerId, []);
                }
            }

            var holders = _leases.Values.Where(IsHeld)
                .Select(l => (Account: accounts.FirstOrDefault(a => a.Id == l.AccountId), l.PlayerId))
                .Where(h => h.Account is not null)
                .Select(h => (h.Account!, h.PlayerId))
                .ToList();
            return new Allocation(null, null, holders);
        }
    }

    /// <summary>Marks the start of a stream; returns <c>false</c> when the account was taken over meanwhile.</summary>
    public bool StreamStarted(string accountId, string playerId)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(accountId, out var lease) || !lease.PlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            lease.ActiveStreams++;
            lease.LastActivity = timeProvider.GetUtcNow();
            return true;
        }
    }

    public void StreamEnded(string accountId, string playerId)
    {
        lock (_lock)
        {
            if (_leases.TryGetValue(accountId, out var lease) && lease.PlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase))
            {
                lease.ActiveStreams = Math.Max(0, lease.ActiveStreams - 1);
                lease.LastActivity = timeProvider.GetUtcNow();
            }
        }
    }

    public bool IsLeasedTo(string accountId, string playerId)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(accountId, out var lease) && lease.PlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Release(string playerId)
    {
        lock (_lock)
        {
            foreach (var lease in _leases.Values.Where(l => l.PlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _leases.Remove(lease.AccountId);
            }
        }
    }

    public void Forget(string accountId)
    {
        lock (_lock)
        {
            _leases.Remove(accountId);
        }
    }

    private bool IsHeld(Lease lease) => lease.ActiveStreams > 0 || timeProvider.GetUtcNow() - lease.LastActivity < holdTime;

    /// <param name="Account">The account to use, <c>null</c> when none is available.</param>
    /// <param name="TakenFromPlayerId">The player that must be stopped because its account was taken over.</param>
    /// <param name="Holders">When no account is available: who holds them.</param>
    public sealed record Allocation(SpotifyAccount? Account, string? TakenFromPlayerId, IReadOnlyList<(SpotifyAccount Account, string PlayerId)> Holders);

    private sealed class Lease(string accountId, string playerId)
    {
        public string AccountId { get; } = accountId;

        public string PlayerId { get; } = playerId;

        public int ActiveStreams { get; set; }

        public DateTimeOffset LastActivity { get; set; }
    }
}
