using System.Text.Json;
using System.Text.Json.Serialization;
using AudioCentral.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioCentral.Providers.LocalFiles;

/// <summary>
/// Scans the music folders in the background and keeps the tag index. The index is saved in the data folder, so a
/// restart only re-reads files that changed.
/// </summary>
internal sealed class LocalLibraryScanner(
    IOptions<LocalFilesOptions> options,
    IOptions<AudioCentralOptions> coreOptions,
    IHostEnvironment environment,
    ILogger<LocalLibraryScanner> logger) : BackgroundService
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile LocalLibrary _library = LocalLibrary.Empty;

    public LocalLibrary Library => _library;

    public IReadOnlyList<LocalFolderOptions> Folders { get; } = options.Value.Folders.Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();

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
        if (Folders.Count == 0)
        {
            return;
        }

        await LoadAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.Value.RescanInterval);
        do
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scanning the music folders failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        _scanLock.Dispose();
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
internal sealed partial class LocalFilesJsonContext : JsonSerializerContext;
