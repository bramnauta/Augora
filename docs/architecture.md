# AudioCentral – Architectuur

AudioCentral is een self-hosted muziekserver: één plek die
muziekbronnen (Spotify, TuneIn, lokale bestanden, podcasts, Nextory) koppelt aan spelers
(Google Cast, browser, Android) en die via een eigen API aanstuurbaar is (Blazor UI, Android-app,
Home Assistant).

## Beslissingen

| Onderwerp | Keuze |
|---|---|
| Runtime | .NET 10 LTS, Aspire 13.x voor development |
| Frontend | Blazor WebAssembly + Radzen.Blazor, gehost door de server (same-origin) |
| Database | SQLite via EF Core |
| Deployment | Eén Docker-image (inclusief ffmpeg en go-librespot), als Home Assistant-app of losse container, `network_mode: host` aanbevolen |
| Externe toegang | Achter reverse proxy; forwarded headers + instelbare `PublicBaseUrl` |
| Authenticatie | Lokale accounts (ASP.NET Core Identity, cookie voor de UI, tokens voor API-clients); OIDC optioneel |
| Home Assistant | Eigen API (REST + SignalR); HA-integratie later |
| Talen | Code Engels, UI Nederlands (standaard) + Engels |
| Spotify-audio | go-librespot-proces per account in dezelfde container (geen sidecar: de FIFO moet lokaal zijn), account-pool |
| Nextory | Laatste fase, na haalbaarheidsonderzoek |

## Solution-indeling

```
src/
  AudioCentral.AppHost            Aspire AppHost (alleen development)
  AudioCentral.ServiceDefaults    OpenTelemetry, health checks, resilience
  AudioCentral.Contracts          DTO's + API-contract; gedeeld met Web, later Android/HA
  AudioCentral.Core               Domeinmodel, abstracties, PlayerManager, QueueManager, streaming
  AudioCentral.Providers.TuneIn   Muziekbron: TuneIn (OPML-API)
  AudioCentral.Providers.Spotify  Muziekbron: Spotify (Web API + go-librespot, account-pool)
  AudioCentral.Providers.LocalFiles Muziekbron: lokale mappen / NAS (TagLibSharp-index)
  AudioCentral.Providers.Podcasts Muziekbron: podcasts via RSS, zoeken via iTunes
  AudioCentral.Players.Remote     Spelers die zich via de hub aanmelden (browser, Android)
  AudioCentral.Players.GoogleCast Spelerbron: Google Cast (mDNS + Cast v2-protocol)
  AudioCentral.Server             ASP.NET Core host: REST-API, SignalR-hub, stream-endpoints,
                                  persistence, auth, host van de WASM-app
  AudioCentral.Web                Blazor WebAssembly UI
tests/
  AudioCentral.Tests              xUnit v3
docs/
```

Nieuwe bronnen en spelers komen als losse projecten (`AudioCentral.Providers.Spotify`,
`AudioCentral.Players.Browser`, ...) die zich via een `Add...()`-extensie registreren.

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

- `PlayAsync(PlayMedia)` – speel een stream-URL van AudioCentral af, met metadata.
- `PauseAsync`, `ResumeAsync`, `StopAsync`, `SetVolumeAsync`, `SetMutedAsync`, `SeekAsync`.
- `State` + `StateChanged`-event (idle/buffering/playing/paused, volume, positie).
- `Capabilities` (seek, volume, grouping, ...).

Google Cast-groepen zijn voor ons gewoon spelers (Google synchroniseert zelf). Voor latere
*eigen* sync-groepen over speler-types heen is er `IPlayer.Kind = Group` en een
`GroupMembers`-lijst; de stream-pipeline krijgt dan een gedeelde klok. Dat is nu alleen voorbereid.

### Queue (`PlayerQueue`)
Elke speler heeft een wachtrij met items, huidige index, shuffle/repeat. De `QueueManager`
reageert op `StateChanged` (track klaar → volgende) en stuurt de speler aan. Wachtrijen worden
in SQLite bewaard, zodat ze een herstart overleven.

### Streaming
Spelers krijgen **altijd een URL van AudioCentral**, nooit direct de upstream-URL:

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
  AudioCentral bij het HTTP-verzoek van de speler `POST /player/play`, leest de FIFO tot het
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
(`AudioCentral.Players.Remote`, contract in `Contracts/RemotePlayers.cs`):

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

## Authenticatie

Altijd inloggen; er is geen anonieme toegang tot de API (behalve het stream-endpoint, dat met een
niet te raden token werkt, en `/api/auth/status|setup|login|refresh`).

| Client | Methode |
|---|---|
| Web-UI | Identity-cookie (`POST /api/auth/login` met `useCookies: true`), 30 dagen sliding |
| Android-app | `POST /api/auth/login` met `useCookies: false` → access token (1 uur) + refresh token (60 dagen, `POST /api/auth/refresh`) |
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
- Home Assistant-app: later een `config.yaml` met `host_network: true` en ingress.

## Roadmap

1. ✅ **Basis** – solution, Aspire, Docker, core-abstracties, TuneIn → Google Cast, Blazor UI.
2. ✅ **Auth + browser-speler** – Identity, tokens, browser als speler via de hub.
3. ✅ **Spotify** – go-librespot per account, meerdere accounts, account-pool.
4. ✅ **Lokale bestanden + podcasts**.
5. **Android-app** en **Home Assistant-integratie**.
6. **Eigen sync-groepen**.
7. **Nextory** – haalbaarheidsonderzoek, daarna implementatie.
