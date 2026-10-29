using System.Text.Json.Serialization;

namespace AudioCentral.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PlayerKind>))]
public enum PlayerKind
{
    Speaker,
    Tv,
    /// <summary>A group managed by the device ecosystem itself (e.g. a Google Cast group).</summary>
    DeviceGroup,
    /// <summary>A group managed by AudioCentral (synchronised across player types). Reserved for later.</summary>
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
public sealed record PlayRequest(string Uri, QueueOption Option = QueueOption.Replace, string? TakeoverPlayerId = null, IReadOnlyList<string>? Following = null);

public sealed record VolumeRequest(double Level);

public sealed record MuteRequest(bool Muted);

public sealed record QueueDto(
    string PlayerId,
    IReadOnlyList<QueueItemDto> Items,
    int? CurrentIndex,
    bool Shuffle,
    RepeatMode Repeat);

public sealed record QueueItemDto(string Id, MediaItemDto Item);
