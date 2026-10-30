using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Augora.Core;
using Augora.Server.Streaming;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;

namespace Augora.Server.Discovery;

/// <summary>
/// Announces the server on the local network as <c>_augora._tcp</c>, so Home Assistant offers to set up the
/// integration. Works in a container with its own address (macvlan) or on the host network, not in a bridge network.
/// </summary>
internal sealed partial class MdnsAdvertiser(
    IOptions<AugoraOptions> options,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<MdnsAdvertiser> logger) : BackgroundService
{
    private const int Port = 5353;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint GroupEndpoint = new(Group, Port);

    private static readonly TimeSpan InterfaceRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumResponseInterval = TimeSpan.FromSeconds(1);

    private readonly object _sendLock = new();
    private readonly Dictionary<int, DateTime> _lastResponse = [];
    private volatile IReadOnlyDictionary<int, IPAddress> _interfaces = new Dictionary<int, IPAddress>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AdvertiseMdns)
        {
            return;
        }

        try
        {
            // The listening addresses are known once the server has started.
            var started = new TaskCompletionSource();
            await using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);

            var service = CreateService(StreamUrlProvider.GetHttpPort(server));
            using var socket = OpenSocket();
            var joined = new HashSet<IPAddress>();
            RefreshInterfaces(socket, joined);

            logger.LogInformation("Announcing {Instance} over mDNS on port {Port}", service.InstanceName, service.Port);
            try
            {
                await Task.WhenAll(AnnounceAsync(socket, service, stoppingToken), RefreshLoopAsync(socket, joined, service, stoppingToken), ReceiveLoopAsync(socket, service, stoppingToken));
            }
            finally
            {
                // Tell listeners the service is gone instead of leaving it in their caches.
                SendToAll(socket, service, goodbye: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Discovery is a convenience: whatever goes wrong must not stop the server.
            logger.LogWarning(ex, "mDNS announcement is off: {Message}. Home Assistant can still be set up with the server address", ex.Message);
        }
    }

    private static MdnsService CreateService(int port)
    {
        var machine = Environment.MachineName;
        var host = "augora-" + NonHostnameCharacters().Replace(machine.ToLowerInvariant(), "-").Trim('-');
        var version = typeof(MdnsAdvertiser).Assembly.GetName().Version?.ToString(3) ?? "0";
        return new MdnsService($"Augora on {machine}", $"{host}.local", port, [$"version={version}", "path=/"]);
    }

    private static Socket OpenSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Other mDNS software on the host (avahi, Home Assistant) listens on the same port.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Joins the multicast group on every usable interface; returns whether the set of interfaces changed.</summary>
    private bool RefreshInterfaces(Socket socket, HashSet<IPAddress> joined)
    {
        var interfaces = new Dictionary<int, IPAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || !network.SupportsMulticast || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var properties = network.GetIPProperties();
            int? index;
            try
            {
                index = properties.GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
                continue; // the interface has no IPv4 configuration
            }

            var address = properties.UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (index is null || address is null)
            {
                continue;
            }

            interfaces[index.Value] = address;
            if (!joined.Contains(address))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address));
                    joined.Add(address);
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "Cannot join the mDNS group on {Interface} ({Address})", network.Name, address);
                    interfaces.Remove(index.Value);
                }
            }
        }

        var changed = !_interfaces.OrderBy(i => i.Key).SequenceEqual(interfaces.OrderBy(i => i.Key));
        _interfaces = interfaces;
        return changed;
    }

    private async Task AnnounceAsync(Socket socket, MdnsService service, CancellationToken cancellationToken)
    {
        // Two announcements, a second apart, in case the first is lost.
        SendToAll(socket, service, goodbye: false);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        SendToAll(socket, service, goodbye: false);
    }

    private async Task RefreshLoopAsync(Socket socket, HashSet<IPAddress> joined, MdnsService service, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(InterfaceRefresh);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (RefreshInterfaces(socket, joined))
            {
                SendToAll(socket, service, goodbye: false);
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, MdnsService service, CancellationToken cancellationToken)
    {
        var buffer = new byte[9000];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult received;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                received = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, cancellationToken);
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Receiving mDNS failed");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            if (!MdnsPacket.ReadQuestionNames(buffer.AsSpan(0, received.ReceivedBytes)).Any(service.Matches))
            {
                continue;
            }

            var index = received.PacketInformation.Interface;
            if (!_interfaces.TryGetValue(index, out var address))
            {
                continue;
            }

            lock (_sendLock)
            {
                // Questions arrive in bursts from several listeners; one answer per second per interface is enough.
                var now = DateTime.UtcNow;
                if (_lastResponse.TryGetValue(index, out var last) && now - last < MinimumResponseInterval)
                {
                    continue;
                }

                _lastResponse[index] = now;
            }

            Send(socket, address, MdnsPacket.BuildResponse(service, address, goodbye: false));
        }
    }

    private void SendToAll(Socket socket, MdnsService service, bool goodbye)
    {
        foreach (var address in _interfaces.Values)
        {
            Send(socket, address, MdnsPacket.BuildResponse(service, address, goodbye));
        }
    }

    private void Send(Socket socket, IPAddress interfaceAddress, byte[] packet)
    {
        try
        {
            lock (_sendLock)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());
                socket.SendTo(packet, GroupEndpoint);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Sending mDNS on {Address} failed", interfaceAddress);
        }
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonHostnameCharacters();
}
