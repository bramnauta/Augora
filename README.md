# AudioCentral

Self-hosted muziekserver: bronnen (TuneIn, Spotify, lokale
bestanden, podcasts, Nextory) afspelen op Google Cast-apparaten en -groepen, in de browser en
op Android. Zie [docs/architecture.md](docs/architecture.md) voor ontwerp en roadmap.

## Status

- ✅ TuneIn: bladeren, zoeken, afspelen (radio en podcast-afleveringen), optioneel favorieten via `Providers:TuneIn:Username`
- ✅ Google Cast: mDNS-discovery, handmatig toevoegen (andere VLAN's), groepen, volume/pauze/stop
- ✅ Wachtrij per speler (bewaard in SQLite), realtime updates via SignalR
- ✅ Blazor WebAssembly-UI (Radzen), Nederlands en Engels
- ✅ Inloggen: lokale accounts met admin-rol, API-tokens (Home Assistant), access/refresh-tokens (apps), optioneel OIDC
- ✅ Browser als speler (ook op telefoon), via hetzelfde protocol dat de Android-app gaat gebruiken
- ✅ Spotify: meerdere accounts, bladeren (playlists, nummers, albums, artiesten), zoeken, afspelen via go-librespot
- ✅ Lokale bestanden (NAS): bladeren per map, artiest en album, zoeken in tags, artwork
- ✅ Podcasts: abonneren via RSS-URL of zoeken in de iTunes-catalogus
- ✅ Android-app (telefoon en TV): de UI plus een native speler die op de achtergrond doorspeelt
- ✅ Home Assistant-integratie: media players, mediabrowser, realtime updates
- ⏳ Eigen sync-groepen, Nextory

Bij de eerste start vraagt de UI om het eerste account aan te maken; dat wordt beheerder.

## Spotify instellen

1. Maak een app op [developer.spotify.com](https://developer.spotify.com/dashboard) (Web API) en zet
   als redirect-URI `https://<jouw-host>/api/spotify/callback`. Spotify accepteert alleen https, behalve
   voor `http://127.0.0.1:<poort>`. De exacte URI staat op de Spotify-pagina in AudioCentral.
2. Zet de client-id in `Providers__Spotify__ClientId`, of vul per account een eigen client-id in.
   Een app in development mode staat maximaal vijf Spotify-gebruikers toe, die je in het dashboard
   moet toevoegen; de eigenaar van de app heeft Premium nodig.
3. Koppel onder **Spotify** in de UI je account. Iedere gebruiker koppelt zijn eigen account;
   beheerders kunnen ook gedeelde accounts koppelen.

Spotify staat één stream per account toe. Bij afspelen kiest AudioCentral eerst je eigen account,
dan een gedeeld account, dan een vrij account van een ander. Is er geen account vrij, dan biedt de UI
aan een stream te stoppen; via de API krijg je `409 spotify_no_free_account` met de lijst `holders`
en kun je het verzoek herhalen met `takeoverPlayerId`.

Per account draait een [go-librespot](https://github.com/devgianlu/go-librespot)-proces; ffmpeg zet
de audio om naar MP3 320 kbps. Beide zitten in de Docker-image. Buiten Docker moeten `go-librespot`
en `ffmpeg` op het PATH staan (of stel `Providers__Spotify__LibrespotPath` / `__FfmpegPath` in).
Spotify geeft apps sinds februari 2026 alleen de inhoud van playlists die je zelf bezit of waaraan je
meewerkt; gevolgde playlists zijn daardoor niet af te spelen.

## Android-app

`src/AudioCentral.Android` (C#, .NET for Android, Android 8.0+, ook Android TV). Bouwen en installeren
op een toestel met USB-debugging:

```bash
dotnet build src/AudioCentral.Android -t:Install -c Release
```

Bij de eerste start vraagt de app het serveradres. Daarna toont hij de gewone UI, waar je inlogt (ook met
OIDC). De app wisselt die sessie in voor tokens (`POST /api/auth/token`) en meldt het toestel aan als
speler. Het afspelen loopt in een foreground service met mediabediening in de notificatie, op het
vergrendelscherm en via Bluetooth/TV-afstandsbediening. Een ander serveradres instellen: app-gegevens
wissen, of de server onbereikbaar maken (dan verschijnt het adresscherm).

## Home Assistant

Kopieer `integrations/homeassistant/custom_components/audiocentral` naar `config/custom_components/`
van Home Assistant en herstart. Voeg de integratie toe met de URL van AudioCentral en een API-token
(maak dat aan onder **Account**). Elke speler wordt een `media_player`:

- afspelen, pauzeren, volgende/vorige, volume, mute, shuffle en herhalen;
- de mediabrowser toont alle bronnen (TuneIn, Spotify, lokale muziek, podcasts);
- `media_player.play_media` met een AudioCentral-URI, bijv.

```yaml
action: media_player.play_media
target:
  entity_id: media_player.keuken
data:
  media_content_type: music
  media_content_id: tunein:radio:s24939
  enqueue: replace
```

Updates komen realtime binnen via de SignalR-hub. Zijn alle Spotify-accounts bezet, dan geeft de actie
een fout met wie ze gebruiken.

## Ontwikkelen

Vereist: .NET SDK 10.0.400+, Docker (optioneel).

```bash
dotnet run --project src/AudioCentral.AppHost
```

Het Aspire-dashboard opent; de UI staat onder de `server`-resource. Cast-apparaten halen audio
op via poort **8097** op alle interfaces: sta die toe in de firewall.

Tests:

```bash
dotnet test --project tests/AudioCentral.Tests
```

Nieuwe EF Core-migratie:

```bash
dotnet dotnet-ef migrations add <Naam> --project src/AudioCentral.Server --output-dir Data/Migrations
```

## Draaien in Docker

```bash
docker compose up -d --build
```

De UI staat op `http://<host>:4727`. Gebruik `network_mode: host` (Linux) zodat mDNS werkt.

Belangrijke instellingen (omgevingsvariabelen):

| Variabele | Betekenis |
|---|---|
| `AudioCentral__StreamBaseUrl` | URL waarop spelers de server bereiken, bijv. `http://192.168.1.10:4727`. Leeg = LAN-IP detecteren. |
| `AudioCentral__PublicBaseUrl` | Publieke URL achter een reverse proxy. |
| `Providers__TuneIn__Username` | TuneIn-gebruikersnaam voor favorieten. |
| `Providers__Spotify__ClientId` | Standaard client-id van je Spotify-app. |
| `Providers__LocalFiles__Folders__0__Name` / `__Path` | Muziekmap, bijv. `Muziek` en `/music` (koppel je NAS-share als volume). Meer mappen met `__1__`, `__2__`, ... De naam zit in elke item-id: houd hem stabiel. |
| `Providers__LocalFiles__RescanInterval` | Hoe vaak de mappen opnieuw gescand worden (standaard `06:00:00`); beheerders kunnen onder Instellingen direct scannen. |
| `Players__GoogleCast__Discovery` | `false` om mDNS uit te zetten (alleen handmatige apparaten). |
| `Authentication__Oidc__Authority` | Issuer-URL van je OIDC-provider; leeg = geen SSO. Redirect-URI: `https://<host>/signin-oidc`. |
| `Authentication__Oidc__ClientId` / `__ClientSecret` | Client-gegevens bij de OIDC-provider. |
| `Authentication__Oidc__DisplayName` | Tekst op de inlogknop, bijv. `Authentik`. |

`/data` bevat de database én de sleutels van inlogsessies (`/data/keys`): bewaar het volume.

## API

REST onder `/api` (OpenAPI: `/openapi/v1.json`, Scalar-UI op `/scalar` in development) en een
SignalR-hub op `/hubs/events`. Fouten zijn Problem Details met een `code`-veld.

Voor scripts en Home Assistant: maak onder **Account** een API-token aan en stuur het mee:

```bash
curl -H "Authorization: Bearer act_..." http://<host>:4727/api/players
```
