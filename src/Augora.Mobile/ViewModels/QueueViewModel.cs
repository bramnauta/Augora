using System.Collections.ObjectModel;
using Augora.Client;
using Augora.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Augora.Mobile.ViewModels;

/// <summary>The queue of the selected player.</summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly AppState _app = Ioc.Get<AppState>();
    private LiveStore? _store;

    [ObservableProperty]
    private bool _isEmpty = true;

    public ObservableCollection<QueueRow> Rows { get; } = [];

    public void Attach()
    {
        if (_store is not null)
        {
            _store.Changed -= OnStoreChanged;
        }

        _store = _app.Store;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    public Task PlayAsync(QueueRow row) => RunAsync(id => _app.Api.PlayQueueItemAsync(id, row.Id));

    public Task RemoveAsync(QueueRow row) => RunAsync(id => _app.Api.RemoveQueueItemAsync(id, row.Id));

    public Task ClearAsync() => RunAsync(_app.Api.ClearQueueAsync);

    private async Task RunAsync(Func<string, Task> action)
    {
        if (_store?.SelectedPlayerId is not { } id)
        {
            return;
        }

        try
        {
            await action(id);
        }
        catch (ApiException ex)
        {
            await Ui.AlertAsync(Strings.Error, ex.Message);
        }
    }

    private void OnStoreChanged() => MainThread.BeginInvokeOnMainThread(Refresh);

    private void Refresh()
    {
        var queue = _store?.SelectedQueue;
        Rows.Clear();
        if (queue is not null)
        {
            for (var i = 0; i < queue.Items.Count; i++)
            {
                var item = queue.Items[i];
                Rows.Add(new QueueRow(item.Id, new MediaRow(item.Item, _app.ResolveUrl(item.Item.ImageUrl)), i == queue.CurrentIndex));
            }
        }

        IsEmpty = Rows.Count == 0;
    }
}

public sealed record QueueRow(string Id, MediaRow Media, bool IsCurrent)
{
    public string Name => Media.Name;

    public string? Subtitle => Media.Subtitle;

    public string? ImageUrl => Media.ImageUrl;

    public Color TitleColor => IsCurrent ? (Color)Application.Current!.Resources["Accent"] : (Color)Application.Current!.Resources["TextPrimary"];
}
