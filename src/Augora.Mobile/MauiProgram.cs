using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<Navigator>();
        builder.Services.AddSingleton<PlayService>();
        builder.Services.AddSingleton<PlayerViewModel>();
        builder.Services.AddSingleton<QueueViewModel>();

        var app = builder.Build();
        Ioc.Services = app.Services;
        return app;
    }
}
