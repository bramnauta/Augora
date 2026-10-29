using AudioCentral.Contracts;
using AudioCentral.Core.Media;

namespace AudioCentral.Providers.Spotify;

internal static class SpotifyMapper
{
    public const string ProviderId = "spotify";

    public static MediaItemRef Folder(string accountId, string folder = "") =>
        new(ProviderId, MediaType.Folder, folder.Length == 0 ? accountId : $"{accountId}/{folder}");

    public static MediaItem? Track(SpotifyTrack? track, SpotifyAlbum? album = null)
    {
        // Local files and podcast episodes in playlists cannot be streamed through the track api.
        if (track?.Id is null || track.IsLocal || track.Type is not (null or "track"))
        {
            return null;
        }

        var image = FirstImage((track.Album ?? album)?.Images);
        return new MediaItem(
            new MediaItemRef(ProviderId, MediaType.Track, track.Id),
            track.Name,
            Artists(track.Artists),
            image,
            IsPlayable: true,
            Duration: track.DurationMs > 0 ? TimeSpan.FromMilliseconds(track.DurationMs) : null);
    }

    public static MediaItem? Album(SpotifyAlbum? album) =>
        album is null
            ? null
            : new MediaItem(
                new MediaItemRef(ProviderId, MediaType.Album, album.Id),
                album.Name,
                JoinNonEmpty(Artists(album.Artists), album.ReleaseDate is { Length: >= 4 } date ? date[..4] : null),
                FirstImage(album.Images),
                IsPlayable: true,
                IsBrowsable: true);

    public static MediaItem? Artist(SpotifyArtist? artist) =>
        artist?.Id is null
            ? null
            : new MediaItem(new MediaItemRef(ProviderId, MediaType.Artist, artist.Id), artist.Name, ImageUrl: FirstImage(artist.Images), IsBrowsable: true);

    public static MediaItem? Playlist(SpotifyPlaylist? playlist) =>
        playlist is null
            ? null
            : new MediaItem(
                new MediaItemRef(ProviderId, MediaType.Playlist, playlist.Id),
                playlist.Name,
                playlist.Owner?.DisplayName,
                FirstImage(playlist.Images),
                IsPlayable: true,
                IsBrowsable: true);

    public static IEnumerable<MediaItem> NotNull(IEnumerable<MediaItem?> items) => items.OfType<MediaItem>();

    private static string? Artists(IReadOnlyList<SpotifyArtist>? artists) =>
        artists is { Count: > 0 } ? string.Join(", ", artists.Select(a => a.Name)) : null;

    private static string? FirstImage(IReadOnlyList<SpotifyImage>? images) => images is { Count: > 0 } ? images[0].Url : null;

    private static string? JoinNonEmpty(params string?[] parts)
    {
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? null : text;
    }
}
