using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

/// <summary>The sheet to choose where music plays: this device or another player.</summary>
public sealed partial class PlayerPickerView : ContentView
{
    private readonly PlayerViewModel _viewModel = Ioc.Get<PlayerViewModel>();

    public PlayerPickerView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        TitleLabel.Text = Strings.PlayOn;
        LogOutButton.Text = Strings.LogOut;
    }

    private async void OnLogOut(object? sender, EventArgs e) => await Ioc.Get<AppState>().LogoutAsync();

    public event Action? CloseRequested;

    private void OnClose(object? sender, TappedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Swallows taps on the sheet itself, so only the dimmed area closes it.</summary>
    private void OnSheetTapped(object? sender, TappedEventArgs e)
    {
    }

    private async void OnPick(object? sender, TappedEventArgs e)
    {
        if (((BindableObject)sender!).BindingContext is PlayerRow row)
        {
            await _viewModel.SelectPlayerAsync(row);
            CloseRequested?.Invoke();
        }
    }
}
