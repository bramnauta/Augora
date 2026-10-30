using System.Text;
using Augora.Providers.Spotify.Soloist;

namespace Augora.Tests.Spotify;

public sealed class SoloistDaemonTests
{
    [Theory]
    [InlineData("""{"type":"playback_changed","status":"paused"}""", "playback_changed", "paused", null, null)]
    [InlineData("""{"type":"track_changed","item":{"uri":"spotify:track:x","entity_type":"track","decorations":{}}}""", "track_changed", null, "spotify:track:x", null)]
    [InlineData("""{"type":"auth_state","logged_in":true,"is_active":false,"device_name":"Augora (Bram)"}""", "auth_state", null, null, true)]
    public void Events_are_parsed(string json, string type, string? status, string? uri, bool? loggedIn)
    {
        var e = SoloistDaemon.Parse(Encoding.UTF8.GetBytes(json))!;

        Assert.Equal((type, status, uri, loggedIn), (e.Type, e.Status, e.Uri, e.LoggedIn));
    }

    [Fact]
    public void The_playback_snapshot_has_status_uri_and_position()
    {
        const string json = """
            {"type":"playback_state","status":"playing","item":{"uri":"spotify:track:y","entity_type":"track"},
             "position":{"position_ms":45000,"timestamp_ms":1747654321000,"speed":1.0},"volume":65,"is_active":true}
            """;

        var e = SoloistDaemon.Parse(Encoding.UTF8.GetBytes(json))!;

        Assert.Equal(("playing", "spotify:track:y", 45000L), (e.Status, e.Uri, e.PositionMs));
    }
}
