using AudioCentral.Contracts;
using Sharpcaster.Models;

namespace AudioCentral.Players.GoogleCast;

/// <summary>
/// What we know about a Cast device before connecting to it.
/// </summary>
internal sealed record CastDevice(string Id, string Name, string Host, int Port, PlayerKind Kind, string? Model)
{
    private const string GroupModel = "Google Cast Group";

    // Bit 0 of the "ca" TXT record: the device has video output.
    private const int VideoOutCapability = 1;

    public static CastDevice? FromDiscovery(ChromecastReceiver receiver)
    {
        var txt = receiver.ExtraInformation ?? new Dictionary<string, string>();
        if (!txt.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id) || receiver.DeviceUri is null)
        {
            return null;
        }

        txt.TryGetValue("md", out var model);
        var kind = model == GroupModel
            ? PlayerKind.DeviceGroup
            : txt.TryGetValue("ca", out var ca) && int.TryParse(ca, out var capabilities) && (capabilities & VideoOutCapability) != 0
                ? PlayerKind.Tv
                : PlayerKind.Speaker;

        var name = txt.TryGetValue("fn", out var friendlyName) && !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName : receiver.Name;
        return new CastDevice($"cast:{id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()}", name, receiver.DeviceUri.Host, receiver.Port, kind, model);
    }

    public static CastDevice FromManual(ManualCastDeviceDto device) =>
        new($"cast:manual-{device.Id:N}", device.Name, device.Host, device.Port, device.Port == 8009 ? PlayerKind.Speaker : PlayerKind.DeviceGroup, null);

    public ChromecastReceiver ToReceiver() => new()
    {
        Name = Name,
        DeviceUri = new UriBuilder("https", Host).Uri,
        Port = Port,
        Model = Model ?? "",
    };
}
