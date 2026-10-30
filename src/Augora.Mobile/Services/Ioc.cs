namespace Augora.Mobile.Services;

/// <summary>Where views, created by XAML or by hand, find the app's services.</summary>
internal static class Ioc
{
    public static IServiceProvider Services { get; set; } = null!;

    public static T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();
}
