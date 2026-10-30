using System.Text;
using AudioCentral.Core.Streaming;

namespace AudioCentral.Tests.Core;

public sealed class IcyMetadataTests
{
    [Fact]
    public async Task Strips_metadata_blocks_and_reports_new_titles()
    {
        const int interval = 4;
        var source = new MemoryStream();
        source.Write([1, 2, 3, 4]);
        WriteBlock(source, "StreamTitle='Artist - Song';StreamUrl='';");
        source.Write([5, 6, 7, 8]);
        source.WriteByte(0);
        source.Write([9, 10, 11, 12]);
        WriteBlock(source, "StreamTitle='Artist - Song';");
        source.Write([13, 14]);
        source.Position = 0;

        var output = new MemoryStream();
        var titles = new List<string>();
        await IcyMetadata.CopyAudioAsync(source, output, interval, titles.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(1, 14).Select(i => (byte)i), output.ToArray());
        // The repeated title is reported once.
        Assert.Equal(["Artist - Song"], titles);
    }

    [Theory]
    [InlineData("StreamTitle='Queen - Bohemian Rhapsody';StreamUrl='http://x';", "Queen - Bohemian Rhapsody")]
    [InlineData("StreamTitle='Guns N' Roses - Don't Cry';", "Guns N' Roses - Don't Cry")]
    [InlineData("StreamTitle='';", "")]
    [InlineData("StreamUrl='http://x';", null)]
    public void Parses_stream_title(string block, string? expected)
    {
        var bytes = new byte[(Encoding.UTF8.GetByteCount(block) + 15) / 16 * 16];
        Encoding.UTF8.GetBytes(block, bytes);

        Assert.Equal(expected, IcyMetadata.ParseStreamTitle(bytes));
    }

    [Fact]
    public void Falls_back_to_latin1()
    {
        var bytes = Encoding.Latin1.GetBytes("StreamTitle='Beyoncé - Halo';");

        Assert.Equal("Beyoncé - Halo", IcyMetadata.ParseStreamTitle(bytes));
    }

    [Theory]
    [InlineData("Artist - Song", "Artist", "Song")]
    [InlineData("Artist - Song - Live", "Artist", "Song - Live")]
    [InlineData("Station jingle", null, "Station jingle")]
    public void Splits_artist_and_title(string streamTitle, string? artist, string title) =>
        Assert.Equal((artist, title), IcyMetadata.SplitTitle(streamTitle));

    private static void WriteBlock(Stream stream, string text)
    {
        var length = (Encoding.UTF8.GetByteCount(text) + 15) / 16;
        stream.WriteByte((byte)length);
        var block = new byte[length * 16];
        Encoding.UTF8.GetBytes(text, block);
        stream.Write(block);
    }
}
