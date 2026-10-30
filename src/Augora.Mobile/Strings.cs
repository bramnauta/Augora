using System.Globalization;

namespace Augora.Mobile;

/// <summary>The few texts of the setup screen, in Dutch and English.</summary>
internal static class Strings
{
    private static bool Dutch => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";

    public static string SetupTitle => Dutch ? "Verbinden met Augora" : "Connect to Augora";

    public static string SetupHint => Dutch ? "Adres van de server, bijv. http://192.168.1.10:4727" : "Server address, e.g. http://192.168.1.10:4727";

    public static string Connect => Dutch ? "Verbinden" : "Connect";

    public static string SetupFailed(string reason) => Dutch ? $"Kan de server niet bereiken: {reason}" : $"Cannot reach the server: {reason}";

    public static string PageFailed => Dutch ? "De server is niet bereikbaar. Controleer het adres." : "The server is unreachable. Check the address.";
}
