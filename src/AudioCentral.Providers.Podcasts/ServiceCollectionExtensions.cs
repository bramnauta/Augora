using AudioCentral.Core.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AudioCentral.Providers.Podcasts;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers podcasts. The host must register an <see cref="IPodcastStore"/>.</summary>
    public static IServiceCollection AddPodcasts(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue("Providers:Podcasts:Enabled", defaultValue: true))
        {
            return services;
        }

        services.AddHttpClient(PodcastProvider.FeedClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioCentral/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient(PodcastProvider.DirectoryClientName, client => client.BaseAddress = new Uri("https://itunes.apple.com/"));
        services.AddSingleton<PodcastProvider>();
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<PodcastProvider>());
        return services;
    }
}
