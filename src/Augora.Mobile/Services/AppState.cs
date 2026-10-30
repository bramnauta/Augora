using Augora.Client;

namespace Augora.Mobile.Services;

/// <summary>
/// The app's session: which server, who is logged in, the API and the live state. Everything that needs a server
/// goes through here; the address and tokens are kept in <see cref="Settings"/>.
/// </summary>
public sealed class AppState
{
    private HttpClient? _http;

    public AppState()
    {
        Settings = PlatformPlayer.CreateSettings();
        Server = new ServerClient(Settings);
    }

    /// <summary>The login is gone (logged out, or the server refused the token): show the login screen.</summary>
    public event Action? SessionEnded;

    public AppSettings Settings { get; }

    public ServerClient Server { get; }

    public ApiClient Api { get; private set; } = null!;

    public LiveStore Store { get; private set; } = null!;

    public bool IsLoggedIn => Settings.ServerUrl is not null && Server.HasTokens;

    /// <summary>Points the app at <paramref name="serverUrl"/>; another server means another login.</summary>
    public void Connect(string serverUrl)
    {
        if (serverUrl != Settings.ServerUrl)
        {
            Settings.ClearTokens();
        }

        Settings.ServerUrl = serverUrl;
        Build();
    }

    /// <summary>After logging in: this device becomes a player and the live state starts.</summary>
    public void BeginSession()
    {
        PlatformPlayer.Start(Settings, Server);
        _ = Store.StartAsync(CancellationToken.None);
    }

    /// <summary>When the app opens with a login from last time.</summary>
    public void Resume()
    {
        Build();
        BeginSession();
    }

    public async Task LogoutAsync()
    {
        PlatformPlayer.Stop();
        Server.Logout();
        if (Store is not null)
        {
            await Store.DisposeAsync();
        }

        SessionEnded?.Invoke();
    }

    /// <summary>Artwork URLs from the server are relative to it.</summary>
    public string? ResolveUrl(string? url) =>
        string.IsNullOrEmpty(url) ? null : url.StartsWith('/') ? Settings.ServerUrl + url : url;

    private void Build()
    {
        _http?.Dispose();
        _http = new HttpClient(new BearerTokenHandler(Server) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(Settings.ServerUrl + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        Api = new ApiClient(_http);
        Api.Unauthorized += () => _ = LogoutAsync();
        Store = new LiveStore(Api, Server, Settings);
    }
}
