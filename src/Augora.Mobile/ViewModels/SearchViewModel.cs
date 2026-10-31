using System.Collections.ObjectModel;
using Augora.Client;
using Augora.Contracts;
using Augora.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Augora.Mobile.ViewModels;

public sealed partial class SearchViewModel : ObservableObject, IRowHost
{
    private readonly AppState _app = Ioc.Get<AppState>();
    private readonly PlayService _play = Ioc.Get<PlayService>();
    private readonly Navigator _navigator = Ioc.Get<Navigator>();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    public ObservableCollection<MediaGroup> Groups { get; } = [];

    public async Task SearchAsync(string? query)
    {
        query = query?.Trim();
        Groups.Clear();
        Message = null;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _app.Api.SearchAsync(query);
            foreach (var provider in result.Providers.Where(p => p.Items.Count > 0))
            {
                var rows = provider.Items.Select(i => new MediaRow(i, _app.ResolveUrl(i.ImageUrl), this)).ToList();
                Groups.Add(new MediaGroup(provider.ProviderName, rows));
            }

            if (Groups.Count == 0)
            {
                Message = Strings.NoResults;
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            Message = ex.Message;
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

        return row.IsPlayable ? PlayAsync(row) : Task.CompletedTask;
    }

    public Task PlayAsync(MediaRow row) => _play.PlayAsync(row.Item, QueueOption.Replace);

    public Task MoreAsync(MediaRow row) => _play.ShowMenuAsync(row, () => PlayAsync(row));
}
