using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;
using AudioCentral.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using AndroidPlaybackState = Android.Media.Session.PlaybackState;
using PlaybackState = AudioCentral.Contracts.PlaybackState;

namespace AudioCentral.Android;

/// <summary>
/// Makes the device an AudioCentral player: registers over the events hub (the same protocol as the browser player),
/// plays the commanded streams with <see cref="MediaPlayer"/> and reports its state. Runs as a foreground service so
/// playback continues with the screen off; the notification and media buttons send commands through the server.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlayerService : Service, AudioManager.IOnAudioFocusChangeListener
{
    private const string ChannelId = "playback";
    private const int NotificationId = 1;
    private const string ActionCommand = "com.nauralta.audiocentral.COMMAND";
    private const string ExtraCommand = "command";
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private AppSettings _settings = null!;
    private ServerClient _server = null!;
    private Handler _main = null!;
    private HubConnection? _hub;
    private MediaSession? _session;
    private MediaPlayer? _player;
    private AudioFocusRequestClass? _focusRequest;
    private WifiManager.WifiLock? _wifiLock;
    private RemoteMediaDto? _media;
    private PlaybackState _state = PlaybackState.Idle;
    private double _volume = 1;
    private bool _muted;
    private string? _playerId;
    private bool _started;

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(PlayerService));
        context.StartForegroundService(intent);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _settings = new AppSettings(this);
        _server = new ServerClient(_settings);
        _main = new Handler(Looper.MainLooper!);

        var channel = new NotificationChannel(ChannelId, GetString(Resource.String.channel_name), NotificationImportance.Low);
        SystemService<NotificationManager>(NotificationService)!.CreateNotificationChannel(channel);

        _session = new MediaSession(this, "AudioCentral");
        _session.SetCallback(new SessionCallback(this));
        _session.Active = true;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, BuildNotification());
        }

        if (intent?.Action == ActionCommand && intent.GetStringExtra(ExtraCommand) is { } command)
        {
            SendCommand(command);
        }
        else if (!_started)
        {
            _started = true;
            _ = Task.Run(() => ConnectAsync(_stopping.Token));
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _stopping.Cancel();
        ReleasePlayer();
        _session?.Release();
        if (_hub is { } hub)
        {
            _ = hub.DisposeAsync().AsTask();
        }

        base.OnDestroy();
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        // A call or another app took the audio: pause and let the server know.
        if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient && _state == PlaybackState.Playing)
        {
            SendCommand("pause");
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_settings.ServerUrl}{EventsHub.Path}", options => options.AccessTokenProvider = _server.GetAccessTokenAsync)
            .WithAutomaticReconnect(new RetryForever())
            .Build();
        hub.On<RemotePlayerCommandDto>(RemotePlayerHub.Command, command => _main.Post(() => Execute(command)));
        hub.On(RemotePlayerHub.Released, () => _main.Post(() =>
        {
            // Another app instance took over this device id.
            ReleasePlayer();
            StopSelf();
        }));
        hub.Reconnected += _ => RegisterAsync();
        _hub = hub;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await hub.StartAsync(cancellationToken);
                await RegisterAsync();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
            {
                if (!_server.HasTokens)
                {
                    // The login expired; the app asks for a new one when it is opened.
                    _main.Post(StopSelf);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task RegisterAsync()
    {
        if (_hub is not { State: HubConnectionState.Connected } hub)
        {
            return;
        }

        var tv = SystemService<UiModeManager>(UiModeService)?.CurrentModeType == global::Android.Content.Res.UiMode.TypeTelevision;
        var name = global::Android.Provider.Settings.Global.GetString(ContentResolver, "device_name") is { Length: > 0 } deviceName
            ? deviceName
            : $"{Build.Manufacturer} {Build.Model}";
        _playerId = await hub.InvokeAsync<string>(RemotePlayerHub.Register, new RegisterPlayerRequest(_settings.DeviceId, name, tv ? PlayerKind.Tv : PlayerKind.Phone));
        await ReportAsync();
    }

    private void Execute(RemotePlayerCommandDto command)
    {
        switch (command.Type)
        {
            case RemotePlayerCommandType.Play when command.Media is { } media:
                Play(media);
                break;
            case RemotePlayerCommandType.Pause when _player is not null && _state == PlaybackState.Playing:
                _player.Pause();
                SetState(PlaybackState.Paused);
                break;
            case RemotePlayerCommandType.Resume when _player is not null && _state == PlaybackState.Paused:
                RequestFocus();
                _player.Start();
                SetState(PlaybackState.Playing);
                break;
            case RemotePlayerCommandType.Stop:
                ReleasePlayer();
                SetState(PlaybackState.Idle);
                break;
            case RemotePlayerCommandType.SetVolume when command.Volume is { } volume:
                _volume = Math.Clamp(volume, 0, 1);
                ApplyVolume();
                _ = ReportAsync();
                break;
            case RemotePlayerCommandType.SetMuted when command.Muted is { } muted:
                _muted = muted;
                ApplyVolume();
                _ = ReportAsync();
                break;
        }
    }

    private void Play(RemoteMediaDto media)
    {
        ReleasePlayer();
        _media = media;
        var url = media.StreamUrl.StartsWith('/') ? _settings.ServerUrl + media.StreamUrl : media.StreamUrl;

        var player = new MediaPlayer();
        player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!);
        player.SetWakeMode(this, WakeLockFlags.Partial);
        player.Prepared += (_, _) =>
        {
            RequestFocus();
            player.Start();
            SetState(PlaybackState.Playing);
        };
        player.Completion += (_, _) =>
        {
            ReleasePlayer();
            SetState(PlaybackState.Idle, RemotePlaybackEvent.Finished);
        };
        player.Error += (_, e) =>
        {
            e.Handled = true;
            ReleasePlayer();
            SetState(PlaybackState.Idle, RemotePlaybackEvent.Error, $"MediaPlayer error {e.What} ({e.Extra})");
        };

        try
        {
            player.SetDataSource(url);
            _player = player;
            ApplyVolume();
            AcquireWifiLock();
            player.PrepareAsync();
            SetState(PlaybackState.Buffering);
        }
        catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.IllegalStateException or Java.Lang.IllegalArgumentException)
        {
            player.Release();
            SetState(PlaybackState.Idle, RemotePlaybackEvent.Error, ex.Message);
        }
    }

    private void SetState(PlaybackState state, RemotePlaybackEvent playbackEvent = RemotePlaybackEvent.None, string? error = null)
    {
        _state = state;
        UpdateSession();
        SystemService<NotificationManager>(NotificationService)!.Notify(NotificationId, BuildNotification());
        _ = ReportAsync(playbackEvent, error);
        if (state == PlaybackState.Playing)
        {
            ScheduleReport();
        }
    }

    /// <summary>Position updates while playing, so the UI's progress bar follows.</summary>
    private void ScheduleReport() => _main.PostDelayed(() =>
    {
        if (_state == PlaybackState.Playing)
        {
            _ = ReportAsync();
            ScheduleReport();
        }
    }, (long)ReportInterval.TotalMilliseconds);

    private async Task ReportAsync(RemotePlaybackEvent playbackEvent = RemotePlaybackEvent.None, string? error = null)
    {
        if (_hub is not { State: HubConnectionState.Connected } hub || _playerId is null)
        {
            return;
        }

        double? position = null;
        try
        {
            position = _player is not null && _state is PlaybackState.Playing or PlaybackState.Paused ? _player.CurrentPosition / 1000.0 : null;
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Not prepared yet.
        }

        try
        {
            await hub.SendAsync(RemotePlayerHub.ReportState, new RemotePlayerStateDto(_state, _volume, _muted, position, playbackEvent, error));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            // Reconnecting; the state is reported again after registering.
        }
    }

    private void ApplyVolume()
    {
        var level = _muted ? 0f : (float)_volume;
        _player?.SetVolume(level, level);
    }

    private void RequestFocus()
    {
        var audio = SystemService<AudioManager>(AudioService)!;
        _focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)!
            .SetOnAudioFocusChangeListener(this)!
            .Build();
        audio.RequestAudioFocus(_focusRequest!);
    }

    private void AcquireWifiLock()
    {
        if (_wifiLock is null)
        {
#pragma warning disable CA1422 // WifiMode.FullHighPerf is deprecated, but the only mode that keeps streaming on all versions.
            _wifiLock = SystemService<WifiManager>(WifiService)?.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "AudioCentral");
#pragma warning restore CA1422
        }

        if (_wifiLock is { IsHeld: false })
        {
            _wifiLock.Acquire();
        }
    }

    private void ReleasePlayer()
    {
        _player?.Release();
        _player = null;
        if (_wifiLock is { IsHeld: true })
        {
            _wifiLock.Release();
        }

        if (_focusRequest is not null)
        {
            SystemService<AudioManager>(AudioService)?.AbandonAudioFocusRequest(_focusRequest);
        }
    }

    private T? SystemService<T>(string name)
        where T : class, global::Android.Runtime.IJavaObject =>
        GetSystemService(name)?.JavaCast<T>();

    private void SendCommand(string command)
    {
        if (_playerId is { } playerId)
        {
            _ = _server.SendPlayerCommandAsync(playerId, command);
        }
    }

    private void UpdateSession()
    {
        if (_session is null)
        {
            return;
        }

        var metadata = new MediaMetadata.Builder()!
            .PutString(MediaMetadata.MetadataKeyTitle, _media?.Title ?? GetString(Resource.String.app_name))!
            .PutString(MediaMetadata.MetadataKeyArtist, _media?.Subtitle ?? "")!;
        if (_media?.DurationSeconds is { } duration)
        {
            metadata.PutLong(MediaMetadata.MetadataKeyDuration, (long)(duration * 1000));
        }

        _session.SetMetadata(metadata.Build());

        var code = _state switch
        {
            PlaybackState.Playing => PlaybackStateCode.Playing,
            PlaybackState.Paused => PlaybackStateCode.Paused,
            PlaybackState.Buffering => PlaybackStateCode.Buffering,
            _ => PlaybackStateCode.Stopped,
        };
        var position = 0L;
        try
        {
            position = _player is not null && _state is PlaybackState.Playing or PlaybackState.Paused ? _player.CurrentPosition : 0;
        }
        catch (Java.Lang.IllegalStateException)
        {
        }

        _session.SetPlaybackState(new AndroidPlaybackState.Builder()!
            .SetActions(AndroidPlaybackState.ActionPlay | AndroidPlaybackState.ActionPause | AndroidPlaybackState.ActionPlayPause
                | AndroidPlaybackState.ActionSkipToNext | AndroidPlaybackState.ActionSkipToPrevious | AndroidPlaybackState.ActionStop)!
            .SetState(code, position, 1f)!
            .Build());
    }

    private Notification BuildNotification()
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var playing = _state is PlaybackState.Playing or PlaybackState.Buffering;
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetContentTitle(_media?.Title ?? GetString(Resource.String.app_name))!
            .SetContentText(_media?.Subtitle ?? GetString(Resource.String.player_idle))!
            .SetContentIntent(open)!
            .SetOngoing(playing)!
            .SetVisibility(NotificationVisibility.Public)!
            .AddAction(Action(global::Android.Resource.Drawable.IcMediaPrevious, Resource.String.action_previous, "previous", 1))!
            .AddAction(playing
                ? Action(global::Android.Resource.Drawable.IcMediaPause, Resource.String.action_pause, "pause", 2)
                : Action(global::Android.Resource.Drawable.IcMediaPlay, Resource.String.action_play, "resume", 2))!
            .AddAction(Action(global::Android.Resource.Drawable.IcMediaNext, Resource.String.action_next, "next", 3))!
            .SetStyle(new Notification.MediaStyle()!.SetMediaSession(_session?.SessionToken)!.SetShowActionsInCompactView(0, 1, 2))!;
        return builder.Build()!;
    }

    private Notification.Action Action(int icon, int title, string command, int requestCode)
    {
        var intent = new Intent(this, typeof(PlayerService)).SetAction(ActionCommand)!.PutExtra(ExtraCommand, command)!;
        var pending = PendingIntent.GetService(this, requestCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Action.Builder(Icon.CreateWithResource(this, icon), GetString(title), pending).Build()!;
    }

    /// <summary>Media buttons (headset, Bluetooth, TV remote) go through the server, like the notification.</summary>
    private sealed class SessionCallback(PlayerService service) : MediaSession.Callback
    {
        public override void OnPlay() => service.SendCommand("resume");

        public override void OnPause() => service.SendCommand("pause");

        public override void OnSkipToNext() => service.SendCommand("next");

        public override void OnSkipToPrevious() => service.SendCommand("previous");

        public override void OnStop() => service.SendCommand("stop");
    }

    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            TimeSpan.FromSeconds(Math.Min(30, 2 + retryContext.PreviousRetryCount * 2));
    }
}
