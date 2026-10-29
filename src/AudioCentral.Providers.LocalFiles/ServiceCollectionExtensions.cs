using AudioCentral.Core.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AudioCentral.Providers.LocalFiles;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalFiles(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(LocalFilesOptions.SectionName);
        services.Configure<LocalFilesOptions>(section);
        var options = section.Get<LocalFilesOptions>() ?? new LocalFilesOptions();
        if (!options.Enabled || !options.Folders.Any(f => !string.IsNullOrWhiteSpace(f.Path)))
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
