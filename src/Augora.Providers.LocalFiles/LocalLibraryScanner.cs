using System.Text.Json;
using System.Text.Json.Serialization;
using Augora.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Augora.Providers.LocalFiles;

/// <summary>
/// Scans the music folders in the background and keeps the tag index. The index is saved in the data folder, so a
/// restart only re-reads files that changed.
/// </summary>
internal sealed class LocalLibraryScanner(
    IOptions<LocalFilesOptions> options,
    IOptions<AugoraOptions> coreOptions,
    IRuntimeSettings settings,
    IHostEnvironment environment,
    ILogger<LocalLibraryScanner> logger) : BackgroundService
{
    internal const string FoldersSettingKey = "LocalFiles:Folders";

    internal const string RescanMinutesSettingKey = "LocalFiles:RescanMinutes";
    internal const int MinRescanMinutes = 5;
    internal const int MaxRescanMinutes = 7 * 24 * 60;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private volatile LocalLibrary _library = LocalLibrary.Empty;
    private volatile FolderCache? _folders;

    public LocalLibrary Library => _library;

    /// <summary>True when the folders were set in the UI instead of the configuration.</summary>
    public bool FoldersFromSettings => settings.Get(FoldersSettingKey) is not null;

    /// <summary>How often the folders are scanned: set in the UI, or else the configuration.</summary>
    public TimeSpan RescanInterval =>
        int.TryParse(settings.Get(RescanMinutesSettingKey), out var minutes) && minutes is >= MinRescanMinutes and <= MaxRescanMinutes
            ? TimeSpan.FromMinutes(minutes)
            : options.Value.RescanInterval;

    /// <summary>True when the interval was set in the UI instead of the configuration.</summary>
    public bool RescanIntervalFromSettings => settings.Get(RescanMinutesSettingKey) is not null;

    /// <summary>Saves the interval in minutes; <c>null</c> goes back to the configuration.</summary>
    public Task SetRescanMinutesAsync(int? minutes, CancellationToken cancellationToken) =>
        settings.SetAsync(RescanMinutesSettingKey, minutes?.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>The folders set in the UI, or else those in the configuration.</summary>
    public IReadOnlyList<LocalFolderOptions> Folders
    {
        get
        {
            var json = settings.Get(FoldersSettingKey);
            if (_folders is { } cached && cached.Json == json)
            {
                return cached.Folders;
            }

            var folders = options.Value.Folders;
            if (json is not null)
            {
                try
                {
                    folders = JsonSerializer.Deserialize(json, LocalFilesJsonContext.Default.ListLocalFolderOptions) ?? [];
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "The music folders saved in the settings could not be read; using the configuration");
                }
            }

            var result = folders.Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
            _folders = new FolderCache(json, result);
            return result;
        }
    }

    private sealed record FolderCache(string? Json, IReadOnlyList<LocalFolderOptions> Folders);

    /// <summary>Saves the folders; an empty list falls back to the configuration.</summary>
    public Task SetFoldersAsync(IReadOnlyList<LocalFolderOptions>? folders, CancellationToken cancellationToken) =>
        settings.SetAsync(
            FoldersSettingKey,
            folders is null ? null : JsonSerializer.Serialize(folders.ToList(), LocalFilesJsonContext.Default.ListLocalFolderOptions),
            cancellationToken);

    /// <summary>The full path of a folder name + relative path, or <c>null</c> when it escapes the folder.</summary>
    public string? Resolve(string folderName, string relativePath)
    {
        var folder = Folders.FirstOrDefault(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            return null;
        }

        var root = Path.GetFullPath(folder.Path);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.Equals(root, StringComparison.Ordinal) || full.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? full : null;
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            var previous = _library.ByKey;
            var tracks = new List<LocalTrack>();
            var read = 0;
            foreach (var folder in Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder.Path))
                {
                    logger.LogWarning("Music folder {Folder} ({Path}) does not exist", folder.Name, folder.Path);
                    continue;
                }

                var root = Path.GetFullPath(folder.Path);
                var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => LocalLibrary.Extensions.Contains(Path.GetExtension(f)));
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileInfo(file);
                    var key = $"{folder.Name}/{Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')}";
                    if (previous.TryGetValue(key, out var known) && known.Size == info.Length && known.LastWriteUtc == info.LastWriteTimeUtc)
                    {
                        tracks.Add(known);
                        continue;
                    }

                    tracks.Add(ReadTags(key, info));
                    read++;
                }
            }

            _library = new LocalLibrary(tracks);
            logger.LogInformation("Local library: {Count} tracks, {Read} (re)read", tracks.Count, read);
            if (read > 0 || tracks.Count != previous.Count)
            {
                await SaveAsync(tracks, cancellationToken);
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }

    internal static LocalTrack ReadTags(string key, FileInfo info)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(info.Name);
        try
        {
            using var file = TagLib.File.Create(info.FullName);
            var tag = file.Tag;
            return new LocalTrack(
                key,
                NullIfEmpty(tag.Title) ?? fallbackTitle,
                NullIfEmpty(tag.JoinedPerformers),
                NullIfEmpty(tag.JoinedAlbumArtists),
                NullIfEmpty(tag.Album),
                tag.Disc,
                tag.Track,
                tag.Year,
                file.Properties?.Duration.TotalSeconds ?? 0,
                tag.Pictures.Length > 0,
                info.Length,
                info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            return new LocalTrack(key, fallbackTitle, null, null, null, 0, 0, 0, 0, false, info.Length, info.LastWriteTimeUtc);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken);

        // New or removed folders and a new interval are picked up at once instead of at the next interval.
        EventHandler<string> onChanged = (_, key) =>
        {
            if (key is FoldersSettingKey or RescanMinutesSettingKey)
            {
                _wake.Release();
            }
        };
        settings.Changed += onChanged;
        try
        {
            while (true)
            {
                await ScanQuietlyAsync(stoppingToken);
                await _wake.WaitAsync(RescanInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settings.Changed -= onChanged;
        }
    }

    private async Task ScanQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scanning the music folders failed");
        }
    }

    public override void Dispose()
    {
        _scanLock.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    private string IndexPath => Path.Combine(Path.GetFullPath(coreOptions.Value.DataDirectory, environment.ContentRootPath), "local-library.json");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                await using var stream = File.OpenRead(IndexPath);
                var tracks = await JsonSerializer.DeserializeAsync(stream, LocalFilesJsonContext.Default.ListLocalTrack, cancellationToken);
                _library = new LocalLibrary(tracks ?? []);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            logger.LogWarning(ex, "The saved local library index could not be read; rescanning everything");
        }
    }

    private async Task SaveAsync(List<LocalTrack> tracks, CancellationToken cancellationToken)
    {
        var temp = IndexPath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, tracks, LocalFilesJsonContext.Default.ListLocalTrack, cancellationToken);
        }

        File.Move(temp, IndexPath, overwrite: true);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

[JsonSerializable(typeof(List<LocalTrack>))]
[JsonSerializable(typeof(List<LocalFolderOptions>))]
internal sealed partial class LocalFilesJsonContext : JsonSerializerContext;
