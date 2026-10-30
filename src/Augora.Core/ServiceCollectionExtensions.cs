using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Core.Queue;
using Augora.Core.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Augora.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAugoraCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AugoraOptions>(configuration.GetSection(AugoraOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        services.TryAddSingleton<RuntimeSettings>();
        services.TryAddSingleton<IRuntimeSettings>(sp => sp.GetRequiredService<RuntimeSettings>());
        services.TryAddSingleton<ICurrentUser, NoCurrentUser>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<PlayerManager>();
        services.AddSingleton<GroupMembership>();
        services.AddHostedService(sp => sp.GetRequiredService<PlayerManager>());
        services.AddSingleton<QueueManager>();

        services.AddSingleton<StreamSessionManager>();
        services.AddSingleton<StreamRelay>();
        services.AddSingleton(sp => new StreamResolver(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(StreamResolver.HttpClientName),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StreamResolver>>()));

        // Streams are long-lived: no timeout and no retries, unlike the resilient default clients.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental, but the only way to opt out of ConfigureHttpClientDefaults
        services.AddHttpClient(StreamResolver.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Augora/1.0");
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        return services;
    }
}
