using Android.Webkit;
using Microsoft.Maui.Handlers;

namespace Augora.Mobile;

internal static partial class PlatformWeb
{
    private const string UserAgentMarker = "AugoraAndroid/1.0";

    internal static partial void ConfigureHandler(IWebViewHandler handler)
    {
        var web = handler.PlatformView.Settings!;
        web.JavaScriptEnabled = true;
        web.DomStorageEnabled = true;
        web.MediaPlaybackRequiresUserGesture = false;
        if (web.UserAgentString?.Contains(UserAgentMarker, StringComparison.Ordinal) != true)
        {
            web.UserAgentString = $"{web.UserAgentString} {UserAgentMarker}";
        }

        CookieManager.Instance!.SetAcceptCookie(true);
    }

    internal static partial Task<string?> GetCookieHeaderAsync(string url) => Task.FromResult(CookieManager.Instance!.GetCookie(url));
}
