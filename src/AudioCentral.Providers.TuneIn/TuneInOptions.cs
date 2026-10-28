namespace AudioCentral.Providers.TuneIn;

public sealed class TuneInOptions
{
    public const string SectionName = "Providers:TuneIn";

    public bool Enabled { get; set; } = true;

    /// <summary>TuneIn username; when set, its presets are shown as "Favorites".</summary>
    public string? Username { get; set; }

    /// <summary>Language for category names and descriptions.</summary>
    public string Language { get; set; } = "nl";

    /// <summary>Stream formats we accept, in the syntax of the OPML API.</summary>
    public string Formats { get; set; } = "mp3,aac,ogg,hls";
}
