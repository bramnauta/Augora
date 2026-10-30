using Augora.Client;
using Augora.Contracts;
using Augora.Mobile.Services;

namespace Augora.Mobile.Views;

/// <summary>Asks for the server, then for the login: a password, or one of the server's OIDC providers.</summary>
public sealed partial class LoginPage : ContentPage
{
    private readonly AppState _app = Ioc.Get<AppState>();

    public LoginPage()
    {
        InitializeComponent();
        TitleLabel.Text = Strings.ConnectTitle;
        ServerEntry.Placeholder = Strings.ServerHint;
        ServerEntry.Text = _app.Settings.ServerUrl;
        UserEntry.Placeholder = Strings.UserName;
        PasswordEntry.Placeholder = Strings.Password;
        LoginButton.Text = Strings.LogIn;
        ConnectButton.Text = Strings.Connect;
        OtherServerButton.Text = Strings.OtherServer;
        Busy.IsRunning = false;
        Busy.IsVisible = false;
    }

    /// <summary>The login succeeded: the app can start.</summary>
    public event Action? LoggedIn;

    private async void OnConnect(object? sender, EventArgs e)
    {
        var url = ServerClient.NormalizeServerUrl(ServerEntry.Text);
        await RunAsync(async () =>
        {
            AuthStatusDto status;
            try
            {
                status = await ServerClient.CheckServerAsync(url, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
            {
                StatusLabel.Text = Strings.CannotReach(ex.Message);
                return;
            }

            if (status.SetupRequired)
            {
                StatusLabel.Text = Strings.SetupRequired;
                return;
            }

            _app.Connect(url);
            ServerEntry.Text = url;
            ServerEntry.IsEnabled = false;
            ConnectButton.IsVisible = false;
            OtherServerButton.IsVisible = true;
            CredentialsPanel.IsVisible = true;
            ShowProviders(status.ExternalProviders);
            UserEntry.Focus();
        });
    }

    private void ShowProviders(IReadOnlyList<ExternalProviderDto> providers)
    {
        ProvidersPanel.Children.Clear();
        foreach (var provider in providers)
        {
            var button = new Button { Text = Strings.LogInWith(provider.DisplayName), BackgroundColor = (Color)Resources["SurfaceRaised"], TextColor = Colors.White };
            button.Clicked += async (_, _) => await RunAsync(() => LogInWithAsync(provider.Scheme));
            ProvidersPanel.Children.Add(button);
        }
    }

    private async void OnLogin(object? sender, EventArgs e) => await RunAsync(async () =>
    {
        try
        {
            if (!await _app.Server.LoginAsync(UserEntry.Text?.Trim() ?? "", PasswordEntry.Text ?? "", CancellationToken.None))
            {
                StatusLabel.Text = Strings.WrongCredentials;
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException)
        {
            StatusLabel.Text = ex is ApiException ? ex.Message : Strings.CannotReach(ex.Message);
            return;
        }

        Finish();
    });

    private void OnOtherServer(object? sender, EventArgs e)
    {
        ServerEntry.IsEnabled = true;
        ConnectButton.IsVisible = true;
        OtherServerButton.IsVisible = false;
        CredentialsPanel.IsVisible = false;
        PasswordEntry.Text = "";
        StatusLabel.Text = "";
    }

    /// <summary>OIDC: the system browser logs in; the redirect back to <c>augora://auth</c> carries a one-time code.</summary>
    private async Task LogInWithAsync(string scheme)
    {
        try
        {
            var start = new Uri($"{_app.Settings.ServerUrl}{AppAuth.ExternalPath}{Uri.EscapeDataString(scheme)}?returnUrl={Uri.EscapeDataString(AppAuth.CompletePath)}");
            var result = await WebAuthenticator.Default.AuthenticateAsync(start, new Uri($"{AppAuth.RedirectScheme}://auth"));
            if (!result.Properties.TryGetValue("code", out var code) || !await _app.Server.ExchangeAppCodeAsync(code, CancellationToken.None))
            {
                StatusLabel.Text = Strings.LoginFailed;
                return;
            }
        }
        catch (TaskCanceledException)
        {
            // The user closed the browser.
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            StatusLabel.Text = Strings.CannotReach(ex.Message);
            return;
        }

        Finish();
    }

    private void Finish()
    {
        _app.BeginSession();
        LoggedIn?.Invoke();
    }

    private async Task RunAsync(Func<Task> action)
    {
        StatusLabel.Text = "";
        Busy.IsVisible = Busy.IsRunning = true;
        try
        {
            await action();
        }
        finally
        {
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }
}
