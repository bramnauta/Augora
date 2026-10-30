# Augora – Architectuur

Augora is een self-hosted muziekserver: één plek die
muziekbronnen (Spotify, TuneIn, lokale bestanden, podcasts, Nextory) koppelt aan spelers
(Google Cast, browser, Android) en die via een eigen API aanstuurbaar is (Blazor UI, Android-app,
Home Assistant).

## Beslissingen

| Onderwerp | Keuze |
|---|---|
| Runtime | .NET 10 LTS, Aspire 13.x voor development |
| Frontend | Blazor WebAssembly + Radzen.Blazor, gehost door de server (same-origin) |
| Database | SQLite via EF Core |
| Deployment | Eén Docker-image (inclusief ffmpeg en go-librespot), als Home Assistant-app of losse container; netwerk via macvlan (eigen LAN-IP, mDNS werkt), geen host-modus |
| Externe toegang | Achter reverse proxy; forwarded headers + instelbare `PublicBaseUrl` |
| Authenticatie | Lokale accounts (ASP.NET Core Identity, cookie voor de UI, tokens voor API-clients); OIDC optioneel |
| Home Assistant | Eigen API (REST + SignalR); HA-integratie later |
| Talen | Code Engels, UI Nederlands (standaard) + Engels |
| Spotify-audio | go-librespot-proces per account in dezelfde container (geen sidecar: de FIFO moet lokaal zijn), account-pool |
| Nextory | Onderzocht: nu niet te bouwen zonder officiële API ([nextory-feasibility.md](nextory-feasibility.md)) |

## Solution-indeling

```
src/
  Augora.AppHost            Aspire AppHost (alleen development)
  Augora.ServiceDefaults    OpenTelemetry, health checks, resilience
  Augora.Contracts          DTO's + API-contract; gedeeld met Web, later Android/HA
  Augora.Core               Domeinmodel, abstracties, PlayerManager, QueueManager, streaming
  Augora.Providers.TuneIn   Muziekbron: TuneIn (OPML-API)
  Augora.Providers.Spotify  Muziekbron: Spotify (Web API + go-librespot, account-pool)
  Augora.Providers.LocalFiles Muziekbron: lokale mappen / NAS (TagLibSharp-index)
  Augora.Providers.Podcasts Muziekbron: podcasts via RSS, zoeken via iTunes
  Augora.Players.Remote     Spelers die zich via de hub aanmelden (browser, Android)
  Augora.Players.Groups     Eigen sync-groepen over speler-types heen
  Augora.Players.GoogleCast Spelerbron: Google Cast (mDNS + Cast v2-protocol)
  Augora.Server             ASP.NET Core host: REST-API, SignalR-hub, stream-endpoints,
                                  persistence, auth, host van de WASM-app
  Augora.Web                Blazor WebAssembly UI
  Augora.Client             Gedeeld deel van de apps: serverclient, tokens, speler-protocol (PlayerController)
  Augora.AndroidPlayer      Native Android-speler (foreground service, MediaPlayer, MediaSession), gedeeld door Mobile en AndroidTv
  Augora.Mobile             .NET MAUI-app voor telefoon en tablet (Android + iOS): WebView-UI + native speler
  Augora.AndroidTv          Android TV-app (WebView-UI + native speler)
integrations/
  homeassistant/custom_components/augora   Home Assistant-integratie (Python)
tests/
  Augora.Tests              xUnit v3
docs/
```

Nieuwe bronnen en spelers komen als losse projecten (`Augora.Providers.Spotify`,
`Augora.Players.Browser`, ...) die zich via een `Add...()`-extensie registreren.

## Kernbegrippen

### Music providers (`IMusicProvider`)
Een bron levert *browse*, *search* en *stream resolution*:

- `BrowseAsync(path)` – hiërarchisch bladeren (TuneIn-categorieën, Spotify-playlists, mappen).
- `SearchAsync(query, types)` – zoeken; de server voegt de resultaten van alle bronnen samen.
- `GetItemAsync(id)` – metadata van één item.
- `GetQueueItemsAsync(item)` – wat in de wachtrij komt: het item zelf, of de nummers van een album/playlist.
- `ResolveStreamAsync(item, PlaybackContext)` – levert een `StreamSource`: `HttpStreamSource`
  (radio, podcast; wordt uitgepakt tot `ResolvedStream`), `FileStreamSource` (lokaal bestand, met
  range-support) of `ProducedStreamSource` (audio die de server zelf maakt, zoals librespot + ffmpeg).
  `PlaybackContext` bevat speler, gebruiker en eventueel de over te nemen speler.

Items worden geïdentificeerd met een `MediaItemRef` = `{provider}:{type}:{id}`, bijvoorbeeld
`tunein:radio:s24939` of `spotify:track:4uLU6hMCjMI75M1A2tKUQC`. Spotify-items zijn
account-onafhankelijk; alleen mappen bevatten het account (`spotify:folder:{account}/playlists`).

### Player providers (`IPlayerProvider`) en spelers (`IPlayer`)
Een spelerbron ontdekt spelers (mDNS, handmatige configuratie, of spelers die zich via de hub
aanmelden zoals browser en Android) en levert `IPlayer`-objecten:

- `PlayAsync(PlayMedia)` – speel een stream-URL van Augora af, met metadata.
- `PauseAsync`, `ResumeAsync`, `StopAsync`, `SetVolumeAsync`, `SetMutedAsync`, `SeekAsync`.
- `State` + `StateChanged`-event (idle/buffering/playing/paused, volume, positie).
- `Capabilities` (seek, volume, grouping, ...).

Google Cast-groepen zijn voor ons gewoon spelers (Google synchroniseert zelf). Eigen sync-groepen
over speler-types heen staan hieronder.

### Sync-groepen (`Augora.Players.Groups`)

- Een groep (`group:{id}`, `PlayerKind.SyncGroup`) is een speler met leden van elk type behalve
  andere groepen; definities staan in SQLite. Elke gebruiker mag groepen beheren (`/api/groups`).
- De groep heeft een eigen wachtrij. Bij afspelen leest een `BroadcastStream` de bron **één keer**
  (HTTP-relay, bestand of librespot-producer) en krijgt elk lid een eigen stream-sessie die uit die
  buffer leest. Een track begint voor elk lid vooraan; live radio begint bij de nieuwste ±128 kB.
  Spotify gebruikt dus één account per groep.
- Commando's (pauze, hervatten, stop, mute) gaan naar alle leden; volume schaalt alle leden
  relatief, zodat de onderlinge balans blijft.
- Is het eerste lid klaar, dan wacht de groep maximaal 5 s op de rest en laat dan zijn wachtrij
  doorgaan. Zolang een speler in een groep speelt (`GroupMembership`), negeert zijn eigen wachtrij
  zijn "klaar"-events; zelf iets op het lid afspelen haalt het uit de groep.
- Synchronisatie: alle leden starten tegelijk, maar Cast-apparaten bufferen zelf en hebben geen
  gedeelde klok met andere spelers, dus het resultaat is "losjes synchroon" (typisch < 1–2 s
  verschil), geen sample-nauwkeurige multiroom. Voor alleen Google-speakers is een Cast-groep strakker.

### Queue (`PlayerQueue`)
Elke speler heeft een wachtrij met items, huidige index, shuffle/repeat. De `QueueManager`
reageert op `StateChanged` (track klaar → volgende) en stuurt de speler aan. Wachtrijen worden
in SQLite bewaard, zodat ze een herstart overleven.

### Streaming
Spelers krijgen **altijd een URL van Augora**, nooit direct de upstream-URL:

```
speler ──HTTP GET──▶ /api/stream/{sessionId}  ──▶ StreamSession ──▶ StreamSource
                                                       │
                                     passthrough relay │ of ffmpeg-transcode
```

- Voordelen: één plek voor playlists (.pls/.m3u) uitpakken, HTTPS/HTTP-problemen, ICY-metadata,
  later transcoding, volumenormalisatie, crossfade en sync.
- `StreamSession`s zijn kortlevend en worden met een niet te raden id uitgegeven (geen auth op
  het stream-endpoint, Cast-apparaten kunnen geen headers meesturen).
- De URL waarop spelers de server bereiken (`StreamBaseUrl`) is configureerbaar; standaard wordt
  het LAN-IP van de host gedetecteerd. Belangrijk omdat de Google Homes in een ander VLAN kunnen
  zitten dan de server.
- ffmpeg zit in het Docker-image; zonder ffmpeg werkt passthrough (voldoende voor radio).

### Discovery over VLAN's heen
- mDNS (`_googlecast._tcp`) werkt als het netwerk mDNS reflecteert (Avahi/router).
- Daarnaast kun je in de UI apparaten handmatig toevoegen (host + poort, ook groepen met hun
  eigen poort); die worden in SQLite bewaard en periodiek gecontroleerd.

## Spotify

```
Web API (metadata) ◀── SpotifyProvider ──▶ SpotifyAccountPool (account ↔ speler)
                              │
                              ▼  ProducedStreamSource
 speler ─GET /api/stream─▶ LibrespotManager ─POST /player/play─▶ go-librespot (1 proces per account)
             ▲                    │                                     │ PCM s16le 44,1 kHz
             └── MP3 320k ◀── ffmpeg ◀──────── PcmPipe ◀── FIFO ◀──────┘
```

- **Koppelen**: OAuth authorization code + PKCE tegen de Spotify-app (client-id centraal in
  `Providers:Spotify:ClientId`, per account te overschrijven). Het refresh-token staat versleuteld
  (data protection) in SQLite; Spotify roteert het bij elke refresh. Het account-id is de
  Spotify-user-id, dus opnieuw koppelen werkt het bestaande account bij.
- **Metadata** via de Web API. Sinds februari 2026: zoeken max. 10 resultaten per type, playlist-inhoud
  via `/playlists/{id}/items` en alleen voor eigen/collaboratieve playlists, geen top tracks per artiest
  (een artiest toont zijn albums). Albums, playlists en "Liked Songs" zijn afspeelbaar: de wachtrij krijgt
  hun nummers (`IMusicProvider.GetQueueItemsAsync`).
- **Audio**: go-librespot (`--config_dir {DataDirectory}/spotify/{account}`) logt in met het access-token
  (`credentials.type: spotify_token`, scope `streaming`) en schrijft PCM naar een FIFO. Per nummer start
  Augora bij het HTTP-verzoek van de speler `POST /player/play`, leest de FIFO tot het
  `not_playing`/`stopped`-event plus een korte stilte (of tot de verwachte lengte binnen is) en sluit dan de
  HTTP-stream, zodat de speler "klaar" meldt en de wachtrij doorgaat. De kanaalbuffer (±6 s) remt librespot af.
- **Account-pool** (één stream per account):
  - Een speler houdt zijn account zolang hij streamt en `AccountHoldTime` (2 min) daarna, zodat het volgende
    nummer hetzelfde account krijgt.
  - Keuze: eigen account van de gebruiker → gedeeld account → vrij account van een ander. Wie het
    afspelen startte wordt per speler onthouden voor het automatisch doorgaan van de wachtrij.
  - Niets vrij → `409 spotify_no_free_account` met `holders` (account, speler, `canTakeOver`). De UI toont
    een dialoog; met `takeoverPlayerId` wordt die speler gestopt en neemt de nieuwe speler het account over.
    Overnemen mag alleen bij je eigen of een gedeeld account. Home Assistant krijgt dezelfde 409.
- Accounts hebben Spotify Premium nodig; een app in development mode staat vijf gebruikers toe.

### Flow mode (Soloist)

Soloist levert audio in echte tijd, en een Cast-speaker buffert bij elke nieuwe stream opnieuw een paar
seconden. Daarom krijgt een speler zolang de wachtrij Spotify-nummers bevat één doorlopende stream:

- De producer krijgt via `PlaybackContext.Continuation` (`IQueueContinuation`) toegang tot de wachtrij.
  ±250 ms voor het einde van een nummer (berekend uit de duur en de `position_sync`-events) vraagt hij het
  volgende item; is dat een Spotify-nummer, dan schuift de wachtrij door en start Soloist het direct in
  dezelfde opname. Zo niet, dan eindigt de stream en gaat de wachtrij verder zoals altijd.
- `PlayMedia.Continuous` vertelt de speler dat de stream langer is dan het eerste item (geen duur naar
  Cast). Via `IContinuousStreamPlayer.AddSegment` leert de speler waar elk volgend item in de stream
  begint, en toont hij het item op zijn huidige positie (Cast, browser/Android, groepen).
- Beperking: een speaker met scherm toont de metadata van het eerste nummer; Augora, Home Assistant
  en de Android-app tonen wel het juiste nummer.

## Lokale bestanden

- Mappen uit `Providers:LocalFiles:Folders` (naam + pad, bijv. een NAS-share als Docker-volume).
- `LocalLibraryScanner` (BackgroundService) leest tags met TagLibSharp en bewaart de index in
  `{DataDirectory}/local-library.json`; bij een nieuwe scan worden alleen gewijzigde bestanden
  (grootte of wijzigingsdatum) opnieuw gelezen. Bladeren per map leest de map live, zodat nieuwe
  bestanden direct zichtbaar zijn.
- Ids: `local:track:{map}/{relatief pad}`, `local:folder:{map}/{pad}/`, `local:album:{hash}`,
  `local:artist:{naam}`. Paden buiten de muziekmappen worden geweigerd.
- Afspelen als `FileStreamSource` met range-support (spoelen werkt). Formaten die Cast en browsers
  afspelen: mp3, flac, m4a/aac, ogg/opus, wav.
- Artwork (ingebed of `cover.jpg`/`folder.jpg`) via het anonieme `/api/artwork/local/{token}`: Cast-apparaten
  kunnen niet inloggen; het token is een hash van het pad en verwijst alleen naar geïndexeerde bestanden.
  Servergerelateerde artwork is relatief; voor Cast maakt de `QueueManager` er een LAN-URL van.

## Podcasts

- Abonnementen gelden voor het hele huishouden (SQLite), toe te voegen via RSS-URL of de iTunes-zoek-API.
- Feeds (RSS 2.0 + iTunes-extensies) worden 30 minuten gecachet; bij een fout wordt de oude kopie gebruikt.
- Ids: `podcast:podcast:{base64url(feed)}` en `podcast:episode:{feed}/{hash(guid)}`, zodat ook podcasts
  zonder abonnement (zoekresultaten) te bladeren en af te spelen zijn.
- Afleveringen spelen via de gewone HTTP-relay.

## API

- REST onder `/api/...`, OpenAPI-document op `/openapi/v1.json`, Scalar-UI in development.
- SignalR-hub `/hubs/events`: realtime events (`PlayerUpdated`, `QueueUpdated`, ...) voor UI,
  Android en Home Assistant.
- Foutmeldingen als RFC 9457 Problem Details met een machineleesbare `code`.

### Remote players (browser, later Android)

Spelers die zelf audio afspelen gebruiken één generiek protocol over de hub
(`Augora.Players.Remote`, contract in `Contracts/RemotePlayers.cs`):

| Richting | Methode | Inhoud |
|---|---|---|
| client → server | `RegisterPlayer(RegisterPlayerRequest)` | `DeviceId` (stabiel per apparaat), naam, soort; geeft de player-id terug |
| client → server | `ReportPlayerState(RemotePlayerStateDto)` | toestand, volume, positie, en `Finished`/`Error` als het item vanzelf stopte |
| client → server | `UnregisterPlayer()` | |
| server → client | `PlayerCommand(RemotePlayerCommandDto)` | `Play` (met stream-URL en metadata), `Pause`, `Resume`, `Stop`, `SetVolume`, `SetMuted` |
| server → client | `PlayerReleased()` | een andere verbinding nam dit apparaat over |

- De player-id is `remote:{deviceId}`, zodat de wachtrij een reload of herverbinding overleeft. Na
  het wegvallen van de verbinding blijft de speler bekend als "niet beschikbaar"; gebruikers kunnen
  hem vergeten.
- Remote players krijgen een **relatieve** stream-URL (`/api/stream/{token}`), zodat ze via de
  publieke URL van de UI (reverse proxy, HTTPS) streamen. Cast-apparaten krijgen de absolute LAN-URL.
- De browser-speler (`wwwroot/js/browserPlayer.js`) gebruikt een `<audio>`-element en de Media
  Session API. Browsers blokkeren automatisch afspelen zonder klik; de UI toont dan een knop
  "Afspelen toestaan". HLS-streams kan Chrome niet native afspelen.

## Apps (Android, Android TV, iOS)

Twee apps: `Augora.Mobile` (.NET MAUI, telefoon en tablet, Android 8+ en iOS 15+) en `Augora.AndroidTv`
(.NET for Android, Android TV via de leanback-launcher). Android TV heeft een eigen app omdat MAUI daar
niet op mikt en de bediening met een afstandsbediening een andere UI vraagt.

- Beide tonen de web-UI in een WebView (user agent `AugoraAndroid/1.0` of `AugoraIos/1.0`; de UI verbergt
  dan de browser-speler). Inloggen gebeurt in de WebView, dus OIDC werkt zonder extra code.
- Zodra de WebView een `Augora.Auth`-cookie heeft, wisselt de app die via `POST /api/auth/token`
  in voor een access- en refresh-token (alleen met de cookie toegestaan, niet met een bearer-token).
- `Augora.Client` bevat wat niet platformspecifiek is: `ServerClient` (tokens), `AppSettings` en
  `PlayerController`. Die gebruikt hetzelfde remote-player-protocol als de browser: `RegisterPlayer` met
  een vaste device-id en soort `Phone` of `Tv`, commando's uitvoeren, state elke 5 s rapporteren. Het
  platform levert alleen een `IPlayerBackend`: laden, afspelen, pauzeren, zoeken, volume en de
  lock-screen/notificatie.
- Android (`Augora.AndroidPlayer`): `PlayerService` (foreground service, `mediaPlayback`) met
  `MediaPlayer`; audio focus, wake- en wifi-lock houden het afspelen gaande. Notificatie, MediaSession
  (koptelefoon, Bluetooth, TV-afstandsbediening) en verlies van audio focus sturen REST-commando's naar
  de server, zodat wachtrij en UI's het volgen.
- iOS (`IosPlayerBackend` in `Augora.Mobile`): `AVPlayer`, `MPRemoteCommandCenter` en
  `MPNowPlayingInfoCenter` voor het vergrendelscherm, en `UIBackgroundModes: audio`. Er is geen service:
  de controller leeft in de app, dus een stilstaande app die iOS onderbreekt is niet als speler te bereiken.
- De iOS-code compileert op Windows (`-p:BuildIos=true`) maar is nog niet op een toestel getest; bouwen,
  signeren en installeren vergt een Mac.

## Home Assistant-integratie

- Custom component `integrations/homeassistant/custom_components/augora` (config flow met URL +
  API-token, reauth bij een ingetrokken token).
- `media_player` per Augora-speler; features volgen `PlayerFeatures`. Media browsing gebruikt
  `/api/browse`, `play_media` neemt Augora-URI's met `enqueue` → `QueueOption`.
- Realtime: de integratie verbindt rechtstreeks met de hub via WebSocket (SignalR JSON-protocol, zonder
  negotiate), met het API-token in de header; na elke (her)verbinding haalt hij de spelers opnieuw op.

## Authenticatie

Altijd inloggen; er is geen anonieme toegang tot de API (behalve het stream-endpoint, dat met een
niet te raden token werkt, en `/api/auth/status|setup|login|refresh`).

| Client | Methode |
|---|---|
| Web-UI | Identity-cookie (`POST /api/auth/login` met `useCookies: true`), 30 dagen sliding |
| Android-app | Inloggen in de WebView (cookie), dan `POST /api/auth/token` → access token (1 uur) + refresh token (60 dagen, `POST /api/auth/refresh`). Apps zonder WebView: `POST /api/auth/login` met `useCookies: false` |
| Home Assistant, scripts | Langlevend API-token (`act_...`), aan te maken onder Account; alleen de SHA-256 wordt opgeslagen |
| SSO (optioneel) | OpenID Connect (Authentik, Keycloak, ...) via `Authentication:Oidc`; nieuwe gebruikers worden automatisch aangemaakt |

- Een policy-scheme kiest per request: `Bearer act_...` → API-token, andere bearer → Identity-bearer,
  anders cookie. Op de hub mag het token ook als `access_token` in de query staan.
- De eerste gebruiker (via `/setup` of de eerste OIDC-login) wordt **Admin**. Admins beheren
  gebruikers, handmatige Cast-apparaten en instellingen; gewone gebruikers kunnen alles afspelen
  en bedienen. Alle spelers zijn voor iedereen zichtbaar.
- Data-protection-sleutels staan in `{DataDirectory}/keys`, zodat logins en refresh-tokens een
  herstart overleven.

## Deployment

- `Dockerfile` bouwt één image (multi-arch amd64/arm64) met ffmpeg.
- Data in `/data` (SQLite, cache, config).
- `docker-compose.yml`: macvlan-netwerk (`lan`) voor discovery en streams, plus een bridge-netwerk
  (`proxy`) voor een reverse proxy op dezelfde host. Met twee netwerken is `StreamBaseUrl` vast op het
  macvlan-adres gezet. In een bridge-netwerk zonder `StreamBaseUrl` logt de server een waarschuwing.
- Home Assistant-app: later, als één container met `host_network: true` in `config.yaml`: mDNS en het stream-adres werken dan zonder macvlan. go-librespot zit altijd in dezelfde
  container als Augora; de audio loopt via een FIFO, dus een losse librespot-container kan niet.

## Roadmap

1. ✅ **Basis** – solution, Aspire, Docker, core-abstracties, TuneIn → Google Cast, Blazor UI.
2. ✅ **Auth + browser-speler** – Identity, tokens, browser als speler via de hub.
3. ✅ **Spotify** – go-librespot per account, meerdere accounts, account-pool.
4. ✅ **Lokale bestanden + podcasts**.
5. ✅ **Android-app** en **Home Assistant-integratie**.
6. ✅ **Eigen sync-groepen** (losjes synchroon, gedeelde bron).
7. ✅ **Nextory – haalbaarheidsonderzoek**: nu niet betrouwbaar te bouwen (geen publieke API, interne API
   gewijzigd, DRM onbekend). Zie [nextory-feasibility.md](nextory-feasibility.md); implementatie pas na
   officiële toegang of na vaststellen dat de audio niet DRM-beveiligd is.
