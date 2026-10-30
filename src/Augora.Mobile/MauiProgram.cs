using Microsoft.Maui.Handlers;

namespace Augora.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        WebViewHandler.Mapper.AppendToMapping("Augora", (handler, _) => PlatformWeb.ConfigureHandler(handler));

        return builder.Build();
    }
}
