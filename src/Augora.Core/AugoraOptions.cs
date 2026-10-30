namespace Augora.Core;

public sealed class AugoraOptions
{
    public const string SectionName = "Augora";

    /// <summary>
    /// Folder for the database, login keys and caches. <c>/data</c> in the container. Not <c>data</c>:
    /// on Windows that is the same folder as the server's <c>Data</c> source folder.
    /// </summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>
    /// Base url under which players (Cast devices, ...) reach the stream endpoint, e.g.
    /// <c>http://192.168.1.10:4727</c>. When empty, the LAN address of the host is detected.
    /// Set this when the server sits behind NAT or in another VLAN than the players.
    /// </summary>
    public string? StreamBaseUrl { get; set; }

    /// <summary>Port used for auto-detected stream urls. Defaults to the first HTTP port the server listens on.</summary>
    public int? StreamPort { get; set; }

    /// <summary>Announce the server on the local network (mDNS) so Home Assistant can discover it.</summary>
    public bool AdvertiseMdns { get; set; } = true;

    /// <summary>Public url of the UI when running behind a reverse proxy. Used for links and OAuth callbacks.</summary>
    public string? PublicBaseUrl { get; set; }
}
