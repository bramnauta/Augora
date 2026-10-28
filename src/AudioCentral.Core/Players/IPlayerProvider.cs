namespace AudioCentral.Core.Players;

/// <summary>
/// Discovers and owns players of one kind. Providers announce players through the events;
/// the <see cref="PlayerManager"/> aggregates them.
/// </summary>
public interface IPlayerProvider
{
    string Id { get; }

    string Name { get; }

    IReadOnlyCollection<IPlayer> Players { get; }

    event EventHandler<IPlayer>? PlayerAdded;

    event EventHandler<IPlayer>? PlayerRemoved;
}
