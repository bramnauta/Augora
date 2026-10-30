using System.Collections.ObjectModel;
using System.Diagnostics;
using Augora.Client;
using Augora.Contracts;
using Augora.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Augora.Mobile.ViewModels;

/// <summary>The selected player as the mini player, the player screen and the player picker show it.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly AppState _app = Ioc.Get<AppState>();
    private readonly Stopwatch _since = new();
    private LiveStore? _store;
    private PlayerDto? _base;
    private IDispatcherTimer? _timer;
    private bool _dragging;

    [ObservableProperty]
    private string _title = Strings.NothingPlaying;

    [ObservableProperty]
    private string? _subtitle;

    [ObservableProperty]
    private string? _imageUrl;

    [ObservableProperty]
    private bool _hasMedia;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playPauseIcon = "Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private bool _canSeek;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private double _duration = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private double _position;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private double _volume = 1;

    [ObservableProperty]
    private bool _canSetVolume;

    [ObservableProperty]
    private bool _shuffle;

    [ObservableProperty]
    private RepeatMode _repeat;

    [ObservableProperty]
    private string _repeatIcon = "Repeat";

    [ObservableProperty]
    private string _playerName = "";

    [ObservableProperty]
    private bool _playsOnThisDevice = true;

    [ObservableProperty]
    private bool _isAvailable = true;

    public ObservableCollection<PlayerRow> Players { get; } = [];

    /// <summary>0 to 1, for the thin progress line of the mini player.</summary>
    public double Progress => CanSeek ? Math.Clamp(Position / Duration, 0, 1) : 0;

    /// <summary>Called by the root page after logging in: follow the live state of the current session.</summary>
    public void Attach()
    {
        if (_store is not null)
        {
            _store.Changed -= OnStoreChanged;
        }

        _store = _app.Store;
        _store.Changed += OnStoreChanged;
        _timer ??= CreateTimer();
        _timer.Start();
        Refresh();
    }

    public async Task PlayPauseAsync()
    {
        if (_store?.SelectedPlayer is not { } player)
        {
            return;
        }

        await RunAsync(() => player.State is PlaybackState.Playing or PlaybackState.Buffering
            ? _app.Api.PauseAsync(player.Id)
            : _app.Api.ResumeAsync(player.Id));
    }

    public Task NextAsync() => RunAsync(() => _app.Api.NextAsync(_store!.SelectedPlayerId!));

    public Task PreviousAsync() => RunAsync(() => _app.Api.PreviousAsync(_store!.SelectedPlayerId!));

    public void BeginSeek() => _dragging = true;

    public async Task SeekAsync(double seconds)
    {
        try
        {
            if (_store?.SelectedPlayerId is { } id)
            {
                await RunAsync(() => _app.Api.SeekAsync(id, seconds));
            }
        }
        finally
        {
            _dragging = false;
        }
    }

    public Task SetVolumeAsync(double level) => RunAsync(() => _app.Api.SetVolumeAsync(_store!.SelectedPlayerId!, level));

    public Task ToggleShuffleAsync() => RunAsync(() => _app.Api.SetShuffleAsync(_store!.SelectedPlayerId!, !Shuffle));

    public Task CycleRepeatAsync() => RunAsync(() => _app.Api.SetRepeatAsync(_store!.SelectedPlayerId!, Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    }));

    public Task SelectPlayerAsync(PlayerRow row) => _store!.SelectPlayerAsync(row.Id);

    private async Task RunAsync(Func<Task> action)
    {
        if (_store?.SelectedPlayerId is null)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            await Ui.AlertAsync(Strings.Error, ex.Message);
        }
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Application.Current!.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => UpdatePosition();
        return timer;
    }

    private void OnStoreChanged() => MainThread.BeginInvokeOnMainThread(Refresh);

    private void Refresh()
    {
        if (_store is null)
        {
            return;
        }

        var player = _store.SelectedPlayer;
        var now = player?.NowPlaying;
        HasMedia = now is not null;
        Title = now?.Title ?? Strings.NothingPlaying;
        Subtitle = now?.Subtitle;
        ImageUrl = _app.ResolveUrl(now?.ImageUrl);
        IsPlaying = player?.State is PlaybackState.Playing or PlaybackState.Buffering;
        PlayPauseIcon = IsPlaying ? "Pause" : "Play";
        CanSeek = now is { IsLive: false, Duration: { } d } && d > TimeSpan.Zero && player!.Features.HasFlag(PlayerFeatures.Seek);
        Duration = Math.Max(1, now?.Duration?.TotalSeconds ?? 1);
        DurationText = MediaRow.Format(now?.Duration ?? TimeSpan.Zero);
        CanSetVolume = player?.Features.HasFlag(PlayerFeatures.Volume) == true;
        Volume = player?.Volume ?? 1;
        Shuffle = _store.SelectedQueue?.Shuffle ?? false;
        Repeat = _store.SelectedQueue?.Repeat ?? RepeatMode.Off;
        RepeatIcon = Repeat == RepeatMode.One ? "RepeatOne" : "Repeat";
        PlaysOnThisDevice = _store.PlaysOnThisDevice;
        IsAvailable = player?.Available ?? PlaysOnThisDevice;
        PlayerName = PlaysOnThisDevice ? Strings.ThisDevice(player?.Name ?? "") : player?.Name ?? "";

        // Every status from the server is a new object with a fresh position; other refreshes keep counting.
        if (!ReferenceEquals(player, _base))
        {
            _base = player;
            _since.Restart();
        }

        UpdatePosition();
        RefreshPlayers();
    }

    /// <summary>The reported position plus the time played since it arrived: players report it only now and then.</summary>
    private void UpdatePosition()
    {
        if (_dragging)
        {
            return;
        }

        var elapsed = IsPlaying ? _since.Elapsed : TimeSpan.Zero;
        var position = Math.Min(Duration, ((_base?.NowPlaying?.Position ?? TimeSpan.Zero) + elapsed).TotalSeconds);
        Position = position;
        PositionText = MediaRow.Format(TimeSpan.FromSeconds(position));
    }

    private void RefreshPlayers()
    {
        var store = _store!;
        var rows = store.Players
            .Where(p => p.Available || string.Equals(p.Id, store.SelectedPlayerId, StringComparison.OrdinalIgnoreCase))
            .Select(p => new PlayerRow(
                p.Id,
                string.Equals(p.Id, store.DevicePlayerId, StringComparison.OrdinalIgnoreCase) ? Strings.ThisDevice(p.Name) : p.Name,
                p.State is PlaybackState.Playing ? p.NowPlaying?.Title : null,
                string.Equals(p.Id, store.SelectedPlayerId, StringComparison.OrdinalIgnoreCase),
                p.Available))
            .OrderByDescending(r => string.Equals(r.Id, store.DevicePlayerId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Players.Clear();
        foreach (var row in rows)
        {
            Players.Add(row);
        }
    }
}

/// <summary>A player in the picker.</summary>
public sealed record PlayerRow(string Id, string Name, string? Playing, bool IsSelected, bool Available)
{
    public string? Subtitle => Available ? Playing : Strings.Unavailable;
}
