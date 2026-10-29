using System.Collections.Concurrent;
using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Players;
using AudioCentral.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Players.Groups;

/// <summary>
/// AudioCentral's own sync groups: user-defined groups of players of any kind (Cast, browser, phone, TV).
/// Google Cast groups stay Cast players; use those for Cast-only rooms, they are synchronised by the speakers.
/// </summary>
public sealed class GroupPlayerProvider(
    IServiceProvider services,
    StreamSessionManager sessions,
    IStreamUrlProvider urls,
    StreamRelay relay,
    GroupMembership membership,
    ILogger<GroupPlayerProvider> logger) : IPlayerProvider, IHostedService, IDisposable
{
    public const string ProviderId = "group";

    private readonly ConcurrentDictionary<Guid, GroupPlayer> _groups = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => ProviderId;

    public string Name => "Sync groups";

    public IReadOnlyCollection<IPlayer> Players => _groups.Values.ToList<IPlayer>();

    public event EventHandler<IPlayer>? PlayerAdded;

    public event EventHandler<IPlayer>? PlayerRemoved;

    public IReadOnlyList<SyncGroupDto> Groups =>
        _groups.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToDto).ToList();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        foreach (var definition in await scope.ServiceProvider.GetRequiredService<ISyncGroupStore>().GetAllAsync(cancellationToken))
        {
            Add(definition);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<SyncGroupDto> SaveAsync(Guid? id, SaveSyncGroupRequest request, CancellationToken cancellationToken)
    {
        var definition = Validate(id ?? Guid.CreateVersion7(), request);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (id is { } existingId && !_groups.ContainsKey(existingId))
            {
                throw new AudioCentralException(ErrorCodes.PlayerNotFound, "The group does not exist.", 404);
            }

            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISyncGroupStore>().SaveAsync(definition, cancellationToken);
            if (_groups.TryGetValue(definition.Id, out var group))
            {
                group.Definition = definition;
                group.UpdateStatus();
                return ToDto(group);
            }

            return ToDto(Add(definition));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISyncGroupStore>().DeleteAsync(id, cancellationToken);
            if (!_groups.TryRemove(id, out var group))
            {
                return false;
            }

            await group.StopAsync(cancellationToken);
            group.Dispose();
            PlayerRemoved?.Invoke(this, group);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var group in _groups.Values)
        {
            group.Dispose();
        }

        _lock.Dispose();
    }

    private GroupPlayer Add(SyncGroupDefinition definition)
    {
        var group = new GroupPlayer(definition, Resolve, sessions, urls, relay, membership, logger);
        _groups[definition.Id] = group;
        PlayerAdded?.Invoke(this, group);
        return group;
    }

    // Resolved lazily: the PlayerManager itself depends on this provider.
    private IPlayer? Resolve(string playerId) =>
        services.GetRequiredService<PlayerManager>().Players.FirstOrDefault(p => p.Id.Equals(playerId, StringComparison.OrdinalIgnoreCase));

    private SyncGroupDefinition Validate(Guid id, SaveSyncGroupRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new AudioCentralException(ErrorCodes.InvalidGroup, "A group needs a name.");
        }

        var members = (request.MemberIds ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (members.Count < 2)
        {
            throw new AudioCentralException(ErrorCodes.InvalidGroup, "A group needs at least two players.");
        }

        if (members.Any(m => m.StartsWith(GroupPlayer.IdPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AudioCentralException(ErrorCodes.InvalidGroup, "A group cannot contain another group.");
        }

        if (members.FirstOrDefault(m => Resolve(m) is null) is { } unknown)
        {
            throw new AudioCentralException(ErrorCodes.PlayerNotFound, $"Player '{unknown}' does not exist.", 404);
        }

        return new SyncGroupDefinition(id, name, members);
    }

    private static SyncGroupDto ToDto(GroupPlayer group) =>
        new(group.Definition.Id, group.Id, group.Name, group.Definition.MemberIds);
}
