using Android.Content;

namespace AudioCentral.Android;

/// <summary>The server address, device id and native-player tokens, in the app's private preferences.</summary>
internal sealed class AppSettings(Context context)
{
    private readonly ISharedPreferences _preferences = context.GetSharedPreferences("audiocentral", FileCreationMode.Private)!;

    /// <summary>Without trailing slash, e.g. <c>http://192.168.1.10:4727</c>.</summary>
    public string? ServerUrl
    {
        get => _preferences.GetString("server", null);
        set => Edit(e => e.PutString("server", value?.TrimEnd('/')));
    }

    /// <summary>Stable per installation, so the player keeps its id and queue.</summary>
    public string DeviceId
    {
        get
        {
            var id = _preferences.GetString("device", null);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                Edit(e => e.PutString("device", id));
            }

            return id;
        }
    }

    public string? RefreshToken => _preferences.GetString("refresh", null);

    public string? AccessToken => _preferences.GetString("access", null);

    public DateTimeOffset AccessTokenExpires =>
        DateTimeOffset.FromUnixTimeSeconds(_preferences.GetLong("access_expires", 0));

    public void SaveTokens(string accessToken, long expiresInSeconds, string refreshToken) => Edit(e =>
    {
        e.PutString("access", accessToken);
        e.PutLong("access_expires", DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds());
        e.PutString("refresh", refreshToken);
    });

    public void ClearTokens() => Edit(e =>
    {
        e.Remove("access");
        e.Remove("access_expires");
        e.Remove("refresh");
    });

    private void Edit(Action<ISharedPreferencesEditor> change)
    {
        var editor = _preferences.Edit()!;
        change(editor);
        editor.Apply();
    }
}
