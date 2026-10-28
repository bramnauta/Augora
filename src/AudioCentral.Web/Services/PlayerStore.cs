using AudioCentral.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace AudioCentral.Web.Services;

/// <summary>
/// Client-side state: all players, the selected player and its queue, kept current through SignalR.
/// </summary>
public sealed class PlayerStore(ApiClient api, NavigationManager navigation, IJSRuntime js) : IAsyncDisposable
{
    private const string SelectedPlayerKey = "audiocentral.selectedPlayer";

    private readonly Dictionary<string, PlayerDto> _players = new(StringComparer.OrdinalIgnoreCase);
    private HubConnection? _hub;
    private Task? _initialization;

    public event Action? Changed;

    public IReadOnlyList<PlayerDto> Players => _players.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public string? SelectedPlayerId { get; private set; }

    public PlayerDto? SelectedPlayer => SelectedPlayerId is not null && _players.TryGetValue(SelectedPlayerId, out var p) ? p : null;

    public QueueDto? SelectedQueue { get; private set; }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    public async Task SelectPlayerAsync(string? playerId)
    {
        SelectedPlayerId = playerId;
        SelectedQueue = null;
        await js.InvokeVoidAsync("localStorage.setItem", SelectedPlayerKey, playerId ?? "");
        await RefreshQueueAsync();
        Changed?.Invoke();
    }

    public async Task RefreshQueueAsync()
    {
        if (SelectedPlayerId is null)
        {
            return;
        }

        try
        {
            SelectedQueue = await api.GetQueueAsync(SelectedPlayerId);
        }
        catch (ApiException)
        {
            SelectedQueue = null;
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(EventsHub.Path))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<PlayerDto>(EventsHub.PlayerUpdated, player =>
        {
            _players[player.Id] = player;
            Changed?.Invoke();
        });
        _hub.On<string>(EventsHub.PlayerRemoved, id =>
        {
            _players.Remove(id);
            Changed?.Invoke();
        });
        _hub.On<QueueDto>(EventsHub.QueueUpdated, queue =>
        {
            if (string.Equals(queue.PlayerId, SelectedPlayerId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedQueue = queue;
                Changed?.Invoke();
            }
        });
        _hub.Reconnecting += _ => Notify();
        _hub.Reconnected += async _ =>
        {
            await LoadPlayersAsync();
            Changed?.Invoke();
        };
        _hub.Closed += _ => Notify();

        await _hub.StartAsync();
        await LoadPlayersAsync();

        var stored = await js.InvokeAsync<string?>("localStorage.getItem", SelectedPlayerKey);
        var initial = !string.IsNullOrEmpty(stored) && _players.ContainsKey(stored) ? stored : Players.FirstOrDefault(p => p.Available)?.Id;
        await SelectPlayerAsync(initial);
    }

    private async Task LoadPlayersAsync()
    {
        var players = await api.GetPlayersAsync();
        _players.Clear();
        foreach (var player in players)
        {
            _players[player.Id] = player;
        }
    }

    private Task Notify()
    {
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
