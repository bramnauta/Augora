namespace Augora.Mobile.Services;

/// <summary>Lets any view ask the root page to open something: a folder, the player screen or the player picker.</summary>
public sealed class Navigator
{
    public event Action<string, string>? BrowseRequested;

    public event Action? NowPlayingRequested;

    public event Action? PlayerPickerRequested;

    public event Action<string>? ToastRequested;

    public void Browse(string uri, string title) => BrowseRequested?.Invoke(uri, title);

    public void ShowNowPlaying() => NowPlayingRequested?.Invoke();

    public void ShowPlayerPicker() => PlayerPickerRequested?.Invoke();

    public void Toast(string message) => ToastRequested?.Invoke(message);
}
