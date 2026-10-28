using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AudioCentral.Web.Services;

/// <summary>
/// UI language: Dutch by default, English optional. The choice is stored in the browser.
/// </summary>
public sealed class CultureService(IJSRuntime js, NavigationManager navigation)
{
    private const string StorageKey = "audiocentral.culture";

    public static IReadOnlyList<(string Code, string Name)> Supported { get; } = [("nl", "Nederlands"), ("en", "English")];

    public static string Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public static async Task ApplyStoredCultureAsync(IJSRuntime js)
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        var code = Supported.Any(s => s.Code == stored) ? stored! : "nl";
        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public async Task SetAsync(string code)
    {
        if (code == Current)
        {
            return;
        }

        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, code);
        navigation.Refresh(forceReload: true);
    }
}
