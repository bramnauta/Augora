namespace Augora.Players.Groups;

public sealed record SyncGroupDefinition(Guid Id, string Name, IReadOnlyList<string> MemberIds);

/// <summary>Persists sync groups; implemented by the server with EF Core.</summary>
public interface ISyncGroupStore
{
    Task<IReadOnlyList<SyncGroupDefinition>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(SyncGroupDefinition group, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
