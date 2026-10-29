using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AudioCentral.Core;
using AudioCentral.Core.Streaming;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace AudioCentral.Server.Streaming;

/// <summary>
/// Determines the address players use to reach the stream endpoint: the configured
/// <see cref="AudioCentralOptions.StreamBaseUrl"/>, or the LAN address of this host.
/// </summary>
internal sealed class StreamUrlProvider(IOptions<AudioCentralOptions> options, IServer server, ILogger<StreamUrlProvider> logger) : IStreamUrlProvider
{
    private readonly Lazy<Uri> _baseUrl = new(() => DetermineBaseUrl(options.Value, server, logger));

    public Uri GetStreamUrl(StreamSession session) => new(_baseUrl.Value, session.RelativePath.TrimStart('/'));

    private static Uri DetermineBaseUrl(AudioCentralOptions options, IServer server, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.StreamBaseUrl))
        {
            return new Uri(options.StreamBaseUrl.TrimEnd('/') + "/");
        }

        var port = options.StreamPort ?? GetHttpPort(server);
        var address = GetLanAddress();
        var url = new Uri($"http://{address}:{port}/");
        logger.LogInformation("Players will stream from {Url}. Set AudioCentral:StreamBaseUrl to override", url);
        return url;
    }

    private static int GetHttpPort(IServer server)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            // Kestrel reports wildcard bindings as http://[::]:4727 or http://+:4727, which Uri cannot always parse.
            var normalised = address.Replace("://+", "://0.0.0.0", StringComparison.Ordinal).Replace("://*", "://0.0.0.0", StringComparison.Ordinal);
            if (Uri.TryCreate(normalised, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                return uri.Port;
            }
        }

        return 4727;
    }

    /// <summary>The IPv4 address of the interface that has the default gateway.</summary>
    private static IPAddress GetLanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

        return candidates.FirstOrDefault() ?? IPAddress.Loopback;
    }
}
