using System.Globalization;

namespace Augora.Client;

/// <summary>
/// The server address, device id and native-player tokens. A platform supplies the key/value storage
/// (<c>SharedPreferences</c>, <c>Preferences</c>, ...); the rest is the same everywhere.
/// </summary>
public abstract class AppSettings
{
    /// <summary>Without trailing slash, e.g. <c>http://192.168.1.10:4727</c>.</summary>
    public string? ServerUrl
    {
        get => Read("server");
        set => Write("server", value?.TrimEnd('/'));
    }

    /// <summary>Stable per installation, so the player keeps its id and queue.</summary>
    public string DeviceId
    {
        get
        {
            var id = Read("device");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                Write("device", id);
            }

            return id;
        }
    }

    public string? RefreshToken => Read("refresh");

    public string? AccessToken => Read("access");

    public DateTimeOffset AccessTokenExpires =>
        long.TryParse(Read("access_expires"), CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;

    public void SaveTokens(string accessToken, long expiresInSeconds, string refreshToken)
    {
        Write("access", accessToken);
        Write("access_expires", DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Write("refresh", refreshToken);
    }

    public void ClearTokens()
    {
        Write("access", null);
        Write("access_expires", null);
        Write("refresh", null);
    }

    /// <summary>The stored value, or <c>null</c> when there is none.</summary>
    protected abstract string? Read(string key);

    /// <summary>Stores <paramref name="value"/>; <c>null</c> removes the key.</summary>
    protected abstract void Write(string key, string? value);
}
