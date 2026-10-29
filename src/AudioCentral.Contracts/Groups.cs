namespace AudioCentral.Contracts;

/// <param name="PlayerId">The group's own player id (<c>group:...</c>), to play to and control the group.</param>
/// <param name="MemberIds">Player ids of the members: any players except other groups.</param>
public sealed record SyncGroupDto(Guid Id, string PlayerId, string Name, IReadOnlyList<string> MemberIds);

public sealed record SaveSyncGroupRequest(string Name, IReadOnlyList<string> MemberIds);
