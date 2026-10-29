using AudioCentral.Contracts;
using AudioCentral.Players.GoogleCast;
using Microsoft.Extensions.Configuration;

namespace AudioCentral.Tests.Players;

public sealed class CastDeviceOptionsTests
{
    [Fact]
    public void Devices_from_configuration_get_the_same_player_id_as_when_discovered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Players:GoogleCast:Devices:0:Id"] = "3346589d-1b32-43af-9e96-a936216c639e",
                ["Players:GoogleCast:Devices:0:Name"] = "Begane Grond",
                ["Players:GoogleCast:Devices:0:Host"] = "10.116.26.10",
                ["Players:GoogleCast:Devices:0:Port"] = "32193",
                ["Players:GoogleCast:Devices:0:Kind"] = "DeviceGroup",
                ["Players:GoogleCast:Devices:0:Model"] = "Google Cast Group",
            })
            .Build();

        var options = configuration.GetSection(GoogleCastOptions.SectionName).Get<GoogleCastOptions>()!;
        var device = CastDevice.FromOptions(Assert.Single(options.Devices));

        Assert.Equal("cast:3346589d1b3243af9e96a936216c639e", device.Id);
        Assert.Equal(("10.116.26.10", 32193, PlayerKind.DeviceGroup), (device.Host, device.Port, device.Kind));
        Assert.Equal(device, CastDevice.FromOptions(device.ToOptions()));
    }
}
