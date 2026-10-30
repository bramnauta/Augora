using Augora.Core.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Augora.Providers.TuneIn;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTuneIn(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TuneInOptions.SectionName);
        services.Configure<TuneInOptions>(section);
        if (!section.GetValue(nameof(TuneInOptions.Enabled), defaultValue: true))
        {
            return services;
        }

        services.AddHttpClient(TuneInProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://opml.radiotime.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Augora/1.0");
        });
        services.AddSingleton<TuneInProvider>();
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<TuneInProvider>());

        return services;
    }
}
