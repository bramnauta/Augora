using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Augora.Core;
using Augora.Core.Streaming;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Augora.Server.Streaming;

/// <summary>
/// Determines the address players use to reach the stream endpoint: the configured
/// <see cref="AugoraOptions.StreamBaseUrl"/>, or the LAN address of this host.
/// </summary>
internal sealed class StreamUrlProvider(IOptions<AugoraOptions> options, IServer server, ILogger<StreamUrlProvider> logger) : IStreamUrlProvider
{
    private readonly Lazy<Uri> _baseUrl = new(() => DetermineBaseUrl(options.Value, server, logger));

    public Uri GetStreamUrl(StreamSession session) => GetAbsoluteUrl(session.RelativePath);

    public Uri GetAbsoluteUrl(string relativePath) => new(_baseUrl.Value, relativePath.TrimStart('/'));

    private static Uri DetermineBaseUrl(AugoraOptions options, IServer server, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.StreamBaseUrl))
        {
            return new Uri(options.StreamBaseUrl.TrimEnd('/') + "/");
        }

        var port = options.StreamPort ?? GetHttpPort(server);
        var address = GetLanAddress();
        var url = new Uri($"http://{address}:{port}/");
        if (IsDockerBridgeAddress(address))
        {
            // In a bridge network the container only sees its private address; speakers cannot reach it.
            logger.LogWarning(
                "Players will stream from {Url}, a Docker bridge address that speakers usually cannot reach. "
                + "Set Augora:StreamBaseUrl to the host's LAN address, or use a macvlan network",
                url);
        }
        else
        {
            logger.LogInformation("Players will stream from {Url}. Set Augora:StreamBaseUrl to override", url);
        }

        return url;
    }

    internal static int GetHttpPort(IServer server)
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

    /// <summary>Running in a container on an address in 172.16.0.0/12, Docker's default bridge range.</summary>
    internal static bool IsDockerBridgeAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            && bytes.Length == 4 && bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
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
