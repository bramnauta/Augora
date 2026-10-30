using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Augora.Client;

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.WakeLock)]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK")]
[assembly: UsesPermission(Android.Manifest.Permission.PostNotifications)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessWifiState)]

namespace Augora.AndroidPlayer;

/// <summary>
/// Makes the device an Augora player (see <see cref="PlayerController"/>). Runs as a foreground service so playback
/// continues with the screen off; the notification and media buttons send commands through the server.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlayerService : Service
{
    internal const int NotificationId = 1;
    internal const string ActionCommand = "com.nauralta.augora.COMMAND";
    internal const string ExtraCommand = "command";

    private ServerClient _server = null!;
    private AndroidPlayerBackend _backend = null!;
    private PlayerController _controller = null!;

    public static void Start(Context context) => context.StartForegroundService(new Intent(context, typeof(PlayerService)));

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(PlayerService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        var settings = new AndroidAppSettings(this);
        _server = new ServerClient(settings);
        _backend = new AndroidPlayerBackend(this);
        _controller = new PlayerController(settings, _server, _backend);
        _controller.Released += StopSelf;
        _controller.LoginExpired += StopSelf;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, _backend.BuildNotification(), ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, _backend.BuildNotification());
        }

        if (intent?.Action == ActionCommand && intent.GetStringExtra(ExtraCommand) is { } command)
        {
            _controller.SendCommand(command);
        }
        else
        {
            _controller.Start();
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        var controller = _controller;
        var backend = _backend;
        var server = _server;
        _ = Task.Run(async () =>
        {
            await controller.DisposeAsync();
            backend.Dispose();
            server.Dispose();
        });

        base.OnDestroy();
    }
}
