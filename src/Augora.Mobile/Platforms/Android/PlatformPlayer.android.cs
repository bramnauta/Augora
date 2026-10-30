using Augora.AndroidPlayer;
using Augora.Client;

namespace Augora.Mobile;

internal static partial class PlatformPlayer
{
    internal static partial AppSettings CreateSettings() => new AndroidAppSettings(global::Android.App.Application.Context);

    internal static partial void Start(AppSettings settings, ServerClient server) => PlayerService.Start(global::Android.App.Application.Context);

    internal static partial void Stop() => PlayerService.Stop(global::Android.App.Application.Context);
}
