namespace Augora.Core.Streaming;

/// <summary>
/// Where the audio for an item comes from. Providers return one of the derived types.
/// </summary>
public abstract record StreamSource(bool IsLive);

/// <summary>
/// Audio (or a playlist pointing to audio) available over HTTP. Resolved to a <see cref="ResolvedStream"/> before playback.
/// </summary>
public sealed record HttpStreamSource(Uri Url, bool IsLive, string? ContentType = null) : StreamSource(IsLive);

/// <summary>
/// The final audio location after unwrapping playlists and redirects.
/// </summary>
/// <param name="Direct">
/// The player must fetch <paramref name="Url"/> itself instead of going through the relay (HLS).
/// </param>
public sealed record ResolvedStream(Uri Url, string ContentType, bool IsLive, bool Direct) : StreamSource(IsLive);

/// <summary>A file on the server (local library, downloaded podcast). Served with range support.</summary>
public sealed record FileStreamSource(string Path, string ContentType) : StreamSource(IsLive: false);

/// <summary>
/// Audio produced on demand by the server, e.g. decoded by librespot and encoded by ffmpeg. <see cref="WriteAsync"/>
/// runs once per player request and writes the complete item; it returns when the item has ended.
/// </summary>
/// <param name="Continuous">Flow mode: the producer continues with following queue items through <c>PlaybackContext.Continuation</c>.</param>
/// <param name="Length">
/// The size of the complete item in bytes, when the producer knows it (constant bitrate and a known duration). With
/// <paramref name="WriteFromAsync"/> the stream is served with range support, so players seek in it themselves.
/// </param>
/// <param name="WriteFromAsync">Writes the item from a byte offset on: the producer starts at the matching time.</param>
public sealed record ProducedStreamSource(
    string ContentType,
    Func<Stream, CancellationToken, Task> WriteAsync,
    bool IsLive = false,
    bool Continuous = false,
    long? Length = null,
    Func<Stream, long, CancellationToken, Task>? WriteFromAsync = null)
    : StreamSource(IsLive)
{
    /// <summary>Served with range support: players seek in the stream themselves.</summary>
    public bool IsSeekable => Length is > 0 && WriteFromAsync is not null && !Continuous && !IsLive;
}

public static class StreamSourceExtensions
{
    /// <summary>Whether a player can seek in the stream itself (byte ranges); otherwise a seek restarts the item.</summary>
    public static bool IsSeekable(this StreamSource source) => source switch
    {
        FileStreamSource => true,
        ResolvedStream resolved => !resolved.IsLive,
        ProducedStreamSource produced => produced.IsSeekable,
        _ => false,
    };

    public static string GetContentType(this StreamSource source) => source switch
    {
        ResolvedStream resolved => resolved.ContentType,
        FileStreamSource file => file.ContentType,
        ProducedStreamSource produced => produced.ContentType,
        HttpStreamSource http => http.ContentType ?? "audio/mpeg",
        _ => "audio/mpeg",
    };
}
