using AudioCentral.Core.Streaming;

namespace AudioCentral.Tests.Core;

public sealed class PlaylistParserTests
{
    private static readonly Uri Base = new("http://example.com/radio/listen.pls");

    [Fact]
    public void Parses_pls()
    {
        const string pls = """
            [playlist]
            NumberOfEntries=2
            File1=http://stream1.example.com:8000/live
            Title1=Main
            File2=http://stream2.example.com/live
            """;

        var entries = PlaylistParser.Parse(pls, Base);

        Assert.Equal([new Uri("http://stream1.example.com:8000/live"), new Uri("http://stream2.example.com/live")], entries);
    }

    [Fact]
    public void Parses_m3u_and_resolves_relative_entries()
    {
        const string m3u = """
            #EXTM3U
            #EXTINF:-1,Station
            live.mp3

            https://cdn.example.com/backup.aac
            """;

        var entries = PlaylistParser.Parse(m3u, Base);

        Assert.Equal([new Uri("http://example.com/radio/live.mp3"), new Uri("https://cdn.example.com/backup.aac")], entries);
    }

    [Fact]
    public void Parses_asx()
    {
        const string asx = """<asx version="3.0"><entry><ref href="http://example.com/stream.wma" /></entry></asx>""";

        Assert.Equal([new Uri("http://example.com/stream.wma")], PlaylistParser.Parse(asx, Base));
    }

    [Fact]
    public void Ignores_non_http_entries() =>
        Assert.Empty(PlaylistParser.Parse("file:///etc/passwd\nrtsp://example.com/x", Base));

    [Fact]
    public void Detects_hls() =>
        Assert.True(PlaylistParser.IsHls("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nchunklist.m3u8"));

    [Theory]
    [InlineData("http://x/a.pls", null, true)]
    [InlineData("http://x/a.M3U", null, true)]
    [InlineData("http://x/stream", "audio/x-mpegurl", true)]
    [InlineData("http://x/stream", "audio/mpeg", false)]
    [InlineData("http://x/stream.mp3", null, false)]
    public void Recognises_playlists(string url, string? contentType, bool expected) =>
        Assert.Equal(expected, PlaylistParser.LooksLikePlaylist(new Uri(url), contentType));
}
