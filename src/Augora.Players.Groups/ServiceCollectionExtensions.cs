using Augora.Core.Players;
using Microsoft.Extensions.DependencyInjection;

namespace Augora.Players.Groups;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers sync groups. The host must register an <see cref="ISyncGroupStore"/>.</summary>
    public static IServiceCollection AddSyncGroups(this IServiceCollection services)
    {
        services.AddSingleton<GroupPlayerProvider>();
        services.AddSingleton<IPlayerProvider>(sp => sp.GetRequiredService<GroupPlayerProvider>());
        services.AddHostedService(sp => sp.GetRequiredService<GroupPlayerProvider>());
        return services;
    }
}
