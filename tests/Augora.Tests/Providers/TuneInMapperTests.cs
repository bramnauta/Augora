using System.Text.Json;
using Augora.Contracts;
using Augora.Providers.TuneIn;

namespace Augora.Tests.Providers;

public sealed class TuneInMapperTests
{
    // Trimmed real responses of opml.radiotime.com (render=json).
    private const string LocalRadio = """
        { "head": { "title": "Local Radio", "status": "200" }, "body": [
          { "element": "outline", "text": "Stations", "key": "stations", "children": [
            { "element": "outline", "type": "audio", "text": "Radio 538", "URL": "http://opml.radiotime.com/Tune.ashx?id=s6712&formats=mp3,aac",
              "bitrate": "128", "reliability": "96", "guide_id": "s6712", "subtext": "Jouw hits", "item": "station",
              "image": "http://cdn-profiles.tunein.com/s6712/images/logoq.png?t=1" }
          ] },
          { "element": "outline", "type": "link", "text": "More Stations", "URL": "http://opml.radiotime.com/Browse.ashx?id=r100385&formats=mp3,aac", "key": "more" },
          { "element": "outline", "type": "link", "text": "The 538", "URL": "http://opml.radiotime.com/Tune.ashx?c=pbrowse&id=p2226713&formats=mp3", "item": "show" },
          { "element": "outline", "type": "audio", "text": "Episode 1", "URL": "http://opml.radiotime.com/Tune.ashx?id=t1&sid=p2", "guide_id": "t1",
            "stream_type": "download", "topic_duration": "1581", "item": "topic" },
          { "element": "outline", "type": "text", "text": "No more stations" }
        ] }
        """;

    [Fact]
    public void Flattens_sections_and_maps_stations_folders_shows_and_episodes()
    {
        var response = JsonSerializer.Deserialize(LocalRadio, TuneInJsonContext.Default.OpmlResponseOpmlOutline)!;

        var items = TuneInMapper.MapOutlines("tunein", response.Body!).ToList();

        Assert.Collection(
            items,
            station =>
            {
                Assert.Equal("tunein:radio:s6712", station.Ref.ToString());
                Assert.True(station.IsPlayable);
                Assert.Equal("https://cdn-profiles.tunein.com/s6712/images/logoq.png?t=1", station.ImageUrl);
            },
            folder =>
            {
                Assert.Equal("tunein:folder:Browse.ashx?id=r100385", folder.Ref.ToString());
                Assert.True(folder.IsBrowsable);
            },
            show => Assert.Equal("tunein:podcast:Tune.ashx?c=pbrowse&id=p2226713", show.Ref.ToString()),
            episode =>
            {
                Assert.Equal(MediaType.Episode, episode.Type);
                Assert.Equal(TimeSpan.FromSeconds(1581), episode.Duration);
            });
    }

    [Fact]
    public void Prefers_reliable_progressive_streams_over_hls()
    {
        OpmlStream[] streams =
        [
            new("http://a/hls.m3u8", "hls", 256, 100, "false"),
            new("http://a/low.mp3", "mp3", 64, 99, "false"),
            new("http://a/high.mp3", "mp3", 128, 99, "false"),
            new("not a url", "mp3", 320, 100, "false"),
        ];

        Assert.Equal("http://a/high.mp3", TuneInMapper.SelectStream(streams)?.Url);
    }
}
