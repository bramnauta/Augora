using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AudioCentral.Core.Streaming;

public sealed record StreamSession(string Token, string PlayerId, ResolvedStream Stream, DateTimeOffset CreatedAt);

/// <summary>
/// Hands out short-lived, unguessable stream urls. Players such as Google Cast cannot send
/// authentication headers, so possession of the token is the authorisation.
/// </summary>
public sealed class StreamSessionManager(TimeProvider timeProvider)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();

    public StreamSession Create(string playerId, ResolvedStream stream)
    {
        RemoveExpired();

        // A player plays one stream at a time, so its previous session is no longer needed.
        foreach (var old in _sessions.Values.Where(s => s.PlayerId == playerId))
        {
            _sessions.TryRemove(old.Token, out _);
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var session = new StreamSession(token, playerId, stream, timeProvider.GetUtcNow());
        _sessions[token] = session;
        return session;
    }

    public StreamSession? Get(string token) =>
        _sessions.TryGetValue(token, out var session) && !IsExpired(session) ? session : null;

    private void RemoveExpired()
    {
        foreach (var session in _sessions.Values.Where(IsExpired))
        {
            _sessions.TryRemove(session.Token, out _);
        }
    }

    private bool IsExpired(StreamSession session) => timeProvider.GetUtcNow() - session.CreatedAt > MaxAge;
}
