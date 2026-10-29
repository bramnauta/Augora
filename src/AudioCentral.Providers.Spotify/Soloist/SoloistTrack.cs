namespace AudioCentral.Providers.Spotify.Soloist;

/// <param name="Duration">From the Web API; used to start the next track just before this one ends.</param>
internal sealed record SoloistTrack(string Id, TimeSpan? Duration);
