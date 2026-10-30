using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

/// <summary>The bar above the tabs: what plays, on which player, with play/pause.</summary>
public sealed partial class MiniPlayerView : ContentView
{
    private readonly PlayerViewModel _viewModel = Ioc.Get<PlayerViewModel>();
    private readonly Navigator _navigator = Ioc.Get<Navigator>();

    public MiniPlayerView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
    }

    private void OnOpen(object? sender, TappedEventArgs e) => _navigator.ShowNowPlaying();

    private void OnPickPlayer(object? sender, TappedEventArgs e) => _navigator.ShowPlayerPicker();

    private async void OnPlayPause(object? sender, TappedEventArgs e) => await _viewModel.PlayPauseAsync();
}
