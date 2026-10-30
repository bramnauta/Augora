using Augora.Client;

namespace Augora.Mobile;

/// <summary>The server address, device id and native-player tokens, in the app's preferences.</summary>
internal sealed class PreferencesAppSettings : AppSettings
{
    protected override string? Read(string key) => Preferences.Default.Get<string?>(key, null);

    protected override void Write(string key, string? value)
    {
        if (value is null)
        {
            Preferences.Default.Remove(key);
        }
        else
        {
            Preferences.Default.Set(key, value);
        }
    }
}
