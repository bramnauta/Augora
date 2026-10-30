using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

/// <summary>A folder of the library, or its root (the sources) when <c>uri</c> is <c>null</c>.</summary>
public sealed partial class BrowseView : ContentView
{
    private readonly BrowseViewModel _viewModel;
    private bool _loaded;

    public BrowseView(string? uri, string title, bool canGoBack)
    {
        InitializeComponent();
        BindingContext = _viewModel = new BrowseViewModel(uri, title);
        BackButton.IsVisible = canGoBack;
        Loaded += async (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                await _viewModel.LoadAsync();
            }
        };
    }

    public event Action? BackRequested;

    private void OnBack(object? sender, TappedEventArgs e) => BackRequested?.Invoke();

    private async void OnRefreshing(object? sender, EventArgs e) => await _viewModel.LoadAsync();
}
