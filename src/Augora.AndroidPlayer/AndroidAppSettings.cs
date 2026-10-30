using Android.Content;
using Augora.Client;

namespace Augora.AndroidPlayer;

/// <summary>The server address, device id and native-player tokens, in the app's private preferences.</summary>
public sealed class AndroidAppSettings(Context context) : AppSettings
{
    private readonly ISharedPreferences _preferences = context.GetSharedPreferences("augora_client", FileCreationMode.Private)!;

    protected override string? Read(string key) => _preferences.GetString(key, null);

    protected override void Write(string key, string? value)
    {
        var editor = _preferences.Edit()!;
        if (value is null)
        {
            editor.Remove(key);
        }
        else
        {
            editor.PutString(key, value);
        }

        editor.Apply();
    }
}
