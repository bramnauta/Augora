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
| Deployment | Eén Docker-image (+ librespot-sidecar zodra Spotify er is), als Home Assistant-app of losse container, `network_mode: host` aanbevolen |
| Externe toegang | Achter reverse proxy; forwarded headers + instelbare `PublicBaseUrl` |
| Authenticatie | Lokale accounts (ASP.NET Core Identity, cookie voor de UI, tokens voor API-clients); OIDC optioneel |
| Home Assistant | Eigen API (REST + SignalR); HA-integratie later |
| Talen | Code Engels, UI Nederlands (standaard) + Engels |
| Spotify-audio | librespot-sidecar (later), account-pool |
| Nextory | Laatste fase, na haalbaarheidsonderzoek |

## Solution-indeling

```
src/
  AudioCentral.AppHost            Aspire AppHost (alleen development)
  AudioCentral.ServiceDefaults    OpenTelemetry, health checks, resilience
  AudioCentral.Contracts          DTO's + API-contract; gedeeld met Web, later Android/HA
  AudioCentral.Core               Domeinmodel, abstracties, PlayerManager, QueueManager, streaming
  AudioCentral.Providers.TuneIn   Muziekbron: TuneIn (OPML-API)
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
- `ResolveStreamAsync(item, context)` – levert een `StreamSource`: een upstream-URL
  (radio, podcast), een lokaal bestand, of een *producer* die audio schrijft (librespot).

Items worden geïdentificeerd met een `MediaItemRef` = `{provider}:{type}:{id}`, bijvoorbeeld
`tunein:radio:s24939`. Een provider kan meerdere *instances* hebben (bijv. elk Spotify-account is
een instance van de Spotify-provider); de ref bevat dan de instance-id.

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

## Spotify (fase 3)

- Per gekoppeld account draait een librespot-instance (sidecar-container, één proces per account)
  die audio naar AudioCentral pipet; wij transcoderen en streamen naar de speler.
- De **account-pool**: een Spotify-account kan maar één stream tegelijk hebben.
  - Afspelen gekozen door een gebruiker: eerst het eigen account; is dat bezet, dan een vrij
    gedeeld account; is er niets vrij, dan vraagt de UI of de eigen lopende stream gestopt moet
    worden.
  - Afspelen via de API (Home Assistant): vrij account uit de pool, anders `409 Conflict`
    met een duidelijke foutcode (`spotify_no_free_account`).
- Client-id: centraal instelbaar, per account te overschrijven.
- Accounts hebben Spotify Premium nodig.

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
3. **Spotify** – librespot-sidecar, meerdere accounts, account-pool.
4. **Lokale bestanden + podcasts**.
5. **Android-app** en **Home Assistant-integratie**.
6. **Eigen sync-groepen**.
7. **Nextory** – haalbaarheidsonderzoek, daarna implementatie.
