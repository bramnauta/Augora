using System.Text.Json.Serialization;

namespace Augora.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PlayerKind>))]
public enum PlayerKind
{
    Speaker,
    Tv,
    /// <summary>A group managed by the device ecosystem itself (e.g. a Google Cast group).</summary>
    DeviceGroup,
    /// <summary>A group managed by Augora (synchronised across player types). Reserved for later.</summary>
    SyncGroup,
    Browser,
    Phone,
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackState>))]
public enum PlaybackState
{
    Idle,
    Buffering,
    Playing,
    Paused,
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<PlayerFeatures>))]
public enum PlayerFeatures
{
    None = 0,
    Volume = 1,
    Mute = 2,
    Pause = 4,
    Seek = 8,
}

public sealed record PlayerDto(
    string Id,
    string Name,
    string Provider,
    PlayerKind Kind,
    bool Available,
    PlaybackState State,
    double Volume,
    bool Muted,
    PlayerFeatures Features,
    NowPlayingDto? NowPlaying,
    /// <summary>Set when another app (e.g. Spotify Connect or YouTube) is controlling the device.</summary>
    string? ExternalSource,
    string? Model);

public sealed record NowPlayingDto(
    string? ItemUri,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    bool IsLive,
    TimeSpan? Position,
    TimeSpan? Duration);

[JsonConverter(typeof(JsonStringEnumConverter<QueueOption>))]
public enum QueueOption
{
    /// <summary>Replace the queue and start playing.</summary>
    Replace,
    /// <summary>Insert after the current item and start playing it.</summary>
    Play,
    /// <summary>Insert after the current item.</summary>
    Next,
    /// <summary>Append to the end of the queue.</summary>
    Add,
}

[JsonConverter(typeof(JsonStringEnumConverter<RepeatMode>))]
public enum RepeatMode
{
    Off,
    One,
    All,
}

/// <param name="TakeoverPlayerId">
/// When no Spotify account is free (<c>spotify_no_free_account</c>): stop this player and use its account.
/// </param>
/// <param name="Following">
/// "Play from here": the uris after <paramref name="Uri"/> in the list it was chosen from (album, playlist),
/// queued after it so playback continues.
/// </param>
/// <param name="Autoplay">Sets autoplay of the queue (track radio) together with starting; null leaves it as it is.</param>
public sealed record PlayRequest(
    string Uri,
    QueueOption Option = QueueOption.Replace,
    string? TakeoverPlayerId = null,
    IReadOnlyList<string>? Following = null,
    bool? Autoplay = null);

/// <param name="Url">Absolute http(s) url of the audio to announce (e.g. a Home Assistant TTS file); the player fetches it itself.</param>
/// <param name="Volume">Level (0-1) for the announcement; the player returns to its own level afterwards. Null leaves the volume alone.</param>
/// <param name="Resume">Continue what the player was playing, from where it was interrupted, once the announcement is over.</param>
/// <param name="ContentType">Defaults to <c>audio/mpeg</c>.</param>
public sealed record AnnounceRequest(string Url, double? Volume = null, bool Resume = true, string? ContentType = null);

public sealed record VolumeRequest(double Level);

public sealed record MuteRequest(bool Muted);

/// <param name="PositionSeconds">The position in the current item, as <see cref="NowPlayingDto.Position"/> shows it.</param>
public sealed record SeekRequest(double PositionSeconds);

/// <param name="Autoplay">
/// Track radio: when the queue ends, similar tracks (from the provider of the last item) are added and keep playing.
/// </param>
public sealed record QueueDto(
    string PlayerId,
    IReadOnlyList<QueueItemDto> Items,
    int? CurrentIndex,
    bool Shuffle,
    RepeatMode Repeat,
    bool Autoplay = false);

public sealed record QueueItemDto(string Id, MediaItemDto Item);
