using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Augora.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The playback notification needs permission from Android 13.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 1);
        }
    }
}
