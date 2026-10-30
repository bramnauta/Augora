using System.Collections.ObjectModel;
using Augora.Client;
using Augora.Contracts;
using Augora.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Augora.Mobile.ViewModels;

/// <summary>One folder of the library; the root (<c>null</c> uri) lists the sources.</summary>
public sealed partial class BrowseViewModel(string? uri, string title) : ObservableObject, IRowHost
{
    private readonly AppState _app = Ioc.Get<AppState>();
    private readonly PlayService _play = Ioc.Get<PlayService>();
    private readonly Navigator _navigator = Ioc.Get<Navigator>();
    private IReadOnlyList<MediaItemDto> _items = [];

    [ObservableProperty]
    private string _title = title;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    public ObservableCollection<MediaRow> Rows { get; } = [];

    public async Task LoadAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _app.Api.BrowseAsync(uri);
            _items = result.Items;
            Title = uri is null ? Strings.Sources : result.Title;
            Rows.Clear();
            foreach (var item in result.Items)
            {
                Rows.Add(new MediaRow(item, _app.ResolveUrl(item.ImageUrl), this));
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task OpenAsync(MediaRow row)
    {
        if (row.IsBrowsable)
        {
            _navigator.Browse(row.Item.Uri, row.Name);
            return Task.CompletedTask;
        }

        return row.IsPlayable ? _play.PlayAsync(row.Item, QueueOption.Replace, _items) : Task.CompletedTask;
    }

    public Task PlayAsync(MediaRow row) => _play.PlayAsync(row.Item, QueueOption.Replace, _items);

    public Task MoreAsync(MediaRow row) => _play.ShowMenuAsync(row, () => PlayAsync(row));
}
