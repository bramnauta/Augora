using Augora.Client;

namespace Augora.Mobile;

/// <summary>
/// Shows the Augora web UI in a WebView. The user logs in there (also with OIDC); the app then exchanges that
/// session for tokens and starts the native player, which makes the device a player.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The page lives as long as the app.")]
public sealed class MainPage : ContentPage
{
    private const string AuthCookie = "Augora.Auth";
    private static readonly TimeSpan TokenPollInterval = TimeSpan.FromSeconds(3);

    private readonly AppSettings _settings = PlatformPlayer.CreateSettings();
    private readonly ServerClient _server;
    private WebView? _webView;
    private CancellationTokenSource? _polling;

    public MainPage()
    {
        _server = new ServerClient(_settings);
        if (_settings.ServerUrl is null)
        {
            ShowSetup(null);
        }
        else
        {
            ShowWebView();
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_webView is { CanGoBack: true })
        {
            _webView.GoBack();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private void ShowSetup(string? message)
    {
        _polling?.Cancel();
        _webView = null;

        var input = new Entry
        {
            Text = _settings.ServerUrl ?? "http://",
            Placeholder = Strings.SetupHint,
            Keyboard = Keyboard.Url,
            MaximumWidthRequest = 480,
        };
        var connect = new Button { Text = Strings.Connect, HorizontalOptions = LayoutOptions.Center };
        var error = new Label { Text = message, IsVisible = message is not null, HorizontalTextAlignment = TextAlignment.Center };

        connect.Clicked += async (_, _) =>
        {
            var url = ServerClient.NormalizeServerUrl(input.Text);
            connect.IsEnabled = false;
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
                error.Text = Strings.SetupFailed(ex.Message);
                error.IsVisible = true;
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };

        Content = new VerticalStackLayout
        {
            Padding = 32,
            Spacing = 16,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = Strings.SetupTitle, FontSize = 22, HorizontalTextAlignment = TextAlignment.Center },
                input,
                connect,
                error,
            },
        };
    }

    private void ShowWebView()
    {
        var webView = new WebView { Source = new UrlWebViewSource { Url = _settings.ServerUrl! } };
        webView.Navigated += (_, e) =>
        {
            if (e.Result != WebNavigationResult.Success && e.Result != WebNavigationResult.Cancel)
            {
                ShowSetup(Strings.PageFailed);
            }
        };

        _webView = webView;
        Content = webView;
        StartTokenPolling();
    }

    /// <summary>Waits for the user to log in inside the WebView, then starts the native player.</summary>
    private void StartTokenPolling()
    {
        _polling?.Cancel();
        var polling = _polling = new CancellationTokenSource();
        _ = PollAsync(polling.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_server.HasTokens
                    && await PlatformWeb.GetCookieHeaderAsync(_settings.ServerUrl!) is { } cookies
                    && cookies.Contains(AuthCookie, StringComparison.Ordinal))
                {
                    await _server.ExchangeCookieAsync(cookies, cancellationToken);
                }

                if (_server.HasTokens)
                {
                    PlatformPlayer.Start(_settings, _server);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Server unreachable for a moment; try again.
            }

            await Task.Delay(TokenPollInterval, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
