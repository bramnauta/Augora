using Augora.Client;
using Augora.Contracts;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Services;

/// <summary>Plays or queues an item on the selected player, with the questions Spotify can ask on the way.</summary>
public sealed class PlayService(AppState app, Navigator navigator)
{
    /// <summary>The "more" menu of a row: open, play now, play next, add to the queue.</summary>
    /// <param name="playNow">Plays the row the way its list wants it (from here on).</param>
    public async Task ShowMenuAsync(MediaRow row, Func<Task> playNow)
    {
        var options = new List<string>();
        if (row.IsBrowsable)
        {
            options.Add(Strings.Open);
        }

        if (row.IsPlayable)
        {
            options.AddRange([Strings.PlayNow, Strings.PlayNext, Strings.AddToQueue]);
        }

        var choice = await Ui.ChooseAsync(row.Name, [.. options]);
        if (choice == Strings.Open)
        {
            navigator.Browse(row.Item.Uri, row.Name);
        }
        else if (choice == Strings.PlayNow)
        {
            await playNow();
        }
        else if (choice == Strings.PlayNext)
        {
            await PlayAsync(row.Item, QueueOption.Next);
        }
        else if (choice == Strings.AddToQueue)
        {
            await PlayAsync(row.Item, QueueOption.Add);
        }
    }

    /// <param name="siblings">The list the item was chosen from; "play from here" queues what follows it.</param>
    public async Task PlayAsync(MediaItemDto item, QueueOption option, IReadOnlyList<MediaItemDto>? siblings = null)
    {
        var store = app.Store;
        if (store.SelectedPlayerId is not { } playerId)
        {
            return;
        }

        var playerName = store.SelectedPlayer?.Name ?? "";
        IReadOnlyList<string>? following = option is QueueOption.Replace && item.Type is MediaType.Track or MediaType.Episode && siblings is not null
            ? siblings.SkipWhile(i => i.Uri != item.Uri).Skip(1).Where(i => i.IsPlayable && !i.IsBrowsable).Select(i => i.Uri).ToList()
            : null;

        string? takeover = null;
        while (true)
        {
            try
            {
                await app.Api.PlayAsync(playerId, item.Uri, option, takeover, following);
                navigator.Toast(option is QueueOption.Replace or QueueOption.Play ? Strings.PlayingOn(playerName) : Strings.AddedTo(playerName));
                return;
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.SpotifyPairingRequired)
            {
                await Ui.AlertAsync(Strings.Error, Strings.SpotifyPairing);
                return;
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.SpotifyNoFreeAccount && takeover is null)
            {
                // Spotify allows one stream per account: offer to stop one of the streams the user may take over.
                var holders = (ex.GetExtension<List<SpotifyAccountHolderDto>>("holders") ?? []).Where(h => h.CanTakeOver).ToList();
                var chosen = holders.Count == 0 ? null : await Ui.ChooseAsync(Strings.SpotifyBusy, holders.Select(h => h.PlayerName).ToArray());
                if (chosen is null)
                {
                    return;
                }

                takeover = holders.First(h => h.PlayerName == chosen).PlayerId;
            }
            catch (ApiException ex)
            {
                await Ui.AlertAsync(Strings.Error, ex.Message);
                return;
            }
        }
    }
}
