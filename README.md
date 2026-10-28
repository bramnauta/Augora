# AudioCentral

Self-hosted muziekserver: bronnen (TuneIn, later Spotify, lokale
bestanden, podcasts, Nextory) afspelen op Google Cast-apparaten en -groepen, later ook op Android
en in de browser. Zie [docs/architecture.md](docs/architecture.md) voor ontwerp en roadmap.

## Status

- ✅ TuneIn: bladeren, zoeken, afspelen (radio en podcast-afleveringen), optioneel favorieten via `Providers:TuneIn:Username`
- ✅ Google Cast: mDNS-discovery, handmatig toevoegen (andere VLAN's), groepen, volume/pauze/stop
- ✅ Wachtrij per speler (bewaard in SQLite), realtime updates via SignalR
- ✅ Blazor WebAssembly-UI (Radzen), Nederlands en Engels
- ⏳ Auth, browser-speler, Spotify, lokale bestanden, podcasts, Android, Home Assistant, Nextory

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
| `Players__GoogleCast__Discovery` | `false` om mDNS uit te zetten (alleen handmatige apparaten). |

## API

REST onder `/api` (OpenAPI: `/openapi/v1.json`, Scalar-UI op `/scalar` in development) en een
SignalR-hub op `/hubs/events`. Fouten zijn Problem Details met een `code`-veld.
