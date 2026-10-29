using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Events;
using AudioCentral.Core.Providers;
using AudioCentral.Core.Queue;
using AudioCentral.Core.Streaming;
using AudioCentral.Players.GoogleCast;
using AudioCentral.Players.Remote;
using AudioCentral.Providers.LocalFiles;
using AudioCentral.Providers.Podcasts;
using AudioCentral.Providers.Spotify;
using AudioCentral.Providers.TuneIn;
using AudioCentral.Server;
using AudioCentral.Server.Auth;
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

builder.AddAudioCentralAuth(dataDirectory);

builder.Services
    .AddAudioCentralCore(builder.Configuration)
    .AddTuneIn(builder.Configuration)
    .AddSpotify(builder.Configuration)
    .AddLocalFiles(builder.Configuration)
    .AddPodcasts(builder.Configuration)
    .AddGoogleCast(builder.Configuration)
    .AddRemotePlayers();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<ISpotifyAccountStore, EfSpotifyAccountStore>();
builder.Services.AddSingleton<IPodcastStore, EfPodcastStore>();

builder.Services.AddSingleton<IStreamUrlProvider, StreamUrlProvider>();
builder.Services.AddSingleton<IEventPublisher, SignalREventPublisher>();
builder.Services.AddSingleton<IRemotePlayerChannel, SignalRRemotePlayerChannel>();
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

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

// Serves the Blazor WebAssembly app (including _framework) from the referenced AudioCentral.Web project.
// The app itself is public; it asks the API who is logged in.
app.MapStaticAssets().AllowAnonymous();

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapLibraryEndpoints();
app.MapPlayerEndpoints();
app.MapGoogleCastEndpoints();
app.MapLibrarySourceEndpoints();
if (app.Services.GetService<SpotifyProvider>() is not null)
{
    app.MapSpotifyEndpoints();
}
else
{
    app.MapGet("/api/spotify", () => new SpotifyStatusDto(false, false, "", [])).WithTags("Spotify");
}

app.MapStreamEndpoints();
app.MapHub<AudioCentralHub>(EventsHub.Path);

// index.html must never be cached: it decides which version of the app the browser loads after an update.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache, no-store",
}).AllowAnonymous();

await app.RunAsync();

/// <summary>Entry point, public for integration tests.</summary>
public partial class Program;
