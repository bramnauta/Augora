using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;
using Augora.Client;
using Augora.Contracts;
using AndroidPlaybackState = Android.Media.Session.PlaybackState;
using PlaybackState = Augora.Contracts.PlaybackState;

namespace Augora.AndroidPlayer;

/// <summary>
/// Plays the streams with <see cref="MediaPlayer"/> and shows them in a media session and notification, so playback
/// continues with the screen off and the notification, headset and TV remote can control it.
/// </summary>
internal sealed class AndroidPlayerBackend : Java.Lang.Object, IPlayerBackend, AudioManager.IOnAudioFocusChangeListener
{
    private const string ChannelId = "playback";

    private readonly PlayerService _service;
    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly MediaSession _session;
    private MediaPlayer? _player;
    private AudioFocusRequestClass? _focusRequest;
    private WifiManager.WifiLock? _wifiLock;
    private RemoteMediaDto? _media;
    private PlaybackState _state = PlaybackState.Idle;

    public AndroidPlayerBackend(PlayerService service)
    {
        _service = service;

        var channel = new NotificationChannel(ChannelId, service.GetString(Resource.String.augora_channel_name), NotificationImportance.Low);
        SystemService<NotificationManager>(Context.NotificationService)!.CreateNotificationChannel(channel);

        _session = new MediaSession(service, "Augora");
        _session.SetCallback(new SessionCallback(this));
        _session.Active = true;
    }

    public event Action? Ready;

    public event Action? Completed;

    public event Action<string>? Failed;

    public event Action<string>? CommandRequested;

    public PlayerKind Kind =>
        SystemService<UiModeManager>(Context.UiModeService)?.CurrentModeType == global::Android.Content.Res.UiMode.TypeTelevision
            ? PlayerKind.Tv
            : PlayerKind.Phone;

    public string DeviceName =>
        global::Android.Provider.Settings.Global.GetString(_service.ContentResolver, "device_name") is { Length: > 0 } deviceName
            ? deviceName
            : $"{Build.Manufacturer} {Build.Model}";

    public double? PositionSeconds
    {
        get
        {
            try
            {
                return _player?.CurrentPosition / 1000.0;
            }
            catch (Java.Lang.IllegalStateException)
            {
                // Not prepared yet.
                return null;
            }
        }
    }

    public void Post(Action action) => _main.Post(action);

    public void Load(Uri url)
    {
        Release();

        var player = new MediaPlayer();
        player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!);
        player.SetWakeMode(_service, WakeLockFlags.Partial);
        player.Prepared += (_, _) => Ready?.Invoke();
        player.Completion += (_, _) => Completed?.Invoke();
        player.Error += (_, e) =>
        {
            e.Handled = true;
            Failed?.Invoke($"MediaPlayer error {e.What} ({e.Extra})");
        };

        try
        {
            player.SetDataSource(url.AbsoluteUri);
            _player = player;
            AcquireWifiLock();
            player.PrepareAsync();
        }
        catch
        {
            _player = null;
            player.Release();
            throw;
        }
    }

    public void Start()
    {
        RequestFocus();
        _player?.Start();
    }

    public void Pause() => _player?.Pause();

    public void Seek(double seconds) => _player?.SeekTo((int)(seconds * 1000));

    public void SetVolume(double level) => _player?.SetVolume((float)level, (float)level);

    public void Release()
    {
        _player?.Release();
        _player = null;
        if (_wifiLock is { IsHeld: true })
        {
            _wifiLock.Release();
        }

        if (_focusRequest is not null)
        {
            SystemService<AudioManager>(Context.AudioService)?.AbandonAudioFocusRequest(_focusRequest);
        }
    }

    public void ShowNowPlaying(RemoteMediaDto? media, PlaybackState state)
    {
        _media = media;
        _state = state;
        UpdateSession();
        SystemService<NotificationManager>(Context.NotificationService)!.Notify(PlayerService.NotificationId, BuildNotification());
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        // A call or another app took the audio: pause and let the server know.
        if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient && _state == PlaybackState.Playing)
        {
            CommandRequested?.Invoke("pause");
        }
    }

    internal Notification BuildNotification()
    {
        var open = _service.PackageManager!.GetLaunchIntentForPackage(_service.PackageName!);
        var builder = new Notification.Builder(_service, ChannelId)
            .SetSmallIcon(Resource.Drawable.augora_notification)!
            .SetContentTitle(_media?.Title ?? AppName)!
            .SetContentText(_media?.Subtitle ?? _service.GetString(Resource.String.augora_player_idle))!
            .SetOngoing(_state is PlaybackState.Playing or PlaybackState.Buffering)!
            .SetVisibility(NotificationVisibility.Public)!
            .AddAction(Action(global::Android.Resource.Drawable.IcMediaPrevious, Resource.String.augora_action_previous, "previous", 1))!
            .AddAction(_state is PlaybackState.Playing or PlaybackState.Buffering
                ? Action(global::Android.Resource.Drawable.IcMediaPause, Resource.String.augora_action_pause, "pause", 2)
                : Action(global::Android.Resource.Drawable.IcMediaPlay, Resource.String.augora_action_play, "resume", 2))!
            .AddAction(Action(global::Android.Resource.Drawable.IcMediaNext, Resource.String.augora_action_next, "next", 3))!
            .SetStyle(new Notification.MediaStyle()!.SetMediaSession(_session.SessionToken)!.SetShowActionsInCompactView(0, 1, 2))!;
        if (open is not null)
        {
            builder.SetContentIntent(PendingIntent.GetActivity(_service, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        }

        return builder.Build()!;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
            _session.Release();
        }

        base.Dispose(disposing);
    }

    private string AppName => _service.ApplicationInfo!.LoadLabel(_service.PackageManager!);

    private T? SystemService<T>(string name)
        where T : class, IJavaObject =>
        _service.GetSystemService(name)?.JavaCast<T>();

    private void RequestFocus()
    {
        var audio = SystemService<AudioManager>(Context.AudioService)!;
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
            _wifiLock = SystemService<WifiManager>(Context.WifiService)?.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "Augora");
#pragma warning restore CA1422
        }

        if (_wifiLock is { IsHeld: false })
        {
            _wifiLock.Acquire();
        }
    }

    private void UpdateSession()
    {
        var metadata = new MediaMetadata.Builder()!
            .PutString(MediaMetadata.MetadataKeyTitle, _media?.Title ?? AppName)!
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
        var position = _state is PlaybackState.Playing or PlaybackState.Paused ? (long)((PositionSeconds ?? 0) * 1000) : 0;

        _session.SetPlaybackState(new AndroidPlaybackState.Builder()!
            .SetActions(AndroidPlaybackState.ActionPlay | AndroidPlaybackState.ActionPause | AndroidPlaybackState.ActionPlayPause
                | AndroidPlaybackState.ActionSkipToNext | AndroidPlaybackState.ActionSkipToPrevious | AndroidPlaybackState.ActionStop)!
            .SetState(code, position, 1f)!
            .Build());
    }

    private Notification.Action Action(int icon, int title, string command, int requestCode)
    {
        var intent = new Intent(_service, typeof(PlayerService)).SetAction(PlayerService.ActionCommand)!.PutExtra(PlayerService.ExtraCommand, command)!;
        var pending = PendingIntent.GetService(_service, requestCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Action.Builder(Icon.CreateWithResource(_service, icon), _service.GetString(title), pending).Build()!;
    }

    /// <summary>Media buttons (headset, Bluetooth, TV remote) go through the server, like the notification.</summary>
    private sealed class SessionCallback(AndroidPlayerBackend backend) : MediaSession.Callback
    {
        public override void OnPlay() => backend.CommandRequested?.Invoke("resume");

        public override void OnPause() => backend.CommandRequested?.Invoke("pause");

        public override void OnSkipToNext() => backend.CommandRequested?.Invoke("next");

        public override void OnSkipToPrevious() => backend.CommandRequested?.Invoke("previous");

        public override void OnStop() => backend.CommandRequested?.Invoke("stop");
    }
}
