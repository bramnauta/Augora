namespace AudioCentral.Providers.Spotify;

public sealed class SpotifyOptions
{
    public const string SectionName = "Providers:Spotify";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Client id of your app on developer.spotify.com. Accounts can override it, so every household member can use
    /// their own app (a development mode app allows only a few users).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Path of the go-librespot daemon; in the container it is on the PATH.</summary>
    public string LibrespotPath { get; set; } = "go-librespot";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Bitrate librespot streams from Spotify: 96, 160 or 320.</summary>
    public int Bitrate { get; set; } = 320;

    /// <summary>
    /// How long an account stays reserved for a player after its last track ended, so the next queue item can use it.
    /// </summary>
    public TimeSpan AccountHoldTime { get; set; } = TimeSpan.FromMinutes(2);
}
