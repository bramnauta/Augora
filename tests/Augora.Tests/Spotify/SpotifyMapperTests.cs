using System.Text;
using System.Text.Json;
using Augora.Contracts;
using Augora.Providers.Spotify;
using Augora.Providers.Spotify.Librespot;

namespace Augora.Tests.Spotify;

public sealed class SpotifyMapperTests
{
    [Fact]
    public void Playlist_items_are_read_in_the_old_and_the_new_format()
    {
        const string json = """
            {
              "items": [
                { "item": { "id": "t1", "name": "New", "type": "track", "duration_ms": 180000, "is_local": false,
                            "artists": [{ "id": "a", "name": "Artist" }],
                            "album": { "id": "al", "name": "Album", "images": [{ "url": "https://img/1" }] } } },
                { "track": { "id": "t2", "name": "Old", "type": "track", "duration_ms": 1000, "is_local": false } },
                { "item": { "id": "e1", "name": "Episode", "type": "episode", "duration_ms": 1000, "is_local": false } },
                { "item": { "id": null, "name": "Local", "type": "track", "duration_ms": 1000, "is_local": true } },
                null
              ],
              "next": null,
              "total": 5
            }
            """;

        var page = JsonSerializer.Deserialize(json, SpotifyJsonContext.Default.SpotifyPagingSpotifyPlaylistItem)!;
        var tracks = SpotifyMapper.NotNull(page.Items!.Select(i => SpotifyMapper.Track(i?.Value))).ToList();

        Assert.Equal(["spotify:track:t1", "spotify:track:t2"], tracks.Select(t => t.Ref.ToString()));
        Assert.Equal("Artist", tracks[0].Subtitle);
        Assert.Equal("https://img/1", tracks[0].ImageUrl);
        Assert.Equal(TimeSpan.FromMinutes(3), tracks[0].Duration);
        Assert.True(tracks[0].IsPlayable);
    }

    [Fact]
    public void Albums_and_playlists_are_playable_folders_and_artists_browsable()
    {
        var album = SpotifyMapper.Album(new SpotifyAlbum("al", "Album", null, [new SpotifyArtist("a", "Artist", null)], "2021-04-01", null))!;
        var artist = SpotifyMapper.Artist(new SpotifyArtist("a", "Artist", null))!;

        Assert.Equal(MediaType.Album, album.Type);
        Assert.Equal("Artist · 2021", album.Subtitle);
        Assert.True(album.IsPlayable && album.IsBrowsable);
        Assert.True(artist.IsBrowsable);
        Assert.False(artist.IsPlayable);
    }

    [Theory]
    [InlineData("""{"type":"not_playing","data":{"uri":"spotify:track:x","play_origin":"go-librespot"}}""", "not_playing", "spotify:track:x", null)]
    [InlineData("""{"type":"playback_error","data":{"uri":"spotify:track:y","message":"unplayable"}}""", "playback_error", "spotify:track:y", "unplayable")]
    [InlineData("""{"type":"stopped","data":{"play_origin":"go-librespot"}}""", "stopped", null, null)]
    public void Librespot_events_are_parsed(string json, string type, string? uri, string? message)
    {
        var e = LibrespotDaemon.ParseEvent(Encoding.UTF8.GetBytes(json));

        Assert.Equal(new LibrespotEvent(type, uri, message), e);
    }

    [Fact]
    public void The_error_of_a_librespot_log_line_is_extracted()
    {
        const string line = """time="2026-10-29T03:42:53Z" level=fatal msg="daemon exited with error" error="failed authenticating: BadCredentials <nil>" """;

        Assert.Equal("failed authenticating: BadCredentials <nil>", LibrespotDaemon.ExtractError(line));
    }

    [Fact]
    public void The_pairing_code_and_the_logged_in_user_are_read()
    {
        const string code = """{"url":"https://spotify.com/pair?code=TD23RP","code":"TD23RP","expires_at":"2026-10-29T21:46:34Z"}""";

        var pairing = JsonSerializer.Deserialize(code, LibrespotJsonContext.Default.DeviceAuthCode)!;

        Assert.Equal("TD23RP", pairing.Code);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 21, 46, 34, TimeSpan.Zero), pairing.ExpiresAt);
        Assert.Equal("bramnauta", LibrespotDaemon.Username(Encoding.UTF8.GetBytes("""{"username":"bramnauta","stopped":true}""")));
        Assert.Null(LibrespotDaemon.Username(Encoding.UTF8.GetBytes("""{"username":""}""")));
    }

    [Fact]
    public void Invalid_librespot_events_are_ignored() =>
        Assert.Null(LibrespotDaemon.ParseEvent(Encoding.UTF8.GetBytes("not json")));
}
