using Android.App;
using Android.Content;
using Android.Content.PM;
using Augora.Contracts;

namespace Augora.Mobile;

/// <summary>Receives <c>augora://auth?code=...</c>, the end of an OIDC login in the system browser.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = AppAuth.RedirectScheme, DataHost = "auth")]
public sealed class WebAuthenticationCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
