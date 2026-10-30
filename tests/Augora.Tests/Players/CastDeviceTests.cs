using Augora.Contracts;
using Augora.Players.GoogleCast;
using Sharpcaster.Models;

namespace Augora.Tests.Players;

public sealed class CastDeviceTests
{
    private static ChromecastReceiver Receiver(string model, string capabilities, int port = 8009) => new()
    {
        Name = "fallback",
        DeviceUri = new Uri("http://192.168.20.15"),
        Port = port,
        ExtraInformation = new Dictionary<string, string>
        {
            ["id"] = "8F3C1E2A-0000-4000-8000-0123456789AB",
            ["md"] = model,
            ["fn"] = "Woonkamer",
            ["ca"] = capabilities,
        },
    };

    [Fact]
    public void Uses_the_cast_id_and_friendly_name()
    {
        var device = CastDevice.FromDiscovery(Receiver("Google Nest Mini", "199172"))!;

        Assert.Equal("cast:8f3c1e2a0000400080000123456789ab", device.Id);
        Assert.Equal("Woonkamer", device.Name);
        Assert.Equal("192.168.20.15", device.Host);
        Assert.Equal(PlayerKind.Speaker, device.Kind);
    }

    [Fact]
    public void Recognises_groups_and_tvs()
    {
        Assert.Equal(PlayerKind.DeviceGroup, CastDevice.FromDiscovery(Receiver("Google Cast Group", "199172", 32187))!.Kind);
        Assert.Equal(PlayerKind.Tv, CastDevice.FromDiscovery(Receiver("Chromecast", "463365"))!.Kind);
    }
}
