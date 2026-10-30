using System.Globalization;

namespace Augora.Mobile.Services;

/// <summary>The texts of the app, in Dutch and English.</summary>
internal static class Strings
{
    private static bool Dutch => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";

    private static string T(string nl, string en) => Dutch ? nl : en;

    public static string Home => T("Home", "Home");

    public static string Search => T("Zoeken", "Search");

    public static string Queue => T("Wachtrij", "Queue");

    public static string Sources => T("Je muziek", "Your music");

    public static string SearchHint => T("Artiesten, nummers, radio, podcasts", "Artists, tracks, radio, podcasts");

    public static string NothingPlaying => T("Niets aan het afspelen", "Nothing playing");

    public static string QueueEmpty => T("De wachtrij is leeg", "The queue is empty");

    public static string ClearQueue => T("Wachtrij wissen", "Clear queue");

    public static string NoResults => T("Niets gevonden", "Nothing found");

    public static string Cancel => T("Annuleren", "Cancel");

    public static string Ok => "OK";

    public static string Error => T("Mislukt", "Failed");

    // Login
    public static string ConnectTitle => T("Verbinden met Augora", "Connect to Augora");

    public static string ServerHint => T("Adres van de server, bijv. 192.168.1.10:4727", "Server address, e.g. 192.168.1.10:4727");

    public static string Connect => T("Verbinden", "Connect");

    public static string UserName => T("Gebruikersnaam", "User name");

    public static string Password => T("Wachtwoord", "Password");

    public static string LogIn => T("Inloggen", "Log in");

    public static string LogInWith(string provider) => T($"Inloggen met {provider}", $"Log in with {provider}");

    public static string OtherServer => T("Andere server", "Other server");

    public static string SetupRequired => T("Deze server heeft nog geen account. Maak dat eerst aan in de webinterface.", "This server has no account yet. Create one in the web interface first.");

    public static string CannotReach(string reason) => T($"Kan de server niet bereiken: {reason}", $"Cannot reach the server: {reason}");

    public static string WrongCredentials => T("Gebruikersnaam of wachtwoord klopt niet.", "Wrong user name or password.");

    public static string LoginFailed => T("Inloggen is niet gelukt.", "Logging in failed.");

    // Players
    public static string PlayOn => T("Afspelen op", "Play on");

    public static string ThisDevice(string name) => T($"Dit apparaat ({name})", $"This device ({name})");

    public static string Unavailable => T("Niet beschikbaar", "Unavailable");

    public static string PlayingOn(string player) => T($"Speelt op {player}", $"Playing on {player}");

    public static string AddedTo(string player) => T($"Toegevoegd aan {player}", $"Added to {player}");

    public static string PlayNext => T("Hierna afspelen", "Play next");

    public static string AddToQueue => T("Aan wachtrij toevoegen", "Add to queue");

    public static string PlayNow => T("Nu afspelen", "Play now");

    public static string Open => T("Openen", "Open");

    public static string LogOut => T("Uitloggen", "Log out");

    public static string SpotifyBusy => T("Alle Spotify-accounts zijn bezig. Welke stream mag worden gestopt?", "All Spotify accounts are busy. Which stream may be stopped?");

    public static string SpotifyPairing => T("Spotify moet eenmalig worden gekoppeld. Doe dat in de webinterface onder Spotify.", "Spotify has to be paired once. Do that in the web interface under Spotify.");
}
