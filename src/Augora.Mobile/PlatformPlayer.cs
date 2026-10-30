using Augora.Client;

namespace Augora.Mobile;

/// <summary>The native player: a foreground service on Android, an in-process controller on iOS.</summary>
internal static partial class PlatformPlayer
{
    internal static partial AppSettings CreateSettings();

    /// <summary>Makes the device a player. Safe to call again.</summary>
    internal static partial void Start(AppSettings settings, ServerClient server);
}
