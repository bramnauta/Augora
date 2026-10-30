using System.Text.Json.Serialization;

namespace Augora.Contracts;

// Protocol for players that play audio themselves and connect through the events hub:
// browser tabs now, the Android app later.

/// <summary>Hub methods called by a remote player (client → server).</summary>
public static class RemotePlayerHub
{
    /// <summary>Argument <see cref="RegisterPlayerRequest"/>, returns the player id (string).</summary>
    public const string Register = "RegisterPlayer";

    /// <summary>No arguments.</summary>
    public const string Unregister = "UnregisterPlayer";

    /// <summary>Argument <see cref="RemotePlayerStateDto"/>.</summary>
    public const string ReportState = "ReportPlayerState";

    /// <summary>Server → client: <see cref="RemotePlayerCommandDto"/>.</summary>
    public const string Command = "PlayerCommand";

    /// <summary>Server → client: another connection registered the same device; stop playing (no arguments).</summary>
    public const string Released = "PlayerReleased";
}

/// <param name="DeviceId">Stable per device/browser, so the player keeps its id (and queue) across reconnects.</param>
/// <param name="Ephemeral">A browser session: forgotten when it stays disconnected. Installed apps leave this off.</param>
public sealed record RegisterPlayerRequest(string DeviceId, string Name, PlayerKind Kind, bool Ephemeral = false);

[JsonConverter(typeof(JsonStringEnumConverter<RemotePlaybackEvent>))]
public enum RemotePlaybackEvent
{
    None,
    Finished,
    Error,
}

public sealed record RemotePlayerStateDto(
    PlaybackState State,
    double Volume,
    bool Muted,
    double? PositionSeconds,
    RemotePlaybackEvent Event = RemotePlaybackEvent.None,
    string? Error = null);

[JsonConverter(typeof(JsonStringEnumConverter<RemotePlayerCommandType>))]
public enum RemotePlayerCommandType
{
    Play,
    Pause,
    Resume,
    Stop,
    SetVolume,
    SetMuted,
    Seek,
}

/// <param name="PositionSeconds">For <see cref="RemotePlayerCommandType.Seek"/>: the position in the stream that plays.</param>
public sealed record RemotePlayerCommandDto(
    RemotePlayerCommandType Type,
    RemoteMediaDto? Media = null,
    double? Volume = null,
    bool? Muted = null,
    double? PositionSeconds = null);

/// <param name="StreamUrl">Relative to the server (<c>/api/stream/...</c>) or absolute for direct streams.</param>
public sealed record RemoteMediaDto(string StreamUrl, string ContentType, bool IsLive, string ItemUri, string Title, string? Subtitle, string? ImageUrl, double? DurationSeconds);
