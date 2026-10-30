# Augora

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
- ✅ Android-app (telefoon, tablet en TV): de UI plus een native speler die op de achtergrond doorspeelt
- 🚧 iOS-app (iPhone en iPad, MAUI): gebouwd, maar nog niet getest op een toestel
- ✅ Home Assistant-integratie: media players, mediabrowser, realtime updates
- ✅ Eigen groepen: spelers van elk type samen laten spelen (losjes synchroon, één bron en één Spotify-account per groep)
- ❌ Nextory: onderzocht, nu niet betrouwbaar te bouwen; zie [docs/nextory-feasibility.md](docs/nextory-feasibility.md)

Bij de eerste start vraagt de UI om het eerste account aan te maken; dat wordt beheerder.

## Spotify instellen

1. Maak een app op [developer.spotify.com](https://developer.spotify.com/dashboard) (Web API) en zet
   als redirect-URI `https://<jouw-host>/api/spotify/callback`. Spotify accepteert alleen https, behalve
   voor `http://127.0.0.1:<poort>`. De exacte URI staat op de Spotify-pagina in Augora.
2. Vul de client-id in als beheerder onder **Spotify** in de UI (of zet `Providers__Spotify__ClientId`),
   of vul per account een eigen client-id in.
   Een app in development mode staat maximaal vijf Spotify-gebruikers toe, die je in het dashboard
   moet toevoegen; de eigenaar van de app heeft Premium nodig.
3. Koppel onder **Spotify** in de UI je account. Iedere gebruiker koppelt zijn eigen account;
   beheerders kunnen ook gedeelde accounts koppelen.

Spotify staat één stream per account toe. Bij afspelen kiest Augora eerst je eigen account,
dan een gedeeld account, dan een vrij account van een ander. Is er geen account vrij, dan biedt de UI
aan een stream te stoppen; via de API krijg je `409 spotify_no_free_account` met de lijst `holders`
en kun je het verzoek herhalen met `takeoverPlayerId`.

Per account draait een [go-librespot](https://github.com/devgianlu/go-librespot)-proces; ffmpeg zet
de audio om naar MP3 320 kbps. Beide zitten in de Docker-image. Buiten Docker moeten `go-librespot`
en `ffmpeg` op het PATH staan (of stel `Providers__Spotify__LibrespotPath` / `__FfmpegPath` in).
Spotify geeft apps sinds februari 2026 alleen de inhoud van playlists die je zelf bezit of waaraan je
meewerkt; gevolgde playlists zijn daardoor niet af te spelen.

### Afspeel-engine per account

| Engine | Koppelen | Opmerkingen |
|---|---|---|
| go-librespot (standaard) | Eenmalig een code goedkeuren op spotify.com/pair | Zit in de image. Spotify weigert voor veel (nieuwere) accounts de sleutel van nummers: `failed retrieving aes key`. |
| Spotify Soloist | Eenmalig in de Spotify-app het apparaat "Augora (naam)" kiezen, op hetzelfde netwerk | Spotify's eigen client; werkt met alle Premium-accounts. Kies hem onder **Spotify** → het instellingen-icoon bij een account. |

Over Soloist:

- Spotify verbiedt het meeleveren ervan. Augora downloadt hem pas nadat je dat per account hebt
  goedgekeurd, van `soloist-builds.spotifycdn.com`, en vervangt hem voordat hij na 90 dagen verloopt.
  De voorwaarden van Spotify staan dit gebruik niet duidelijk toe: op eigen risico.
- Je hebt een API-key nodig van [developer.spotify.com/dashboard/soloist](https://developer.spotify.com/dashboard/soloist),
  aangemaakt met hetzelfde Premium-account.
- Soloist speelt alleen af via PulseAudio of PipeWire. In de container draait Augora daarom een eigen
  PulseAudio-daemon met een virtuele uitgang per account, en ffmpeg neemt die op.
- Koppelen gaat via Spotify Connect (mDNS). Dat werkt in de Home Assistant-app en met macvlan, maar niet
  in Docker Desktop.

## Apps

| App | Project | Platform |
|---|---|---|
| Telefoon en tablet | `src/Augora.Mobile` (.NET MAUI) | Android 8.0+ en iOS 15+ |
| Android TV | `src/Augora.Tv` (Kotlin, Jetpack Compose for TV) | Android TV, Android 8.0+ |

De telefoon-app is een eigen, native app met Home, Zoeken en Wachtrij, een miniplayer en een afspeelscherm.
Hij speelt standaard op het toestel zelf; kies bovenin de miniplayer of op het afspeelscherm een andere
speler (Cast, andere apparaten) om daarheen te spelen. Beheer (gebruikers, Spotify koppelen, mappen,
instellingen) blijft in de webinterface.

Bij de eerste start vraagt de app het serveradres en laat hij inloggen met gebruikersnaam en wachtwoord of,
als de server dat heeft, met OIDC via de browser van het toestel. Het afspelen loopt native, met
mediabediening op het vergrendelscherm en via Bluetooth. Uitloggen kan onderaan de spelerkiezer.

De Android TV-app is een eigen Kotlin-app met Compose for TV (bediening met de afstandsbediening): Home,
Zoeken, Wachtrij, een spelerbalk en een afspeelscherm, en hij speelt standaard op de tv zelf. Inloggen kan
op de tv alleen met gebruikersnaam en wachtwoord. Bouwen met Android Studio, of op de opdrachtregel:

```bash
cd src/Augora.Tv
./gradlew assembleDebug
```

Gebruik daarvoor een JDK 17 of 21 als `JAVA_HOME` (niet 25) en een Android SDK met platform 36.

Installeren van de telefoon-app op een Android-toestel met USB-debugging:

```bash
dotnet build src/Augora.Mobile -f net10.0-android -t:Install -c Release
```

De iOS-app bouwen kan alleen op een Mac (of vanuit Visual Studio, gekoppeld aan een Mac) met Xcode en de
`ios`- en `maui`-workloads. Op macOS staat het iOS-target vanzelf aan; elders zet je `-p:BuildIos=true`
(dan compileert het, maar installeren kan niet). Een geïnstalleerde app zonder betaald Apple
Developer-account verloopt na 7 dagen.

```bash
dotnet build src/Augora.Mobile -f net10.0-ios -c Release
```

Beperking op iOS: er is geen service die op de achtergrond blijft draaien. Zolang er iets speelt blijft de
app actief (achtergrondaudio), maar een stilstaande app kan door iOS worden onderbroken; dan is het toestel
pas weer als speler beschikbaar zodra je de app opent.

## Home Assistant

### Server als Home Assistant-app

1. Push naar `main`: de workflow `.github/workflows/ha-app.yml` bouwt de images en zet ze op
   `ghcr.io/bramnauta/augora-ha`. Zet het package daarna op **Public** (GitHub → Packages → Package
   settings), anders kan Home Assistant het niet ophalen.
2. In Home Assistant: **Instellingen → Apps → App store → ⋮ → Repositories** en voeg
   `https://github.com/bramnauta/Augora` toe. Installeer **Augora** en start de app.
3. Open de web-UI op `http://<home-assistant>:4727` (een bewust ongebruikelijke poort, omdat de app het netwerk van de host deelt) en maak het eerste (admin-)account aan.

De app draait met `host_network` (mDNS voor Spotify Connect en Cast, geen macvlan nodig). De HA-mappen
`/media` en `/share` zijn als muziekmappen gekoppeld; Spotify-client-id, TuneIn-gebruikersnaam en de
stream-/publieke URL stel je in onder de app-configuratie. Verhoog `version` in `ha-app/config.yaml`
bij elke release, anders biedt Home Assistant geen update aan.

### Integratie

Installeer via de Marketplace van Home Assistant (2026.11 en nieuwer) of via HACS: voeg
`https://github.com/bramnauta/Augora` toe als aangepaste repository (type Integratie) en installeer
**Augora**. Handmatig kan ook: kopieer `custom_components/augora` naar `config/custom_components/`.
Herstart Home Assistant. Voeg de integratie toe met de URL van Augora en een API-token
(maak dat aan onder **Account**). Elke speler wordt een `media_player`:

- afspelen, pauzeren, volgende/vorige, volume, mute, shuffle en herhalen;
- de mediabrowser toont alle bronnen (TuneIn, Spotify, lokale muziek, podcasts);
- `media_player.play_media` met een Augora-URI, bijv.

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
dotnet run --project src/Augora.AppHost
```

Het Aspire-dashboard opent; de UI staat onder de `server`-resource. Cast-apparaten halen audio
op via poort **8097** op alle interfaces: sta die toe in de firewall.

Spotify-audio (go-librespot) werkt alleen onder Linux. Om dat lokaal te testen start Aspire de server
als container uit de `Dockerfile`:

```bash
dotnet run --project src/Augora.AppHost --launch-profile container
```

De UI staat dan op `http://127.0.0.1:4727`. Gebruik dat adres, niet `localhost`, als redirect-URI bij
Spotify (`http://127.0.0.1:4727/api/spotify/callback`). Speakers halen de stream op via poort 4727 op
het LAN-IP van je pc. Docker Desktop laat mDNS niet door; daarom zoekt de AppHost de speakers en
groepen bij het starten op Windows en geeft ze door aan de container (`Players__GoogleCast__Devices`).
Nieuwe speakers of gewijzigde groepspoorten verschijnen na een herstart van de AppHost.
Data staat in het Docker-volume `augora-dev-data`.

Tests:

```bash
dotnet test --project tests/Augora.Tests
```

Nieuwe EF Core-migratie:

```bash
dotnet dotnet-ef migrations add <Naam> --project src/Augora.Server --output-dir Data/Migrations
```

## Draaien in Docker

Het compose-voorbeeld gebruikt een **macvlan**-netwerk: de container krijgt een eigen IP in je LAN,
zodat mDNS de Cast-speakers vindt en de speakers de stream rechtstreeks ophalen, zonder
`network_mode: host`. Kopieer `.env.example` naar `.env` en vul interface, subnet, gateway en een
vrij IP buiten de DHCP-pool in. Daarna:

```bash
docker compose up -d --build
```

De UI staat dan op `http://<AUGORA_IP>:4727`.

- **Reverse proxy op dezelfde host:** macvlan sluit de host zelf af van de container. Hang je
  proxy-container daarom aan het `proxy`-netwerk uit de compose en proxy naar `http://augora:4727`.
- **Speakers in een ander VLAN:** gebruik als `LAN_PARENT` de VLAN-interface van dat netwerk
  (bijv. `eth0.20`), of laat de router mDNS reflecteren en verkeer naar `AUGORA_IP:4727` toe.
- **Liever een gewoon bridge-netwerk:** publiceer poort 4727, zet `Augora__StreamBaseUrl` op
  het LAN-adres van de host en voeg de speakers handmatig toe onder Instellingen (mDNS werkt dan niet).
  Zonder `StreamBaseUrl` waarschuwt de log dat spelers een Docker-adres krijgen. Docker Desktop
  (Windows/macOS) ondersteunt macvlan niet goed; gebruik daar deze bridge-opzet.

Belangrijke instellingen (omgevingsvariabelen):

| Variabele | Betekenis |
|---|---|
| `Augora__StreamBaseUrl` | URL waarop spelers de server bereiken, bijv. `http://192.168.1.10:4727`. Leeg = LAN-IP detecteren. |
| `Augora__PublicBaseUrl` | Publieke URL achter een reverse proxy. |
| `Providers__TuneIn__Username` | TuneIn-gebruikersnaam voor favorieten. Optioneel: beheerders stellen het ook in onder Instellingen in de UI; wat daar staat gaat voor. |
| `Providers__Spotify__ClientId` | Standaard client-id van je Spotify-app. Optioneel: beheerders stellen het ook in onder Spotify in de UI; wat daar staat gaat voor. |
| `Providers__LocalFiles__Folders__0__Name` / `__Path` | Muziekmap, bijv. `Muziek` en `/music` (koppel je NAS-share als volume). Meer mappen met `__1__`, `__2__`, ... De naam zit in elke item-id: houd hem stabiel. Optioneel: beheerders beheren de mappen ook onder Instellingen in de UI; wat daar staat gaat voor. Het volume moet wel in de container gekoppeld zijn. |
| `Providers__LocalFiles__RescanInterval` | Hoe vaak de mappen opnieuw gescand worden (standaard `06:00:00`). Beheerders kunnen het interval ook onder Instellingen in de UI zetten (5 minuten tot 7 dagen; gaat voor) en direct scannen. |
| `Players__GoogleCast__Discovery` | `false` om mDNS uit te zetten (alleen handmatige apparaten). |
| `Players__GoogleCast__ReceiverAppId` | Eigen Cast-receiver, zodat speakers "Augora" tonen in plaats van "Default Media Receiver". Registreer in de [Google Cast SDK Developer Console](https://cast.google.com/publish) een *Styled Media Receiver* met die naam en publiceer hem. |
| `Providers__Spotify__SoloistPreroll` | Stilte die aan het begin van een Soloist-nummer in één keer wordt verstuurd, zodat speakers sneller starten (standaard `00:00:03`; `00:00:00` = uit). |
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
