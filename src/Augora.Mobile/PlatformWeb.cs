using Microsoft.Maui.Handlers;

namespace Augora.Mobile;

/// <summary>What the WebView needs per platform: its settings and the login cookie of the web UI.</summary>
internal static partial class PlatformWeb
{
    /// <summary>Lets the web UI see that it runs in the app: the native player does the playing, not the browser.</summary>
    internal static partial void ConfigureHandler(IWebViewHandler handler);

    /// <summary>The cookies the WebView has for <paramref name="url"/>, as a <c>Cookie</c> header; <c>null</c> when there are none.</summary>
    internal static partial Task<string?> GetCookieHeaderAsync(string url);
}
