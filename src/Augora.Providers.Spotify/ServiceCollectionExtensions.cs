using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Providers.Spotify.Librespot;
using Augora.Providers.Spotify.Soloist;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Augora.Providers.Spotify;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Spotify. The host must register an <see cref="ISpotifyAccountStore"/>.</summary>
    public static IServiceCollection AddSpotify(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SpotifyOptions.SectionName);
        services.Configure<SpotifyOptions>(section);
        if (!section.GetValue(nameof(SpotifyOptions.Enabled), defaultValue: true))
        {
            return services;
        }

        services.AddHttpClient(SpotifyAccountService.AccountsClientName, client => client.BaseAddress = new Uri("https://accounts.spotify.com/"));
        services.AddHttpClient(SpotifyAccountService.ApiClientName, client => client.BaseAddress = new Uri("https://api.spotify.com/v1/"));

        services.AddSingleton<SpotifyAccountService>();
        services.AddSingleton(sp => new SpotifyAccountPool(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<SpotifyOptions>>().Value.AccountHoldTime));
        services.AddSingleton<LibrespotManager>();

        // Spotify Soloist is downloaded from Spotify on first use (only after the user chose it for an account).
        services.AddHttpClient(SoloistManager.DownloadClientName, client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<SoloistManager>();

        services.AddSingleton(sp => new SpotifyProvider(
            sp.GetRequiredService<SpotifyAccountService>(),
            sp.GetRequiredService<SpotifyAccountPool>(),
            sp.GetRequiredService<LibrespotManager>(),
            sp.GetRequiredService<SoloistManager>(),
            sp.GetRequiredService<PlayerManager>(),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<SpotifyProvider>>()));
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<SpotifyProvider>());

        return services;
    }
}
