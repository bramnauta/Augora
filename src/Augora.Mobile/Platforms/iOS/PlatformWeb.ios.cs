using Foundation;
using Microsoft.Maui.Handlers;
using WebKit;

namespace Augora.Mobile;

internal static partial class PlatformWeb
{
    private const string UserAgentMarker = "AugoraIos/1.0";
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1 " + UserAgentMarker;

    internal static partial void ConfigureHandler(IWebViewHandler handler)
    {
        handler.PlatformView.CustomUserAgent = UserAgent;
    }

    internal static partial async Task<string?> GetCookieHeaderAsync(string url)
    {
        var host = new Uri(url).Host;
        var cookies = await WKWebsiteDataStore.DefaultDataStore.HttpCookieStore.GetAllCookiesAsync();
        var matching = cookies
            .Where(c => host.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Name}={c.Value}")
            .ToArray();
        return matching.Length == 0 ? null : string.Join("; ", matching);
    }
}
