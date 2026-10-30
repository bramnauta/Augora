using Augora.Client;

namespace Augora.Mobile;

internal static partial class PlatformPlayer
{
    private static PlayerController? _controller;

    internal static partial AppSettings CreateSettings() => new PreferencesAppSettings();

    // iOS has no services: the controller lives in the app. Background audio (UIBackgroundModes) keeps the app
    // running while something plays; an idle app can be suspended, and then the device is not available as a player.
    internal static partial void Start(AppSettings settings, ServerClient server)
    {
        if (_controller is not null)
        {
            return;
        }

        var controller = _controller = new PlayerController(settings, server, new IosPlayerBackend());
        controller.Released += Stop;
        controller.LoginExpired += Stop;
        controller.Start();
    }

    private static void Stop()
    {
        var controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            _ = controller.DisposeAsync().AsTask();
        }
    }
}
