using System.Collections.Concurrent;

namespace Augora.Core.Players;

/// <summary>
/// Which players currently play as a member of a sync group. Their own queue must then ignore their
/// "playback ended" events: the group's queue decides what comes next.
/// </summary>
public sealed class GroupMembership
{
    private readonly ConcurrentDictionary<string, string> _groups = new(StringComparer.OrdinalIgnoreCase);

    public void Join(string playerId, string groupId) => _groups[playerId] = groupId;

    public void Leave(string playerId) => _groups.TryRemove(playerId, out _);

    /// <summary>Removes the players that still belong to <paramref name="groupId"/>.</summary>
    public void Disband(string groupId)
    {
        foreach (var (playerId, group) in _groups)
        {
            if (group == groupId)
            {
                _groups.TryRemove(playerId, out _);
            }
        }
    }

    public string? GetGroup(string playerId) => _groups.GetValueOrDefault(playerId);
}
