using System.Text.Json.Serialization;

namespace Augora.Providers.Spotify;

// Subset of the Spotify Web API objects, as of the February 2026 changes (playlist "tracks" became "items").

internal sealed record SpotifyImage([property: JsonPropertyName("url")] string Url);

internal sealed record SpotifyArtist(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] IReadOnlyList<SpotifyImage>? Images);

internal sealed record SpotifyAlbum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] IReadOnlyList<SpotifyImage>? Images,
    [property: JsonPropertyName("artists")] IReadOnlyList<SpotifyArtist>? Artists,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("tracks")] SpotifyPaging<SpotifyTrack>? Tracks);

internal sealed record SpotifyTrack(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("artists")] IReadOnlyList<SpotifyArtist>? Artists,
    [property: JsonPropertyName("album")] SpotifyAlbum? Album,
    [property: JsonPropertyName("is_local")] bool IsLocal);

internal sealed record SpotifyOwner(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);

internal sealed record SpotifyPlaylist(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] IReadOnlyList<SpotifyImage>? Images,
    [property: JsonPropertyName("owner")] SpotifyOwner? Owner,
    [property: JsonPropertyName("collaborative")] bool Collaborative);

/// <summary>An entry of a playlist; <c>item</c> since February 2026, <c>track</c> before.</summary>
internal sealed record SpotifyPlaylistItem(
    [property: JsonPropertyName("item")] SpotifyTrack? Item,
    [property: JsonPropertyName("track")] SpotifyTrack? Track)
{
    public SpotifyTrack? Value => Item ?? Track;
}

internal sealed record SpotifySavedTrack([property: JsonPropertyName("track")] SpotifyTrack? Track);

internal sealed record SpotifySavedAlbum([property: JsonPropertyName("album")] SpotifyAlbum? Album);

internal sealed record SpotifyPaging<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T?>? Items,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("total")] int Total);

internal sealed record SpotifyFollowedArtists([property: JsonPropertyName("artists")] SpotifyCursorPaging<SpotifyArtist>? Artists);

internal sealed record SpotifyCursorPaging<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T?>? Items,
    [property: JsonPropertyName("next")] string? Next);

internal sealed record SpotifySearchResponse(
    [property: JsonPropertyName("tracks")] SpotifyPaging<SpotifyTrack>? Tracks,
    [property: JsonPropertyName("albums")] SpotifyPaging<SpotifyAlbum>? Albums,
    [property: JsonPropertyName("artists")] SpotifyPaging<SpotifyArtist>? Artists,
    [property: JsonPropertyName("playlists")] SpotifyPaging<SpotifyPlaylist>? Playlists);

internal sealed record SpotifyUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);

internal sealed record SpotifyTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("scope")] string? Scope);

[JsonSerializable(typeof(SpotifyAlbum))]
[JsonSerializable(typeof(SpotifyArtist))]
[JsonSerializable(typeof(SpotifyTrack))]
[JsonSerializable(typeof(SpotifyPlaylist))]
[JsonSerializable(typeof(SpotifyPaging<SpotifyPlaylist>))]
[JsonSerializable(typeof(SpotifyPaging<SpotifyPlaylistItem>))]
[JsonSerializable(typeof(SpotifyPaging<SpotifySavedTrack>))]
[JsonSerializable(typeof(SpotifyPaging<SpotifySavedAlbum>))]
[JsonSerializable(typeof(SpotifyPaging<SpotifyAlbum>))]
[JsonSerializable(typeof(SpotifyPaging<SpotifyTrack>))]
[JsonSerializable(typeof(SpotifyFollowedArtists))]
[JsonSerializable(typeof(SpotifySearchResponse))][JsonSerializable(typeof(SpotifyUser))]
[JsonSerializable(typeof(SpotifyTokenResponse))]
internal sealed partial class SpotifyJsonContext : JsonSerializerContext;
