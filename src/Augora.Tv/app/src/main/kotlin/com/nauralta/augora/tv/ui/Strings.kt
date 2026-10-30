package com.nauralta.augora.tv.ui

import java.util.Locale

/** The texts of the app, in Dutch and English. */
object S {
    private val dutch get() = Locale.getDefault().language == "nl"

    private fun t(nl: String, en: String) = if (dutch) nl else en

    val home get() = t("Home", "Home")
    val search get() = t("Zoeken", "Search")
    val queue get() = t("Wachtrij", "Queue")
    val sources get() = t("Je muziek", "Your music")
    val searchHint get() = t("Artiesten, nummers, radio, podcasts", "Artists, tracks, radio, podcasts")
    val nothingPlaying get() = t("Niets aan het afspelen", "Nothing playing")
    val queueEmpty get() = t("De wachtrij is leeg", "The queue is empty")
    val clearQueue get() = t("Wachtrij wissen", "Clear queue")
    val noResults get() = t("Niets gevonden", "Nothing found")
    val loading get() = t("Laden…", "Loading…")
    val retry get() = t("Opnieuw proberen", "Try again")
    val close get() = t("Sluiten", "Close")

    val connectTitle get() = t("Verbinden met Augora", "Connect to Augora")
    val serverHint get() = t("Server, bijv. 192.168.1.10:4727", "Server, e.g. 192.168.1.10:4727")
    val userName get() = t("Gebruikersnaam", "User name")
    val password get() = t("Wachtwoord", "Password")
    val logIn get() = t("Inloggen", "Log in")
    val logOut get() = t("Uitloggen", "Log out")
    val setupRequired get() = t(
        "Deze server heeft nog geen account. Maak dat eerst aan in de webinterface.",
        "This server has no account yet. Create one in the web interface first.",
    )
    val wrongCredentials get() = t("Gebruikersnaam of wachtwoord klopt niet.", "Wrong user name or password.")
    fun cannotReach(reason: String) = t("Kan de server niet bereiken: $reason", "Cannot reach the server: $reason")
    val oidcHint get() = t(
        "Inloggen met een externe dienst kan nog niet op de TV; gebruik een account met een wachtwoord.",
        "Logging in with an external provider is not possible on the TV yet; use an account with a password.",
    )

    val playOn get() = t("Afspelen op", "Play on")
    fun thisTv(name: String) = t("Deze tv ($name)", "This TV ($name)")
    val unavailable get() = t("Niet beschikbaar", "Unavailable")
    fun playingOn(player: String) = t("Speelt op $player", "Playing on $player")
    fun addedTo(player: String) = t("Toegevoegd aan $player", "Added to $player")
    val playNow get() = t("Nu afspelen", "Play now")
    val playNext get() = t("Hierna afspelen", "Play next")
    val addToQueue get() = t("Aan wachtrij toevoegen", "Add to queue")
    val open get() = t("Openen", "Open")
    val spotifyPairing get() = t(
        "Spotify moet eenmalig worden gekoppeld. Doe dat in de webinterface onder Spotify.",
        "Spotify has to be paired once. Do that in the web interface under Spotify.",
    )
}
