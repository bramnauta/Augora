using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Events;
using AudioCentral.Core.Queue;
using AudioCentral.Core.Streaming;
using AudioCentral.Players.GoogleCast;
using AudioCentral.Providers.TuneIn;
using AudioCentral.Server;
using AudioCentral.Server.Data;
using AudioCentral.Server.Endpoints;
using AudioCentral.Server.Realtime;
using AudioCentral.Server.Streaming;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var options = builder.Configuration.GetSection(AudioCentralOptions.SectionName).Get<AudioCentralOptions>() ?? new AudioCentralOptions();
var dataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);

builder.Services.AddDbContextFactory<AudioCentralDbContext>(db =>
    db.UseSqlite($"Data Source={Path.Combine(dataDirectory, "audiocentral.db")}"));

builder.Services
    .AddAudioCentralCore(builder.Configuration)
    .AddTuneIn(builder.Configuration)
    .AddGoogleCast(builder.Configuration);

builder.Services.AddSingleton<IStreamUrlProvider, StreamUrlProvider>();
builder.Services.AddSingleton<IEventPublisher, SignalREventPublisher>();
builder.Services.AddSingleton<IQueueStore, EfQueueStore>();
builder.Services.AddScoped<ICastDeviceStore, EfCastDeviceStore>();

builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

// Behind a reverse proxy (Traefik, Nginx, Home Assistant ingress) the original scheme and host come from these headers.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AudioCentralDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Serves the Blazor WebAssembly app (including _framework) from the referenced AudioCentral.Web project.
app.MapStaticAssets();

app.MapDefaultEndpoints();
app.MapLibraryEndpoints();
app.MapPlayerEndpoints();
app.MapGoogleCastEndpoints();
app.MapStreamEndpoints();
app.MapHub<AudioCentralHub>(EventsHub.Path);

app.MapFallbackToFile("index.html");

await app.RunAsync();
