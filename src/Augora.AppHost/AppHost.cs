using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Augora.Players.GoogleCast;

var builder = DistributedApplication.CreateBuilder(args);

if (bool.TryParse(builder.Configuration["Augora:Container"], out var inContainer) && inContainer)
{
    // The production image, with go-librespot and ffmpeg: needed for Spotify playback, which is Linux-only.
    // Speakers stream from port 4727 on this machine's LAN address; allow it in the firewall.
    const int port = 4727;
    var streamBaseUrl = $"http://{LanAddress()}:{port}";
    var server = builder.AddDockerfile("server", "../..");

    // Docker Desktop runs containers in a VM that mDNS does not reach. Search here, on the host, and hand the
    // speakers and groups to the container. Restart the AppHost to pick up new devices or changed group ports.
    // A single search regularly misses a few devices, so search several times and merge the results.
    var found = new Dictionary<string, CastDeviceOptions>();
    for (var round = 1; round <= 3; round++)
    {
        foreach (var device in await CastDiscovery.FindAsync())
        {
            found[device.Id] = device;
        }
        Console.WriteLine($"Cast search {round}/3: {found.Count} devices so far");
    }
    var devices = found.Values.ToList();
    Console.WriteLine($"Found {devices.Count} Cast devices for the container");
    server.WithEnvironment("Players__GoogleCast__Discovery", "false");
    // dotnet user-secrets set Players:GoogleCast:ReceiverAppId <id> --project src/Augora.AppHost
    if (builder.Configuration["Players:GoogleCast:ReceiverAppId"] is { Length: > 0 } receiverAppId)
    {
        server.WithEnvironment("Players__GoogleCast__ReceiverAppId", receiverAppId);
    }

    // The Spotify engines are where playback problems show up; their events and process output are debug logs.
    server.WithEnvironment("Logging__LogLevel__Augora.Providers.Spotify", "Debug");
    for (var i = 0; i < devices.Count; i++)
    {
        var device = devices[i];
        var prefix = $"Players__GoogleCast__Devices__{i}__";
        server
            .WithEnvironment(prefix + nameof(device.Id), device.Id)
            .WithEnvironment(prefix + nameof(device.Name), device.Name)
            .WithEnvironment(prefix + nameof(device.Host), device.Host)
            .WithEnvironment(prefix + nameof(device.Port), device.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment(prefix + nameof(device.Kind), device.Kind.ToString())
            .WithEnvironment(prefix + nameof(device.Model), device.Model ?? "");
    }

    server
        .WithHttpEndpoint(port: port, targetPort: port, name: "http", isProxied: false)
        // Aspire publishes container ports on 127.0.0.1 by default; the speakers come in over the LAN.
        .WithEndpoint("http", endpoint => endpoint.TargetHost = "0.0.0.0")
        .WithEnvironment("Augora__StreamBaseUrl", streamBaseUrl)
        .WithVolume("augora-dev-data", "/data")
        .WithExternalHttpEndpoints()
        // Spotify only accepts http redirect uris on 127.0.0.1, so open the UI there when linking accounts.
        .WithUrlForEndpoint("http", url =>
        {
            url.Url = $"http://127.0.0.1:{port}";
            url.DisplayText = "Augora";
        });
}
else
{
    // Cast devices fetch audio from the server over the LAN, so the stream endpoint must listen on all
    // interfaces with a fixed port, outside of Aspire's localhost proxy. Allow port 8097 in the firewall.
    const int streamPort = 8097;

    builder.AddProject<Projects.Augora_Server>("server")
        .WithHttpEndpoint(port: streamPort, name: "stream", isProxied: false)
        .WithEndpoint("stream", endpoint => endpoint.TargetHost = "0.0.0.0")
        .WithEnvironment("Augora__StreamPort", streamPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .WithExternalHttpEndpoints()
        .WithUrlForEndpoint("https", url => url.DisplayText = "Augora")
        .WithUrlForEndpoint("stream", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly);
}

builder.Build().Run();

// The IPv4 address of the interface with the default gateway, as the server itself detects it outside a container.
static IPAddress LanAddress() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => n.GetIPProperties())
        .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
        .SelectMany(p => p.UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
    ?? IPAddress.Loopback;
