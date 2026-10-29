using AudioCentral.Core.Players;
using AudioCentral.Core.Providers;
using AudioCentral.Providers.Spotify.Librespot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioCentral.Providers.Spotify;

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
        services.AddSingleton(sp => new SpotifyProvider(
            sp.GetRequiredService<SpotifyAccountService>(),
            sp.GetRequiredService<SpotifyAccountPool>(),
            sp.GetRequiredService<LibrespotManager>(),
            sp.GetRequiredService<PlayerManager>(),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<SpotifyProvider>>()));
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<SpotifyProvider>());

        return services;
    }
}
