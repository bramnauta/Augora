namespace AudioCentral.Core;

public sealed class AudioCentralOptions
{
    public const string SectionName = "AudioCentral";

    /// <summary>Folder for the database and caches. <c>/data</c> in the container.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Base url under which players (Cast devices, ...) reach the stream endpoint, e.g.
    /// <c>http://192.168.1.10:4727</c>. When empty, the LAN address of the host is detected.
    /// Set this when the server sits behind NAT or in another VLAN than the players.
    /// </summary>
    public string? StreamBaseUrl { get; set; }

    /// <summary>Port used for auto-detected stream urls. Defaults to the first HTTP port the server listens on.</summary>
    public int? StreamPort { get; set; }

    /// <summary>Public url of the UI when running behind a reverse proxy. Used for links and OAuth callbacks.</summary>
    public string? PublicBaseUrl { get; set; }
}
