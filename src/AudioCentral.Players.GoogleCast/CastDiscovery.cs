using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sharpcaster;

namespace AudioCentral.Players.GoogleCast;

/// <summary>One mDNS search for Cast devices and groups.</summary>
public static class CastDiscovery
{
    public static async Task<IReadOnlyList<CastDeviceOptions>> FindAsync(ILoggerFactory? loggerFactory = null)
    {
        using var locator = new ChromecastLocator((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ChromecastLocator>());
        var receivers = await locator.FindReceiversAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
        return receivers
            .Select(CastDevice.FromDiscovery)
            .OfType<CastDevice>()
            .DistinctBy(d => d.Id)
            .Select(d => d.ToOptions())
            .ToList();
    }
}
