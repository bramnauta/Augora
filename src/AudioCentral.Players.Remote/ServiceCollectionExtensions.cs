using AudioCentral.Core.Players;
using Microsoft.Extensions.DependencyInjection;

namespace AudioCentral.Players.Remote;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers players that connect over the events hub. The host must register an <see cref="IRemotePlayerChannel"/>.
    /// </summary>
    public static IServiceCollection AddRemotePlayers(this IServiceCollection services)
    {
        services.AddSingleton<RemotePlayerProvider>();
        services.AddSingleton<IPlayerProvider>(sp => sp.GetRequiredService<RemotePlayerProvider>());
        return services;
    }
}
