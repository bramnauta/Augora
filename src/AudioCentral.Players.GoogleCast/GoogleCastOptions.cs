namespace AudioCentral.Players.GoogleCast;

public sealed class GoogleCastOptions
{
    public const string SectionName = "Players:GoogleCast";

    public bool Enabled { get; set; } = true;

    /// <summary>mDNS discovery; turn off when the speakers are only reachable as manual devices.</summary>
    public bool Discovery { get; set; } = true;

    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(15);
}
