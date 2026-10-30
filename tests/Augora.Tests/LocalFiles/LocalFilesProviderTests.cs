using Augora.Contracts;
using Augora.Core;
using Augora.Core.Media;
using Augora.Core.Providers;
using Augora.Core.Streaming;
using Augora.Providers.LocalFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Augora.Tests.LocalFiles;

public sealed class LocalFilesProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "augora-local", Guid.NewGuid().ToString("N"));
    private readonly LocalLibraryScanner _scanner;
    private readonly LocalFilesProvider _provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LocalFilesProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music", "Artist", "Album"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        WriteWav(Path.Combine(_root, "music", "Artist", "Album", "01 First song.wav"));
        WriteWav(Path.Combine(_root, "music", "Artist", "Album", "02 Second song.wav"));
        File.WriteAllText(Path.Combine(_root, "music", "Artist", "Album", "notes.txt"), "not audio");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "outside the music folder");

        var options = Options.Create(new LocalFilesOptions { Folders = [new LocalFolderOptions { Name = "Music", Path = Path.Combine(_root, "music") }] });
        var core = Options.Create(new AugoraOptions { DataDirectory = Path.Combine(_root, "data") });
        _scanner = new LocalLibraryScanner(options, core, new RuntimeSettings(), new TestEnvironment(_root), NullLogger<LocalLibraryScanner>.Instance);
        _provider = new LocalFilesProvider(_scanner);
    }

    public void Dispose()
    {
        _scanner.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Scans_audio_files_and_finds_them_by_file_name()
    {
        await _scanner.ScanAsync(Ct);

        Assert.Equal(2, _provider.TrackCount);
        var results = await _provider.SearchAsync("second", Ct);
        var track = Assert.Single(results);
        Assert.Equal("local:track:Music/Artist/Album/02 Second song.wav", track.Ref.ToString());
        Assert.True(File.Exists(Path.Combine(_root, "data", "local-library.json")));
    }

    [Fact]
    public async Task Browses_directories_and_plays_a_directory_as_its_tracks()
    {
        await _scanner.ScanAsync(Ct);

        var root = await _provider.BrowseAsync(null, Ct);
        var musicFolder = Assert.Single(root.Items, i => i.Name == "Music");
        var artist = Assert.Single((await _provider.BrowseAsync(musicFolder.Ref, Ct)).Items);
        var album = Assert.Single((await _provider.BrowseAsync(artist.Ref, Ct)).Items);
        var tracks = (await _provider.BrowseAsync(album.Ref, Ct)).Items;

        Assert.Equal(["01 First song", "02 Second song"], tracks.Select(t => t.Name));
        Assert.Equal(2, (await _provider.GetQueueItemsAsync(album, Ct)).Count);
    }

    [Fact]
    public async Task Resolves_tracks_to_files()
    {
        var item = new MediaItemRef(LocalFilesProvider.ProviderId, MediaType.Track, "Music/Artist/Album/01 First song.wav");

        var source = Assert.IsType<FileStreamSource>(await _provider.ResolveStreamAsync(item, new PlaybackContext("p"), Ct));

        Assert.Equal("audio/wav", source.ContentType);
        Assert.True(File.Exists(source.Path));
    }

    [Theory]
    [InlineData("Music/../secret.txt")]
    [InlineData("Music/../../secret.txt")]
    [InlineData("Other/file.wav")]
    public async Task Paths_outside_the_music_folders_are_rejected(string id)
    {
        var item = new MediaItemRef(LocalFilesProvider.ProviderId, MediaType.Track, id);

        await Assert.ThrowsAsync<AugoraException>(() => _provider.ResolveStreamAsync(item, new PlaybackContext("p"), Ct));
    }

    /// <summary>One second of silence, 8 kHz mono 16-bit.</summary>
    private static void WriteWav(string path)
    {
        const int sampleRate = 8000;
        const int dataSize = sampleRate * 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = root;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
