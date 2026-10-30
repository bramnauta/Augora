using Augora.Core.Players;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Augora.Players.GoogleCast;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Google Cast support. The host must register an <see cref="ICastDeviceStore"/>.
    /// </summary>
    public static IServiceCollection AddGoogleCast(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(GoogleCastOptions.SectionName);
        services.Configure<GoogleCastOptions>(section);
        if (!section.GetValue(nameof(GoogleCastOptions.Enabled), defaultValue: true))
        {
            return services;
        }

        services.AddSingleton<CastPlayerProvider>();
        services.AddSingleton<IPlayerProvider>(sp => sp.GetRequiredService<CastPlayerProvider>());
        services.AddHostedService(sp => sp.GetRequiredService<CastPlayerProvider>());

        return services;
    }
}
