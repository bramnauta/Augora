using Augora.Mobile.Services;
using Augora.Mobile.Views;

namespace Augora.Mobile;

public sealed partial class App : Application
{
    private readonly AppState _state;
    private Window? _window;

    public App(AppState state)
    {
        InitializeComponent();
        _state = state;
        _state.SessionEnded += () => MainThread.BeginInvokeOnMainThread(ShowLogin);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _window = new Window(new ContentPage());
        if (_state.IsLoggedIn)
        {
            _state.Resume();
            ShowRoot();
        }
        else
        {
            ShowLogin();
        }

        return _window;
    }

    private void ShowRoot() => _window!.Page = new RootPage();

    private void ShowLogin()
    {
        var login = new LoginPage();
        login.LoggedIn += () => MainThread.BeginInvokeOnMainThread(ShowRoot);
        _window!.Page = login;
    }
}
