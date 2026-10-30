using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Augora.AndroidPlayer;
using Augora.Client;

namespace Augora.AndroidTv;

/// <summary>
/// Shows the Augora web UI in a WebView. The user logs in there (also with OIDC); the app then exchanges that
/// session for tokens and starts <see cref="PlayerService"/>, which makes the TV a player.
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Theme = "@android:style/Theme.Material.NoActionBar",
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
[IntentFilter([Intent.ActionMain], Categories = [Intent.CategoryLeanbackLauncher])]
public sealed class MainActivity : Activity
{
    private const string AuthCookie = "Augora.Auth";
    private static readonly TimeSpan TokenPollInterval = TimeSpan.FromSeconds(3);

    private AndroidAppSettings _settings = null!;
    private ServerClient _server = null!;
    private WebView? _webView;
    private CancellationTokenSource? _polling;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _settings = new AndroidAppSettings(this);
        _server = new ServerClient(_settings);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 1);
        }

        if (_settings.ServerUrl is null)
        {
            ShowSetup(null);
        }
        else
        {
            ShowWebView();
        }
    }

    public override void OnBackPressed()
    {
        if (_webView?.CanGoBack() == true)
        {
            _webView.GoBack();
            return;
        }

#pragma warning disable CA1422 // Still the simplest way to leave the WebView on Android 8-12.
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    protected override void OnDestroy()
    {
        _polling?.Cancel();
        _webView?.Destroy();
        base.OnDestroy();
    }

    internal void ShowSetup(string? message)
    {
        _polling?.Cancel();
        _webView?.Destroy();
        _webView = null;
        SetContentView(Resource.Layout.activity_main);

        var input = FindViewById<EditText>(Resource.Id.server_url)!;
        var connect = FindViewById<Button>(Resource.Id.connect)!;
        input.Text = _settings.ServerUrl ?? "http://";
        if (message is not null)
        {
            Toast.MakeText(this, message, ToastLength.Long)!.Show();
        }

        connect.Click += async (_, _) =>
        {
            var url = ServerClient.NormalizeServerUrl(input.Text);
            connect.Enabled = false;
            try
            {
                await ServerClient.CheckServerAsync(url, CancellationToken.None);
                if (url != _settings.ServerUrl)
                {
                    _settings.ClearTokens();
                }

                _settings.ServerUrl = url;
                ShowWebView();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
            {
                Toast.MakeText(this, GetString(Resource.String.setup_failed, new Java.Lang.String(ex.Message)), ToastLength.Long)!.Show();
            }
            finally
            {
                connect.Enabled = true;
            }
        };
    }

    private void ShowWebView()
    {
        var webView = new WebView(this);
        var web = webView.Settings;
        web.JavaScriptEnabled = true;
        web.DomStorageEnabled = true;
        web.MediaPlaybackRequiresUserGesture = false;
        // The web UI hides "this browser as player" inside the app: the native player does that job.
        web.UserAgentString = $"{web.UserAgentString} AugoraAndroid/1.0";
        CookieManager.Instance!.SetAcceptCookie(true);
        webView.SetWebViewClient(new AugoraWebViewClient(this));
        webView.SetWebChromeClient(new WebChromeClient());
        webView.LoadUrl(_settings.ServerUrl!);

        _webView = webView;
        SetContentView(webView);
        webView.RequestFocus(FocusSearchDirection.Down);
        StartTokenPolling();
    }

    /// <summary>Waits for the user to log in inside the WebView, then starts the native player.</summary>
    private void StartTokenPolling()
    {
        _polling?.Cancel();
        var polling = _polling = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!polling.IsCancellationRequested)
            {
                try
                {
                    if (!_server.HasTokens
                        && CookieManager.Instance!.GetCookie(_settings.ServerUrl) is { } cookies
                        && cookies.Contains(AuthCookie, StringComparison.Ordinal))
                    {
                        await _server.ExchangeCookieAsync(cookies, polling.Token);
                    }

                    if (_server.HasTokens)
                    {
                        RunOnUiThread(() => PlayerService.Start(this));
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Server unreachable for a moment; try again.
                }

                await Task.Delay(TokenPollInterval, polling.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, polling.Token);
    }

    private sealed class AugoraWebViewClient(MainActivity activity) : WebViewClient
    {
        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            base.OnReceivedError(view, request, error);
            if (request?.IsForMainFrame == true)
            {
                activity.ShowSetup(activity.GetString(Resource.String.page_failed));
            }
        }
    }
}
