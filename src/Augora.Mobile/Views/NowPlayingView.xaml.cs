using System.ComponentModel;
using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

/// <summary>The full player screen.</summary>
public sealed partial class NowPlayingView : ContentView
{
    private readonly PlayerViewModel _viewModel = Ioc.Get<PlayerViewModel>();

    public NowPlayingView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
        UpdateToggles();
    }

    public event Action? CloseRequested;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.PlayerName))
        {
            PlayingOnLabel.Text = Strings.PlayingOn(_viewModel.PlayerName);
        }
        else if (e.PropertyName is nameof(PlayerViewModel.Shuffle) or nameof(PlayerViewModel.Repeat))
        {
            UpdateToggles();
        }
    }

    private void UpdateToggles()
    {
        var accent = (Color)Application.Current!.Resources["Accent"];
        var quiet = (Color)Application.Current.Resources["TextSecondary"];
        ShuffleIcon.Fill = _viewModel.Shuffle ? accent : quiet;
        RepeatIcon.Fill = _viewModel.Repeat != Augora.Contracts.RepeatMode.Off ? accent : quiet;
    }

    /// <summary>The artwork is a square as wide as the room allows.</summary>
    private void OnArtworkSizeChanged(object? sender, EventArgs e)
    {
        if (Artwork.Width > 0 && Math.Abs(Artwork.HeightRequest - Artwork.Width) > 1)
        {
            Artwork.HeightRequest = Artwork.Width;
        }
    }

    private void OnClose(object? sender, TappedEventArgs e) => CloseRequested?.Invoke();

    private async void OnPlayPause(object? sender, TappedEventArgs e) => await _viewModel.PlayPauseAsync();

    private async void OnNext(object? sender, TappedEventArgs e) => await _viewModel.NextAsync();

    private async void OnPrevious(object? sender, TappedEventArgs e) => await _viewModel.PreviousAsync();

    private async void OnShuffle(object? sender, TappedEventArgs e) => await _viewModel.ToggleShuffleAsync();

    private async void OnRepeat(object? sender, TappedEventArgs e) => await _viewModel.CycleRepeatAsync();

    private void OnSeekStarted(object? sender, EventArgs e) => _viewModel.BeginSeek();

    private async void OnSeekCompleted(object? sender, EventArgs e) => await _viewModel.SeekAsync(SeekSlider.Value);

    private async void OnVolumeCompleted(object? sender, EventArgs e) => await _viewModel.SetVolumeAsync(((Slider)sender!).Value);
}
