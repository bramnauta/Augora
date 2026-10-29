using AudioCentral.Contracts;

namespace AudioCentral.Players.GoogleCast;

public sealed class GoogleCastOptions
{
    public const string SectionName = "Players:GoogleCast";

    public bool Enabled { get; set; } = true;

    /// <summary>mDNS discovery; turn off when the speakers are only reachable as manual devices.</summary>
    public bool Discovery { get; set; } = true;

    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The Cast receiver app; devices show its name. The default is Google's Default Media Receiver. Register a
    /// Styled Media Receiver named "AudioCentral" in the Google Cast SDK Developer Console and publish it to show that.
    /// </summary>
    public string ReceiverAppId { get; set; } = "CC1AD845";

    /// <summary>
    /// Devices known from elsewhere, e.g. discovered by the Aspire AppHost for a container that mDNS cannot reach.
    /// They get the same player ids as when discovered, unlike devices added in the UI.
    /// </summary>
    public List<CastDeviceOptions> Devices { get; set; } = [];
}

/// <param name="Id">The Cast device id from mDNS (the <c>id</c> TXT record).</param>
public sealed record CastDeviceOptions(string Id, string Name, string Host, int Port, PlayerKind Kind, string? Model)
{
    public CastDeviceOptions()
        : this("", "", "", 8009, PlayerKind.Speaker, null)
    {
    }
}
