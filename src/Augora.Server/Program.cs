using Augora.Contracts;
using Augora.Core;
using Augora.Core.Events;
using Augora.Core.Providers;
using Augora.Core.Queue;
using Augora.Core.Streaming;
using Augora.Players.GoogleCast;
using Augora.Players.Groups;
using Augora.Players.Remote;
using Augora.Providers.LocalFiles;
using Augora.Providers.Podcasts;
using Augora.Providers.Spotify;
using Augora.Providers.TuneIn;
using Augora.Server;
using Augora.Server.Auth;
using Augora.Server.Data;
using Augora.Server.Endpoints;
using Augora.Server.Realtime;
using Augora.Server.Streaming;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var options = builder.Configuration.GetSection(AugoraOptions.SectionName).Get<AugoraOptions>() ?? new AugoraOptions();
var dataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);

builder.Services.AddDbContextFactory<AugoraDbContext>(db =>
    db.UseSqlite($"Data Source={Path.Combine(dataDirectory, "augora.db")}"));

builder.AddAugoraAuth(dataDirectory);

builder.Services
    .AddAugoraCore(builder.Configuration)
    .AddTuneIn(builder.Configuration)
    .AddSpotify(builder.Configuration)
    .AddLocalFiles(builder.Configuration)
    .AddPodcasts(builder.Configuration)
    .AddGoogleCast(builder.Configuration)
    .AddRemotePlayers()
    .AddSyncGroups();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<IRuntimeSettingsStore, EfRuntimeSettingsStore>();
builder.Services.AddSingleton<ISpotifyAccountStore, EfSpotifyAccountStore>();
builder.Services.AddSingleton<IPodcastStore, EfPodcastStore>();
builder.Services.AddScoped<ISyncGroupStore, EfSyncGroupStore>();

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
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AugoraDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<RuntimeSettings>().LoadAsync(CancellationToken.None);
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

// Serves the Blazor WebAssembly app (including _framework) from the referenced Augora.Web project.
// The app itself is public; it asks the API who is logged in.
app.MapStaticAssets().AllowAnonymous();

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapLibraryEndpoints();
app.MapPlayerEndpoints();
app.MapGoogleCastEndpoints();
app.MapLibrarySourceEndpoints();
app.MapGroupEndpoints();
if (app.Services.GetService<SpotifyProvider>() is not null)
{
    app.MapSpotifyEndpoints();
}
else
{
    app.MapGet("/api/spotify", () => new SpotifyStatusDto(false, false, "", [])).WithTags("Spotify");
}

app.MapStreamEndpoints();
app.MapHub<AugoraHub>(EventsHub.Path);

// index.html must never be cached: it decides which version of the app the browser loads after an update.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache, no-store",
}).AllowAnonymous();

await app.RunAsync();

/// <summary>Entry point, public for integration tests.</summary>
public partial class Program;
