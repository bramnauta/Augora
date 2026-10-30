using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

/// <summary>One item of a folder or of the search results.</summary>
public sealed partial class MediaRowView : ContentView
{
    public MediaRowView() => InitializeComponent();

    private MediaRow? Row => BindingContext as MediaRow;

    private async void OnOpen(object? sender, TappedEventArgs e)
    {
        if (Row is { Host: { } host } row)
        {
            await host.OpenAsync(row);
        }
    }

    private async void OnPlay(object? sender, TappedEventArgs e)
    {
        if (Row is { Host: { } host } row)
        {
            await host.PlayAsync(row);
        }
    }

    private async void OnMore(object? sender, TappedEventArgs e)
    {
        if (Row is { Host: { } host } row)
        {
            await host.MoreAsync(row);
        }
    }
}
