using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

/// <summary>
/// The app after logging in: three tabs (home, search, queue) that can open folders on top of themselves, the mini player,
/// and the player screen and player picker that slide over everything.
/// </summary>
public sealed partial class RootPage : ContentPage
{
    private readonly Navigator _navigator = Ioc.Get<Navigator>();
    private readonly Stack<View>[] _stacks;
    private CancellationTokenSource? _toast;
    private int _tab;

    public RootPage()
    {
        InitializeComponent();
        Ioc.Get<PlayerViewModel>().Attach();
        Ioc.Get<QueueViewModel>().Attach();

        HomeLabel.Text = Strings.Home;
        SearchLabel.Text = Strings.Search;
        QueueLabel.Text = Strings.Queue;

        _stacks =
        [
            new Stack<View>([CreateBrowse(null, Strings.Home, canGoBack: false)]),
            new Stack<View>([new SearchView()]),
            new Stack<View>([new QueueView()]),
        ];
        Select(0);

        NowPlaying.CloseRequested += () => _ = HideNowPlayingAsync();
        Picker.CloseRequested += () => Picker.IsVisible = false;
        _navigator.BrowseRequested += OnBrowse;
        _navigator.NowPlayingRequested += OnShowNowPlaying;
        _navigator.PlayerPickerRequested += OnShowPicker;
        _navigator.ToastRequested += OnToast;
        Unloaded += (_, _) =>
        {
            _navigator.BrowseRequested -= OnBrowse;
            _navigator.NowPlayingRequested -= OnShowNowPlaying;
            _navigator.PlayerPickerRequested -= OnShowPicker;
            _navigator.ToastRequested -= OnToast;
        };
    }

    protected override bool OnBackButtonPressed()
    {
        if (NowPlaying.IsVisible)
        {
            _ = HideNowPlayingAsync();
            return true;
        }

        if (Picker.IsVisible)
        {
            Picker.IsVisible = false;
            return true;
        }

        if (_stacks[_tab].Count > 1)
        {
            Pop();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private BrowseView CreateBrowse(string? uri, string title, bool canGoBack)
    {
        var view = new BrowseView(uri, title, canGoBack);
        view.BackRequested += Pop;
        return view;
    }

    private void OnBrowse(string uri, string title)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // From the player screen or a sheet, the folder opens in the current tab underneath.
            Picker.IsVisible = false;
            _ = HideNowPlayingAsync();
            var view = CreateBrowse(uri, title, canGoBack: true);
            _stacks[_tab].Push(view);
            Host.Content = view;
        });
    }

    private void Pop()
    {
        if (_stacks[_tab].Count > 1)
        {
            _stacks[_tab].Pop();
            Host.Content = _stacks[_tab].Peek();
        }
    }

    private void Select(int tab)
    {
        _tab = tab;
        Host.Content = _stacks[tab].Peek();
        var accent = (Color)Resources["Accent"];
        var quiet = (Color)Resources["TextSecondary"];
        Paint(HomeIcon, HomeLabel, tab == 0 ? accent : quiet);
        Paint(SearchIcon, SearchLabel, tab == 1 ? accent : quiet);
        Paint(QueueIcon, QueueLabel, tab == 2 ? accent : quiet);
    }

    private static void Paint(Icon icon, Label label, Color color)
    {
        icon.Fill = color;
        label.TextColor = color;
    }

    private void OnHomeTab(object? sender, TappedEventArgs e) => SelectOrReset(0);

    private void OnSearchTab(object? sender, TappedEventArgs e) => SelectOrReset(1);

    private void OnQueueTab(object? sender, TappedEventArgs e) => SelectOrReset(2);

    /// <summary>Tapping the tab you are on goes back to its start, like other music apps.</summary>
    private void SelectOrReset(int tab)
    {
        if (tab == _tab)
        {
            while (_stacks[tab].Count > 1)
            {
                _stacks[tab].Pop();
            }
        }

        Select(tab);
    }

    private void OnShowNowPlaying() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (NowPlaying.IsVisible)
        {
            return;
        }

        NowPlaying.TranslationY = Height;
        NowPlaying.IsVisible = true;
        await NowPlaying.TranslateToAsync(0, 0, 220, Easing.CubicOut);
    });

    private async Task HideNowPlayingAsync()
    {
        if (!NowPlaying.IsVisible)
        {
            return;
        }

        await NowPlaying.TranslateToAsync(0, Height, 200, Easing.CubicIn);
        NowPlaying.IsVisible = false;
    }

    private void OnShowPicker() => MainThread.BeginInvokeOnMainThread(() => Picker.IsVisible = true);

    private void OnToast(string message) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        _toast?.Cancel();
        var toast = _toast = new CancellationTokenSource();
        ToastLabel.Text = message;
        ToastBorder.IsVisible = true;
        await Task.Delay(2500, toast.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!toast.IsCancellationRequested)
        {
            ToastBorder.IsVisible = false;
        }
    });
}
