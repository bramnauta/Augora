namespace AudioCentral.Core.Streaming;

/// <summary>
/// Where the audio for an item comes from. Providers return one of the derived types;
/// later additions are local files and producer processes (librespot).
/// </summary>
public abstract record StreamSource(bool IsLive);

/// <summary>
/// Audio (or a playlist pointing to audio) available over HTTP.
/// </summary>
public sealed record HttpStreamSource(Uri Url, bool IsLive, string? ContentType = null) : StreamSource(IsLive);
