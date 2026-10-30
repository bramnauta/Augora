using Augora.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Augora.Client;

/// <summary>
/// What the app shows: all players, the selected player and its queue, kept current through the events hub.
/// The app starts on its own player, so music plays on the device itself unless the user picks another one.
/// </summary>
public sealed class LiveStore(ApiClient api, ServerClient server, AppSettings settings) : IAsyncDisposable
{
    private readonly Dictionary<string, PlayerDto> _players = new(StringComparer.OrdinalIgnoreCase);
    private HubConnection? _hub;

    /// <summary>Raised from the hub's thread.</summary>
    public event Action? Changed;

    /// <summary>The id the server gives this device's own player once it registered (see <see cref="PlayerController"/>).</summary>
    public string DevicePlayerId => $"remote:{settings.DeviceId}";

    public IReadOnlyList<PlayerDto> Players => _players.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public string? SelectedPlayerId { get; private set; }

    public PlayerDto? SelectedPlayer => SelectedPlayerId is not null && _players.TryGetValue(SelectedPlayerId, out var p) ? p : null;

    public QueueDto? SelectedQueue { get; private set; }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>Whether the selected player is this device.</summary>
    public bool PlaysOnThisDevice => string.Equals(SelectedPlayerId, DevicePlayerId, StringComparison.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_hub is not null)
        {
            return;
        }

        var hub = _hub = new HubConnectionBuilder()
            .WithUrl($"{settings.ServerUrl}{EventsHub.Path}", options => options.AccessTokenProvider = server.GetAccessTokenAsync)
            .WithAutomaticReconnect(new RetryForever())
            .Build();
        hub.On<PlayerDto>(EventsHub.PlayerUpdated, player =>
        {
            var isNew = _players.TryAdd(player.Id, player);
            _players[player.Id] = player;
            if (isNew && string.Equals(player.Id, SelectedPlayerId, StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshQueueAsync();
            }

            Changed?.Invoke();
        });
        hub.On<string>(EventsHub.PlayerRemoved, id =>
        {
            _players.Remove(id);
            Changed?.Invoke();
        });
        hub.On<QueueDto>(EventsHub.QueueUpdated, queue =>
        {
            if (string.Equals(queue.PlayerId, SelectedPlayerId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedQueue = queue;
                Changed?.Invoke();
            }
        });
        hub.Reconnecting += _ => Notify();
        hub.Closed += _ => Notify();
        hub.Reconnected += async _ =>
        {
            await LoadPlayersAsync();
            await RefreshQueueAsync();
        };

        // Choose first: the own player registers a moment later, and the selection waits for it.
        SelectedPlayerId = DevicePlayerId;
        while (true)
        {
            try
            {
                await hub.StartAsync(cancellationToken);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        await LoadPlayersAsync();
        await RefreshQueueAsync();
    }

    public async Task SelectPlayerAsync(string playerId)
    {
        SelectedPlayerId = playerId;
        SelectedQueue = null;
        Changed?.Invoke();
        await RefreshQueueAsync();
    }

    public async Task RefreshQueueAsync()
    {
        if (SelectedPlayerId is not { } id)
        {
            return;
        }

        try
        {
            SelectedQueue = await api.GetQueueAsync(id);
        }
        catch (ApiException)
        {
            // The own player is not registered yet; the queue arrives with its first update.
            SelectedQueue = null;
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is { } hub)
        {
            _hub = null;
            await hub.DisposeAsync();
        }
    }

    private async Task LoadPlayersAsync()
    {
        _players.Clear();
        foreach (var player in await api.GetPlayersAsync())
        {
            _players[player.Id] = player;
        }

        Changed?.Invoke();
    }

    private Task Notify()
    {
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
