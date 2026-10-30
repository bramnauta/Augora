using Augora.Core.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Augora.Providers.LocalFiles;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalFiles(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(LocalFilesOptions.SectionName);
        services.Configure<LocalFilesOptions>(section);
        var options = section.Get<LocalFilesOptions>() ?? new LocalFilesOptions();
        // Folders can be added in the UI later, so the provider is registered without any (it hides itself then).
        if (!options.Enabled)
        {
            return services;
        }

        services.AddSingleton<LocalLibraryScanner>();
        services.AddHostedService(sp => sp.GetRequiredService<LocalLibraryScanner>());
        services.AddSingleton(sp => new LocalFilesProvider(sp.GetRequiredService<LocalLibraryScanner>()));
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<LocalFilesProvider>());
        return services;
    }
}
