var builder = DistributedApplication.CreateBuilder(args);

// Cast devices fetch audio from the server over the LAN, so the stream endpoint must listen on all
// interfaces with a fixed port, outside of Aspire's localhost proxy. Allow port 8097 in the firewall.
const int streamPort = 8097;

builder.AddProject<Projects.AudioCentral_Server>("server")
    .WithHttpEndpoint(port: streamPort, name: "stream", isProxied: false)
    .WithEndpoint("stream", endpoint => endpoint.TargetHost = "0.0.0.0")
    .WithEnvironment("AudioCentral__StreamPort", streamPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
    .WithExternalHttpEndpoints()
    .WithUrlForEndpoint("https", url => url.DisplayText = "AudioCentral")
    .WithUrlForEndpoint("stream", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly);

builder.Build().Run();
