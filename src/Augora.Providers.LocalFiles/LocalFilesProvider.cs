using Augora.Contracts;
using Augora.Core;
using Augora.Core.Media;
using Augora.Core.Providers;
using Augora.Core.Streaming;

namespace Augora.Providers.LocalFiles;

/// <summary>
/// Music on disk or a mounted NAS share: browse by folder, artist or album, search the tag index.
/// </summary>
/// <remarks>
/// Ids: tracks <c>local:track:{folder}/{path}</c>, directories <c>local:folder:{folder}/{path}</c>,
/// albums <c>local:album:{hash}</c>, artists <c>local:artist:{name}</c>; <c>local:folder:@artists</c> and
/// <c>@albums</c> are the index views.
/// </remarks>
public sealed class LocalFilesProvider : IMusicProvider
{
    public const string ProviderId = "local";
    public const string ArtworkPath = "/api/artwork/local/";
    private const string ArtistsFolder = "@artists";
    private const string AlbumsFolder = "@albums";
    private const int MaxSearchResults = 50;
    private static readonly string[] CoverFileNames = ["cover.jpg", "folder.jpg", "front.jpg", "cover.png", "folder.png"];

    private readonly LocalLibraryScanner _scanner;

    internal LocalFilesProvider(LocalLibraryScanner scanner) => _scanner = scanner;

    public string Id => ProviderId;

    public string Name => "Local music";

    public string Type => ProviderId;

    /// <summary>Without folders there is nothing to browse, so the provider stays out of the library.</summary>
    public bool IsAvailable => _scanner.Folders.Count > 0;

    public IReadOnlyList<LocalFolderOptions> Folders => _scanner.Folders;

    /// <summary>True when the folders were set in the UI instead of the configuration.</summary>
    public bool FoldersFromSettings => _scanner.FoldersFromSettings;

    /// <summary>Saves the folders and scans them; <c>null</c> goes back to the configuration.</summary>
    public Task SetFoldersAsync(IReadOnlyList<LocalFolderOptions>? folders, CancellationToken cancellationToken) =>
        _scanner.SetFoldersAsync(folders, cancellationToken);

    public TimeSpan RescanInterval => _scanner.RescanInterval;

    /// <summary>True when the interval was set in the UI instead of the configuration.</summary>
    public bool RescanIntervalFromSettings => _scanner.RescanIntervalFromSettings;

    /// <summary>Saves how often the folders are scanned, in minutes; <c>null</c> goes back to the configuration.</summary>
    public Task SetRescanMinutesAsync(int? minutes, CancellationToken cancellationToken)
    {
        if (minutes is < LocalLibraryScanner.MinRescanMinutes or > LocalLibraryScanner.MaxRescanMinutes)
        {
            throw new AugoraException(ErrorCodes.InvalidRequest,
                $"The interval must be between {LocalLibraryScanner.MinRescanMinutes} minutes and {LocalLibraryScanner.MaxRescanMinutes / 1440} days.", 400);
        }

        return _scanner.SetRescanMinutesAsync(minutes, cancellationToken);
    }

    /// <summary>Starts a scan now instead of waiting for the next interval.</summary>
    public Task RescanAsync(CancellationToken cancellationToken) => _scanner.ScanAsync(cancellationToken);

    public int TrackCount => _scanner.Library.Tracks.Count;

    public Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken)
    {
        var library = _scanner.Library;
        if (folder is not { } current)
        {
            List<MediaItem> roots =
            [
                new(Ref(MediaType.Folder, ArtistsFolder), "Artists", IsBrowsable: true),
                new(Ref(MediaType.Folder, AlbumsFolder), "Albums", IsBrowsable: true),
                .. _scanner.Folders.Select(f => new MediaItem(Ref(MediaType.Folder, f.Name + "/"), f.Name, IsBrowsable: true, IsPlayable: true)),
            ];
            return Task.FromResult(new BrowseResult(Name, roots));
        }

        var result = current.Type switch
        {
            MediaType.Folder when current.Id == ArtistsFolder => new BrowseResult("Artists",
                library.Artists.Keys.Order(StringComparer.CurrentCultureIgnoreCase).Select(Artist).ToList()),
            MediaType.Folder when current.Id == AlbumsFolder => new BrowseResult("Albums",
                library.Albums.Values.Select(Album).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList()),
            MediaType.Folder => BrowseDirectory(current.Id, library),
            MediaType.Artist when library.Artists.TryGetValue(current.Id, out var tracks) => new BrowseResult(current.Id,
                tracks.Where(t => t.AlbumKey is not null).Select(t => t.AlbumKey!).Distinct()
                    .Select(k => Album(library.Albums[k])).OrderByDescending(a => a.Subtitle, StringComparer.Ordinal).ToList()),
            MediaType.Album when library.Albums.TryGetValue(current.Id, out var tracks) => new BrowseResult(tracks[0].Album ?? "", tracks.Select(Track).ToList()),
            _ => new BrowseResult(Name, []),
        };
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var library = _scanner.Library;
        var tracks = library.Search(query).ToList();
        var artists = library.Artists.Keys.Where(a => a.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(10).Select(Artist);
        var albums = tracks.Where(t => t.AlbumKey is not null).Select(t => t.AlbumKey!).Distinct().Take(10).Select(k => Album(library.Albums[k]));
        IReadOnlyList<MediaItem> items = [.. artists, .. albums, .. tracks.Take(MaxSearchResults).Select(Track)];
        return Task.FromResult(items);
    }

    public Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken)
    {
        var library = _scanner.Library;
        MediaItem? result = item.Type switch
        {
            MediaType.Track when library.ByKey.TryGetValue(item.Id, out var track) => Track(track),
            MediaType.Track when FileForKey(item.Id) is { } path => new MediaItem(item, Path.GetFileNameWithoutExtension(path), IsPlayable: true),
            MediaType.Album when library.Albums.TryGetValue(item.Id, out var tracks) => Album(tracks),
            MediaType.Artist when library.Artists.ContainsKey(item.Id) => Artist(item.Id),
            MediaType.Folder when item.Id is ArtistsFolder or AlbumsFolder => new MediaItem(item, item.Id == ArtistsFolder ? "Artists" : "Albums", IsBrowsable: true),
            MediaType.Folder when DirectoryForKey(item.Id) is { } directory =>
                new MediaItem(item, Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)), IsBrowsable: true, IsPlayable: true),
            _ => null,
        };
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MediaItem>> GetQueueItemsAsync(MediaItem item, CancellationToken cancellationToken)
    {
        var library = _scanner.Library;
        IReadOnlyList<MediaItem> items = item.Type switch
        {
            MediaType.Track => [item],
            MediaType.Album when library.Albums.TryGetValue(item.Ref.Id, out var tracks) => tracks.Select(Track).ToList(),
            // A directory plays its own files, not those of subdirectories.
            MediaType.Folder => BrowseDirectory(item.Ref.Id, library).Items.Where(i => i.Type == MediaType.Track).ToList(),
            _ => [],
        };
        return Task.FromResult(items);
    }

    /// <summary>Other tracks by the same artist, then tracks from the same folder; the tags have no genre to go by.</summary>
    public Task<IReadOnlyList<MediaItem>> GetSimilarAsync(MediaItem seed, int count, IReadOnlySet<MediaItemRef> exclude, CancellationToken cancellationToken)
    {
        var library = _scanner.Library;
        if (seed.Type != MediaType.Track || !library.ByKey.TryGetValue(seed.Ref.Id, out var track))
        {
            return Task.FromResult<IReadOnlyList<MediaItem>>([]);
        }

        bool IsNew(LocalTrack t) => !exclude.Contains(Ref(MediaType.Track, t.Key));
        var sameArtist = track.EffectiveArtist.Length > 0 && library.Artists.TryGetValue(track.EffectiveArtist, out var byArtist)
            ? byArtist.Where(IsNew).ToList()
            : [];
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sameArtist));

        var picked = sameArtist.Take(count).ToList();
        if (picked.Count < count)
        {
            var sameFolder = library.Tracks.Where(t => t.Folder == track.Folder && IsNew(t) && !picked.Contains(t)).ToList();
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sameFolder));
            picked.AddRange(sameFolder.Take(count - picked.Count));
        }

        return Task.FromResult<IReadOnlyList<MediaItem>>(picked.Select(Track).ToList());
    }

    public Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken)
    {
        var path = item.Type == MediaType.Track ? FileForKey(item.Id) : null;
        if (path is null || !File.Exists(path))
        {
            throw new AugoraException(ErrorCodes.ItemNotFound, $"File '{item.Id}' does not exist.", 404);
        }

        return Task.FromResult<StreamSource>(new FileStreamSource(path, AudioContentTypes.FromFileName(path)));
    }

    /// <summary>Embedded artwork of a track, or the cover image in its directory.</summary>
    public async Task<(byte[] Data, string ContentType)?> GetArtworkAsync(string token, CancellationToken cancellationToken)
    {
        if (!_scanner.Library.ByArtworkToken.TryGetValue(token, out var track) || FileForKey(track.Key) is not { } path)
        {
            return null;
        }

        if (track.HasEmbeddedPicture)
        {
            try
            {
                using var file = TagLib.File.Create(path);
                if (file.Tag.Pictures is [var picture, ..])
                {
                    return (picture.Data.Data, string.IsNullOrEmpty(picture.MimeType) ? "image/jpeg" : picture.MimeType);
                }
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
            {
                // Fall back to a cover file.
            }
        }

        var directory = Path.GetDirectoryName(path)!;
        foreach (var name in CoverFileNames)
        {
            var cover = Path.Combine(directory, name);
            if (File.Exists(cover))
            {
                return (await File.ReadAllBytesAsync(cover, cancellationToken), name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
            }
        }

        return null;
    }

    private BrowseResult BrowseDirectory(string id, LocalLibrary library)
    {
        if (DirectoryForKey(id) is not { } directory || !Directory.Exists(directory))
        {
            return new BrowseResult(Name, []);
        }

        var prefix = id.EndsWith('/') ? id : id + "/";
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        var directories = Directory.EnumerateDirectories(directory, "*", options)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.StartsWith('.'))
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new MediaItem(Ref(MediaType.Folder, prefix + n + "/"), n, IsBrowsable: true, IsPlayable: true));
        var files = Directory.EnumerateFiles(directory, "*", options)
            .Where(f => LocalLibrary.Extensions.Contains(Path.GetExtension(f)))
            .Select(f => prefix + Path.GetFileName(f))
            .Select(key => library.ByKey.TryGetValue(key, out var track)
                ? Track(track)
                : new MediaItem(Ref(MediaType.Track, key), Path.GetFileNameWithoutExtension(key), IsPlayable: true))
            .OrderBy(i => library.ByKey.TryGetValue(i.Ref.Id, out var t) ? t.Disc * 1000 + t.Track : 0)
            .ThenBy(i => i.Ref.Id, StringComparer.CurrentCultureIgnoreCase);

        var title = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        return new BrowseResult(title, [.. directories, .. files]);
    }

    private string? FileForKey(string key) =>
        key.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 ? _scanner.Resolve(key[..slash], key[(slash + 1)..]) : null;

    private string? DirectoryForKey(string key) =>
        key.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 ? _scanner.Resolve(key[..slash], key[(slash + 1)..].TrimEnd('/')) : null;

    private static MediaItemRef Ref(MediaType type, string id) => new(ProviderId, type, id);

    private static MediaItem Track(LocalTrack track) => new(
        Ref(MediaType.Track, track.Key),
        track.Title,
        string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } subtitle ? subtitle : null,
        ArtworkPath + track.ArtworkToken,
        IsPlayable: true,
        Duration: track.DurationSeconds > 0 ? TimeSpan.FromSeconds(Math.Round(track.DurationSeconds)) : null);

    private static MediaItem Album(IReadOnlyList<LocalTrack> tracks)
    {
        var first = tracks[0];
        var year = tracks.Select(t => t.Year).FirstOrDefault(y => y > 0);
        return new MediaItem(
            Ref(MediaType.Album, first.AlbumKey!),
            first.Album!,
            string.Join(" · ", new[] { first.EffectiveArtist, year > 0 ? year.ToString(System.Globalization.CultureInfo.InvariantCulture) : null }.Where(s => !string.IsNullOrEmpty(s))),
            ArtworkPath + first.ArtworkToken,
            IsPlayable: true,
            IsBrowsable: true);
    }

    private static MediaItem Artist(string name) => new(Ref(MediaType.Artist, name), name, IsBrowsable: true);
}
