using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Media;
using AudioCentral.Core.Players;
using AudioCentral.Core.Providers;
using AudioCentral.Core.Streaming;
using AudioCentral.Providers.Spotify.Librespot;
using AudioCentral.Providers.Spotify.Soloist;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Providers.Spotify;

/// <summary>
/// Spotify: metadata from the Web API, audio from a go-librespot daemon per linked account. All accounts share the
/// <c>spotify</c> provider id, so items are the same whichever account plays them.
/// </summary>
public sealed class SpotifyProvider : IMusicProvider
{
    private const int MaxCachedItems = 20_000;
    private const int PageSize = 50;
    // Since February 2026 search and artist albums return at most 10 items per page.
    private const int SmallPageSize = 10;
    private const int MaxQueueItems = 1000;

    private readonly SpotifyAccountService _accounts;
    private readonly SpotifyAccountPool _pool;
    private readonly LibrespotManager _librespot;
    private readonly SoloistManager? _soloist;
    private readonly PlayerManager _players;
    private readonly ICurrentUser _currentUser;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyProvider> _logger;
    private readonly ConcurrentDictionary<MediaItemRef, MediaItem> _seen = new();
    // Playlist contents are only readable by an account that owns or collaborates on the playlist.
    private readonly ConcurrentDictionary<string, string> _playlistAccounts = new(StringComparer.Ordinal);

    internal SpotifyProvider(
        SpotifyAccountService accounts,
        SpotifyAccountPool pool,
        LibrespotManager librespot,
        SoloistManager? soloist,
        PlayerManager players,
        ICurrentUser currentUser,
        IHttpClientFactory httpClientFactory,
        ILogger<SpotifyProvider> logger)
    {
        _accounts = accounts;
        _pool = pool;
        _librespot = librespot;
        _soloist = soloist;
        _players = players;
        _currentUser = currentUser;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Id => SpotifyMapper.ProviderId;

    public string Name => "Spotify";

    public string Type => SpotifyMapper.ProviderId;

    /// <summary>Account id → id of the player streaming with it.</summary>
    public IReadOnlyDictionary<string, string> GetAccountHolders() => _pool.GetHolders();

    /// <summary>Whether the account is approved for playback, and otherwise how to approve it (a code, or the Spotify app).</summary>
    public async Task<SpotifyPairingDto> GetPairingAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = await GetLinkedAccountAsync(accountId, cancellationToken);
        try
        {
            return account.Engine == SpotifyEngine.Soloist
                ? await RequireSoloist().GetPairingAsync(account, cancellationToken)
                : await _librespot.GetPairingAsync(account, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new AudioCentralException(ErrorCodes.PlaybackFailed, ex.Message, 502, ex);
        }
    }

    /// <summary>Switches the playback engine of an account; Soloist needs an API key and the user's consent.</summary>
    public async Task<SpotifyEngine> SetEngineAsync(string accountId, SetSpotifyEngineRequest request, CancellationToken cancellationToken)
    {
        var account = await GetLinkedAccountAsync(accountId, cancellationToken);
        if (request.Engine == SpotifyEngine.Soloist)
        {
            RequireSoloist();
            if (!request.AcceptSoloistTerms)
            {
                throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured,
                    "Accept that AudioCentral downloads Spotify Soloist for you and that Spotify's terms do not clearly allow this use.");
            }

            if (string.IsNullOrWhiteSpace(request.SoloistApiKey) && account.SoloistApiKey is null)
            {
                throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured, "Enter the Soloist API key of this Spotify account.");
            }
        }

        // The other engine may hold the account's single stream; stop it.
        _pool.Forget(account.Id);
        await _librespot.RemoveAsync(account.Id);
        _soloist?.Forget(account);
        var updated = await _accounts.SetEngineAsync(account, request.Engine, request.SoloistApiKey, cancellationToken);
        _logger.LogInformation("Spotify account {Account} now plays with {Engine}", account.Id, updated.Engine);
        return updated.Engine;
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        _pool.Forget(accountId);
        await _librespot.RemoveAsync(accountId);
        if (await _accounts.GetAccountAsync(accountId, cancellationToken) is { } account)
        {
            _soloist?.Forget(account);
        }

        await _accounts.RemoveAccountAsync(accountId, cancellationToken);
    }

    private async Task<SpotifyAccount> GetLinkedAccountAsync(string accountId, CancellationToken cancellationToken) =>
        await _accounts.GetAccountAsync(accountId, cancellationToken)
        ?? throw new AudioCentralException(ErrorCodes.ItemNotFound, $"Spotify account '{accountId}' is not linked.", 404);

    private SoloistManager RequireSoloist() =>
        _soloist is not null && SoloistManager.IsSupported
            ? _soloist
            : throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured,
                "Spotify Soloist only runs on Linux, in the AudioCentral container (it needs PulseAudio).", 409);

    private static AudioCentralException PairingRequired(SpotifyPairingDto pairing)
    {
        var message = pairing.Method == SpotifyPairingMethod.Connect
            ? $"Approve playback for Spotify account '{pairing.AccountName}' once: select '{pairing.DeviceName}' in the Spotify app on the same network."
            : $"Approve playback for Spotify account '{pairing.AccountName}' once at {pairing.Url} (code {pairing.Code}).";
        var error = new AudioCentralException(ErrorCodes.SpotifyPairingRequired, message, 409);
        error.Extensions["pairing"] = pairing;
        return error;
    }

    public async Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken)
    {
        if (folder is not { } current)
        {
            var accounts = await _accounts.GetAccountsAsync(cancellationToken);
            return new BrowseResult(Name, accounts
                .Select(a => new MediaItem(SpotifyMapper.Folder(a.Id), a.DisplayName, IsBrowsable: true))
                .ToList());
        }

        switch (current.Type)
        {
            case MediaType.Album:
            {
                var account = await GetApiAccountAsync(null, cancellationToken);
                var album = await GetAsync(account, $"albums/{Escape(current.Id)}", SpotifyJsonContext.Default.SpotifyAlbum, cancellationToken);
                return new BrowseResult(album.Name, Remember(await GetAlbumTracksAsync(account, album, cancellationToken)));
            }

            case MediaType.Playlist:
                return new BrowseResult(
                    _seen.TryGetValue(current, out var playlist) ? playlist.Name : "Playlist",
                    Remember(await GetPlaylistTracksAsync(current.Id, cancellationToken)));

            case MediaType.Artist:
            {
                var account = await GetApiAccountAsync(null, cancellationToken);
                var albums = await GetPagedAsync(account, $"artists/{Escape(current.Id)}/albums?include_groups=album,single&limit={SmallPageSize}",
                    SpotifyJsonContext.Default.SpotifyPagingSpotifyAlbum, 50, cancellationToken);
                var name = _seen.TryGetValue(current, out var artist) ? artist.Name : "Artist";
                return new BrowseResult(name, Remember(SpotifyMapper.NotNull(albums.Select(SpotifyMapper.Album))));
            }

            case MediaType.Folder:
                return await BrowseFolderAsync(current.Id, cancellationToken);

            default:
                return new BrowseResult(Name, []);
        }
    }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if ((await _accounts.GetAccountsAsync(cancellationToken)).Count == 0)
        {
            return [];
        }

        var account = await GetApiAccountAsync(null, cancellationToken);
        var result = await GetAsync(account, $"search?q={Escape(query)}&type=track,artist,album,playlist&limit={SmallPageSize}",
            SpotifyJsonContext.Default.SpotifySearchResponse, cancellationToken);

        var items = new List<MediaItem>();
        items.AddRange(SpotifyMapper.NotNull((result.Tracks?.Items ?? []).Select(t => SpotifyMapper.Track(t))));
        items.AddRange(SpotifyMapper.NotNull((result.Artists?.Items ?? []).Select(SpotifyMapper.Artist)));
        items.AddRange(SpotifyMapper.NotNull((result.Albums?.Items ?? []).Select(SpotifyMapper.Album)));
        items.AddRange(SpotifyMapper.NotNull((result.Playlists?.Items ?? []).Select(SpotifyMapper.Playlist)));
        return Remember(items);
    }

    public async Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken)
    {
        if (_seen.TryGetValue(item, out var cached))
        {
            return cached;
        }

        if (item.Type == MediaType.Folder)
        {
            return await GetFolderItemAsync(item, cancellationToken);
        }

        var account = await GetApiAccountAsync(null, cancellationToken);
        var id = Escape(item.Id);
        try
        {
            var result = item.Type switch
            {
                MediaType.Track => SpotifyMapper.Track(await GetAsync(account, $"tracks/{id}", SpotifyJsonContext.Default.SpotifyTrack, cancellationToken)),
                MediaType.Album => SpotifyMapper.Album(await GetAsync(account, $"albums/{id}", SpotifyJsonContext.Default.SpotifyAlbum, cancellationToken)),
                MediaType.Artist => SpotifyMapper.Artist(await GetAsync(account, $"artists/{id}", SpotifyJsonContext.Default.SpotifyArtist, cancellationToken)),
                MediaType.Playlist => SpotifyMapper.Playlist(await GetAsync(account, $"playlists/{id}", SpotifyJsonContext.Default.SpotifyPlaylist, cancellationToken)),
                _ => null,
            };
            return result is null ? null : Remember([result])[0];
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MediaItem>> GetQueueItemsAsync(MediaItem item, CancellationToken cancellationToken)
    {
        switch (item.Type)
        {
            case MediaType.Track:
                return [item];
            case MediaType.Album:
            {
                var account = await GetApiAccountAsync(null, cancellationToken);
                var album = await GetAsync(account, $"albums/{Escape(item.Ref.Id)}", SpotifyJsonContext.Default.SpotifyAlbum, cancellationToken);
                return Remember(await GetAlbumTracksAsync(account, album, cancellationToken));
            }

            case MediaType.Playlist:
                return Remember(await GetPlaylistTracksAsync(item.Ref.Id, cancellationToken));
            case MediaType.Folder when ParseFolder(item.Ref.Id) is ({ } accountId, "liked"):
            {
                var account = await GetApiAccountAsync(accountId, cancellationToken);
                var saved = await GetPagedAsync(account, $"me/tracks?limit={PageSize}", SpotifyJsonContext.Default.SpotifyPagingSpotifySavedTrack, MaxQueueItems, cancellationToken);
                return Remember(SpotifyMapper.NotNull(saved.Select(s => SpotifyMapper.Track(s.Track))));
            }

            default:
                return [];
        }
    }

    public async Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken)
    {
        if (item.Type != MediaType.Track)
        {
            throw new AudioCentralException(ErrorCodes.ItemNotPlayable, "Only Spotify tracks can be streamed.");
        }

        var accounts = await _accounts.GetAccountsAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured, "No Spotify account is linked.", 409);
        }

        var allocation = _pool.Acquire(accounts, context.PlayerId, context.UserId, context.TakeoverPlayerId);
        if (allocation.Account is not { } account)
        {
            var error = new AudioCentralException(ErrorCodes.SpotifyNoFreeAccount, "All Spotify accounts are in use.", 409);
            error.Extensions["holders"] = allocation.Holders
                .Select(h => new SpotifyAccountHolderDto(h.Account.Id, h.Account.DisplayName, h.PlayerId, PlayerName(h.PlayerId),
                    SpotifyAccountPool.CanTakeOver(h.Account, context.UserId)))
                .ToList();
            throw error;
        }

        if (allocation.TakenFromPlayerId is { } victim)
        {
            _logger.LogInformation("Player {Player} takes over Spotify account {Account} from {Victim}", context.PlayerId, account.Id, victim);
            try
            {
                await _players.Get(victim).StopAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Stopping {Victim} after the takeover failed", victim);
            }
        }

        // Check the engine now, so pairing, login and installation problems are reported to the caller.
        try
        {
            if (account.Engine == SpotifyEngine.Soloist)
            {
                var soloist = RequireSoloist();
                if (!soloist.IsPaired(account))
                {
                    throw PairingRequired(await soloist.GetPairingAsync(account, cancellationToken));
                }
            }
            else
            {
                await _librespot.GetLoggedInDaemonAsync(account, cancellationToken);
            }
        }
        catch
        {
            _pool.Release(context.PlayerId);
            throw;
        }

        var duration = _seen.TryGetValue(item, out var known) ? known.Duration : null;
        var playerId = context.PlayerId;
        return new ProducedStreamSource(LibrespotManager.ContentType, async (output, ct) =>
        {
            if (!_pool.StreamStarted(account.Id, playerId))
            {
                throw new InvalidOperationException($"Spotify account {account.Id} was taken over by another player.");
            }

            try
            {
                if (account.Engine == SpotifyEngine.Soloist)
                {
                    await RequireSoloist().StreamTrackAsync(account, item.Id, duration, output, ct);
                }
                else
                {
                    var daemon = await _librespot.GetDaemonAsync(account, ct);
                    await _librespot.StreamTrackAsync(daemon, item.Id, duration, output, ct);
                }
            }
            finally
            {
                _pool.StreamEnded(account.Id, playerId);
            }
        });
    }

    private async Task<BrowseResult> BrowseFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        var (accountId, folder) = ParseFolder(folderId);
        var account = await GetApiAccountAsync(accountId, cancellationToken);
        switch (folder)
        {
            case "":
                return new BrowseResult(account.DisplayName,
                [
                    new MediaItem(SpotifyMapper.Folder(account.Id, "playlists"), "Playlists", IsBrowsable: true),
                    new MediaItem(SpotifyMapper.Folder(account.Id, "liked"), "Liked Songs", IsBrowsable: true, IsPlayable: true),
                    new MediaItem(SpotifyMapper.Folder(account.Id, "albums"), "Albums", IsBrowsable: true),
                    new MediaItem(SpotifyMapper.Folder(account.Id, "artists"), "Artists", IsBrowsable: true),
                ]);
            case "playlists":
            {
                var playlists = await GetPagedAsync(account, $"me/playlists?limit={PageSize}", SpotifyJsonContext.Default.SpotifyPagingSpotifyPlaylist, 500, cancellationToken);
                foreach (var playlist in playlists)
                {
                    _playlistAccounts[playlist.Id] = account.Id;
                }

                return new BrowseResult("Playlists", Remember(SpotifyMapper.NotNull(playlists.Select(SpotifyMapper.Playlist))));
            }

            case "liked":
            {
                var saved = await GetPagedAsync(account, $"me/tracks?limit={PageSize}", SpotifyJsonContext.Default.SpotifyPagingSpotifySavedTrack, 500, cancellationToken);
                return new BrowseResult("Liked Songs", Remember(SpotifyMapper.NotNull(saved.Select(s => SpotifyMapper.Track(s.Track)))));
            }

            case "albums":
            {
                var saved = await GetPagedAsync(account, $"me/albums?limit={PageSize}", SpotifyJsonContext.Default.SpotifyPagingSpotifySavedAlbum, 500, cancellationToken);
                return new BrowseResult("Albums", Remember(SpotifyMapper.NotNull(saved.Select(s => SpotifyMapper.Album(s.Album)))));
            }

            case "artists":
            {
                var artists = new List<SpotifyArtist>();
                string? url = $"me/following?type=artist&limit={PageSize}";
                while (url is not null && artists.Count < 500)
                {
                    var page = await GetAsync(account, url, SpotifyJsonContext.Default.SpotifyFollowedArtists, cancellationToken);
                    artists.AddRange(page.Artists?.Items?.OfType<SpotifyArtist>() ?? []);
                    url = page.Artists?.Next;
                }

                return new BrowseResult("Artists", Remember(SpotifyMapper.NotNull(artists.Select(SpotifyMapper.Artist))));
            }

            default:
                return new BrowseResult(Name, []);
        }
    }

    private async Task<MediaItem?> GetFolderItemAsync(MediaItemRef item, CancellationToken cancellationToken)
    {
        var (accountId, folder) = ParseFolder(item.Id);
        if (await _accounts.GetAccountAsync(accountId, cancellationToken) is not { } account)
        {
            return null;
        }

        return folder switch
        {
            "" => new MediaItem(item, account.DisplayName, IsBrowsable: true),
            "liked" => new MediaItem(item, "Liked Songs", account.DisplayName, IsPlayable: true, IsBrowsable: true),
            "playlists" or "albums" or "artists" => new MediaItem(item, char.ToUpperInvariant(folder[0]) + folder[1..], account.DisplayName, IsBrowsable: true),
            _ => null,
        };
    }

    private async Task<List<MediaItem>> GetAlbumTracksAsync(SpotifyAccount account, SpotifyAlbum album, CancellationToken cancellationToken)
    {
        var tracks = new List<SpotifyTrack>(album.Tracks?.Items?.OfType<SpotifyTrack>() ?? []);
        if (album.Tracks?.Next is { } next)
        {
            tracks.AddRange(await GetPagedAsync(account, next, SpotifyJsonContext.Default.SpotifyPagingSpotifyTrack, MaxQueueItems, cancellationToken));
        }

        return SpotifyMapper.NotNull(tracks.Select(t => SpotifyMapper.Track(t, album))).ToList();
    }

    private async Task<List<MediaItem>> GetPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken)
    {
        var account = await GetApiAccountAsync(_playlistAccounts.GetValueOrDefault(playlistId), cancellationToken);
        try
        {
            var items = await GetPagedAsync(account, $"playlists/{Escape(playlistId)}/items?limit={PageSize}",
                SpotifyJsonContext.Default.SpotifyPagingSpotifyPlaylistItem, MaxQueueItems, cancellationToken);
            return SpotifyMapper.NotNull(items.Select(i => SpotifyMapper.Track(i.Value))).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new AudioCentralException(ErrorCodes.ItemNotFound,
                "Spotify only shares the contents of playlists you own or collaborate on with apps.", 403, ex);
        }
    }

    /// <summary>The account for Web API calls: the requested one, else the current user's own, else a shared one.</summary>
    private async Task<SpotifyAccount> GetApiAccountAsync(string? preferredAccountId, CancellationToken cancellationToken)
    {
        var accounts = await _accounts.GetAccountsAsync(cancellationToken);
        var userId = _currentUser.UserId;
        return accounts.FirstOrDefault(a => a.Id.Equals(preferredAccountId, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault(a => userId is not null && a.OwnerUserId == userId)
            ?? accounts.FirstOrDefault(a => a.OwnerUserId is null)
            ?? (accounts.Count > 0 ? accounts[0] : null)
            ?? throw new AudioCentralException(ErrorCodes.SpotifyNotConfigured, "No Spotify account is linked.", 409);
    }

    private async Task<List<T>> GetPagedAsync<T>(SpotifyAccount account, string url, JsonTypeInfo<SpotifyPaging<T>> typeInfo, int max, CancellationToken cancellationToken)
        where T : class
    {
        var items = new List<T>();
        string? next = url;
        while (next is not null && items.Count < max)
        {
            var page = await GetAsync(account, next, typeInfo, cancellationToken);
            items.AddRange(page.Items?.OfType<T>() ?? []);
            next = page.Next;
        }

        return items;
    }

    private async Task<T> GetAsync<T>(SpotifyAccount account, string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", await _accounts.GetAccessTokenAsync(account, cancellationToken));
        using var response = await _httpClientFactory.CreateClient(SpotifyAccountService.ApiClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Spotify {Url} returned {Status}", url, (int)response.StatusCode);
            throw new HttpRequestException($"Spotify returned {(int)response.StatusCode} for {url}.", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
            ?? throw new HttpRequestException($"Spotify returned an empty response for {url}.");
    }

    private string PlayerName(string playerId) =>
        _players.Players.FirstOrDefault(p => p.Id.Equals(playerId, StringComparison.OrdinalIgnoreCase))?.Name ?? playerId;

    private static (string AccountId, string Folder) ParseFolder(string id)
    {
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (id, "") : (id[..slash], id[(slash + 1)..]);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private List<MediaItem> Remember(IEnumerable<MediaItem> items)
    {
        if (_seen.Count > MaxCachedItems)
        {
            _seen.Clear();
        }

        var list = items.ToList();
        foreach (var item in list)
        {
            _seen[item.Ref] = item;
        }

        return list;
    }
}
