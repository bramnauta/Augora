# Nextory – haalbaarheidsonderzoek

Datum: 29 oktober 2026. Doel: kan AudioCentral luisterboeken van Nextory afspelen op Google Home, in de
browser en op Android, net als de andere bronnen?

## Conclusie

**Nu niet betrouwbaar te bouwen.** Er is geen publieke API, de interne API is al eens omgegooid (de enige
bekende community-client werkt daardoor niet meer), en of de audio DRM-beveiligd is, is zonder account
niet vast te stellen. Een implementatie zou op aannames gebouwd zijn en bij de eerste wijziging van Nextory
breken. Implementatie volgt pas als een van de routes hieronder open gaat.

## Bevindingen

| Vraag | Antwoord | Bron |
|---|---|---|
| Publieke of partner-API? | Nee. De API is intern; hij is de enige verbinding tussen de Nextory-apps en de backend. Een deel van de interne referentie (2021) is als portfolioproject gepubliceerd; de volledige versie is niet openbaar. | [JodyWinter/nextoryapi](https://github.com/JodyWinter/nextoryapi) |
| Bestaande clients? | Eén Rust-client/CLI (AGPL-3.0, 2023) die boeken downloadde. De README meldt dat Nextory zijn API heeft omgegooid en dat "all of this stopped working". | [HookedBehemoth/nextory](https://github.com/HookedBehemoth/nextory) |
| Hoe kwam die client aan audio? | Na een "activation" van het boek een gewone HTTP-download van het bestand (MP3 kreeg alleen ID3-tags), zonder decryptie in de code. Dat gold voor de oude API; de huidige is onbekend. | `src/downloader.rs` in dezelfde repo |
| DRM? | Niet vast te stellen zonder account. Offline luisteren in de app bestaat, maar hoe dat beveiligd is, is nergens gedocumenteerd. Een commerciële tool claimt DRM van Nextory-boeken te verwijderen, wat erop wijst dat er in elk geval deels beveiliging is. | App Store-vermelding; [Epubor](https://www.epubor.com/how-to-download-nextory-for-offline-reading-and-listening.html) |
| Webspeler? | Volgens derden kun je via een moderne browser luisteren; officiële Nederlandse pagina's noemen alleen de app. | [Futura Sciences](https://www.futura-sciences.com/tech/definitions/logiciels-nextory-livres-audio-bd-magazines-27746/) |
| Chromecast in de app? | Niet gevonden in de openbare informatie; te controleren via het Cast-icoon in de app. | — |
| Voorwaarden | De voorwaarden ([nextory.se/medlemsvillkor](https://nextory.se/medlemsvillkor)) waren niet leesbaar in het onderzoek. Gebruik van een interne API door een eigen client is bij dit soort diensten doorgaans niet toegestaan. | — |

## Opties

1. **Officiële toegang vragen (aanbevolen).** Nextory werkt met partners (telecom, banken, auto-integraties).
   Vraag om API-toegang voor een privé, niet-commerciële integratie. Met een officiële API is dit een gewone
   provider, vergelijkbaar met Spotify: zoeken, bladeren, afspelen via `HttpStreamSource`, plus
   voortgang per hoofdstuk.
2. **Interne API van het eigen account onderzoeken.** Alleen zinvol als de audio *niet* DRM-beveiligd is
   (gewone MP3/AAC of HLS zonder sleutels achter een gesigneerde URL). Dan is een provider technisch
   eenvoudig, maar kwetsbaar (API kan zonder aankondiging wijzigen) en waarschijnlijk in strijd met de
   voorwaarden. Is de audio wel beveiligd (Widevine/FairPlay of versleutelde HLS met een licentieserver),
   dan stopt het hier: AudioCentral omzeilt geen DRM.
3. **Nu al luisteren op Google Home zonder integratie.** Cast vanuit de Nextory-app als die een Cast-knop
   heeft, anders "Mijn audio casten" in de Google Home-app op Android, of de browser-tab casten vanuit
   Chrome als de webspeler werkt. AudioCentral toont de speaker dan als "in gebruik door een andere app".

## Wat nodig is om verder te gaan

- Voor optie 1: een antwoord van Nextory met documentatie en voorwaarden.
- Voor optie 2: met je eigen account in de webspeler een HAR-opname (DevTools → Network) van inloggen,
  een boek openen en een paar seconden afspelen. Daaruit blijkt binnen een uur of de audio vrij of
  beveiligd is en welke endpoints nodig zijn. Deel geen wachtwoorden; tokens in de opname kun je daarna
  intrekken door uit te loggen.

## Voorbereiding in AudioCentral

Wat een Nextory-provider nodig heeft, is grotendeels al aanwezig:

- `MediaType.Audiobook` en `IMusicProvider.GetQueueItemsAsync` (een boek → hoofdstukken in de wachtrij);
- `HttpStreamSource`/relay voor gewone audio, `ProducedStreamSource` als er een omzetting nodig is;
- per gebruiker gekoppelde accounts en versleutelde tokens (zoals bij Spotify).

Nog te bouwen bij een "go": voortgang per gebruiker per boek (hervatten waar je was), afspeelsnelheid,
en een slaaptimer. Die zijn ook nuttig voor podcasts en kunnen los van Nextory worden toegevoegd.
