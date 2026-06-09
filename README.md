# DiffPdf — serverové porovnávání PDF

Server v **C# / .NET 10**, který automaticky porovnává dvě verze sady PDF a řekne ti, **co se změnilo** — slovně, vizuálně i jako pass/fail verdikt. Serverová náhrada desktopového [diffpdf](https://mark-summerfield.github.io/diffpdf.html) navržená pro **regresní testování tiskových sestav** (faktury, reporty).

> 📖 Architektura, kompletní REST API, volby porovnání, SDK a build/testy: **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

---

## Proč

Když přegeneruješ tiskové sestavy (nová verze šablony, knihovny, dat), potřebuješ vědět, jestli se **nerozbilo** něco, co dřív fungovalo. Ruční proklikávání stovek PDF nejde. DiffPdf vezme **referenční** dávku (`old`) a **novou** dávku (`new`), spáruje soubory podle názvu a u každé dvojice najde změněný text, posunuté rozložení, přidané/odebrané stránky, zprázdnělé stránky i chybové hlášky vyrenderované do PDF — a rozdíly **zvýrazní do diff-PDF**. Výsledek je strojově čitelný verdikt, takže z toho jde udělat **CI bránu**.

## Jak to funguje

```
                 ┌─────────────┐   spustí (cron / watch / webhook)
   větev ──▶ instance ──▶ dávka ──────────────────────────────┐
                 └─ old/ new/ │                                ▼
                    reports/  │   durable pipeline po dvojicích (text + vizuál)
                              │   ├─ odolná vůči pádu (retry, zotavení)
                              ▼   └─ paralelní, jeden vadný PDF dávku nezabije
                          výsledek:  verdikt  +  zvýrazněné diff-PDF  +  JSON report
                                     (živý progress přes SignalR)
```

1. **Scope = větev → instance.** Každá instance má `basePath` se složkami `old/`, `new/`, `reports/` (server je založí a opraví). Do `reports/` se jen zapisuje, `old/` a `new/` se jen čtou.
2. **Spouští se automaticky** — ne ručně: **cron rozvrhem**, **sledováním složky** `new/` (spustí se, jakmile se drop souborů ustálí), nebo **webhook triggerem**. V HA prostředí vystřelí dávku jen jedna replika (DB leader-lease).
3. **Porovnání** běží jako **durable pipeline** rozpadlá na jednotlivé dvojice — transientní chyby se opakují, spadlý worker se zotaví, dávka pokračuje.
4. **Výsledek** je verdikt každé dvojice (`Identical` / `Differs` / `OnlyInOld` / `OnlyInNew` / `Error`), zvýrazněné diff-PDF a JSON report; volitelná **CI brána** dělá z dávky pass/fail kontrolu. Po doběhnutí jde **notifikace** (e-mail / Slack / Teams).

## Co to umí

| Oblast | Stručně |
|---|---|
| **Porovnání** | Slovní textový diff (PdfPig) + pixelový vizuální diff (SkiaSharp) s nastavitelnou přísností (`Exact`/`Strict`/`Balanced`/`Lenient`). |
| **Chytrá detekce** | Zarovnání vložených/odebraných stránek (žádná kaskáda falešných rozdílů), prázdné stránky, chybové hlášky v obsahu, ignorování dynamického obsahu (datum, čísla stránek). |
| **Diff-PDF** | Oboustranná dvojstrana (staré vlevo, nové vpravo) — rasterový styl nebo **vektorový overlay** (text zůstává vybíratelný). |
| **Automatizace** | Cron rozvrhy, folder-watch, webhook triggery, fan-out přes celou větev — vše jako **API resources v DB** (CRUD za běhu, bez restartu). |
| **Odolnost** | Per-dvojice pipeline s retry a zotavením; HA single-fire přes leader-lease. |
| **CI brána** | Rozvrh s `gate` → `GET …/result` vrací `200`/`422` (ideální pro `curl --fail`). |
| **Provoz** | `GET /api/v1/status` (leader, ticky služeb, backlog, zdraví závislostí), `/metrics` (Prometheus/OTel), `/health/ready`, retence starých artefaktů. |
| **Klienti** | Typované **.NET SDK** (`DiffPdf.Client`) a **desktop GUI** (Avalonia) s živým sledováním úloh. |
| **Bezpečnost** | Volitelná OAuth2 (OpenIddict, client-credentials → JWT). |

## Rychlý start

```bash
dotnet run --project src/DiffPdf.Api        # http://localhost:5275, auth vypnuto
```

Bez connection stringu běží **jednoinstanční dev režim** (in-memory úložiště, žádná DB). Pro plný stack nastav `ConnectionStrings__SqlServer` — relační zdroj pravdy + **DB-backed durable local queues** (žádný broker); schéma se vytvoří idempotentně při startu. Pro vizuální režim je potřeba **Ghostscript** na `PATH` (nebo `GHOSTSCRIPT_PATH`) — viz [Konfigurace](#konfigurace-ve-zkratce). Kořen artefaktů přepíšeš přes `DIFFPDF_ARTIFACT_ROOT`.

Pak založ větev → instanci → rozvrh a nech to běžet:

```bash
curl -X POST http://localhost:5275/api/v1/branches \
  -d '{"key":"Alfa","name":"Alfa"}' -H 'Content-Type: application/json'

curl -X POST http://localhost:5275/api/v1/branches/Alfa/instances \
  -d '{"key":"LamaEnergy","name":"Lama Energy","basePath":"C:/pdfs/LamaEnergy"}' -H 'Content-Type: application/json'

# cron + porovnávací volby (+ volitelně CI brána) — od teď běží automaticky
curl -X POST http://localhost:5275/api/v1/branches/Alfa/instances/LamaEnergy/schedules \
  -d '{"key":"nightly","cron":"0 2 * * *","options":{"mode":"Both"}}' -H 'Content-Type: application/json'

# spusť teď (mimo rozvrh) → 202 + jobId; pak GET /jobs/{id} a /jobs/{id}/report
curl -X POST http://localhost:5275/api/v1/branches/Alfa/instances/LamaEnergy/schedules/nightly/run
```

Interaktivní **Swagger** je na `/swagger`. Kompletní API: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#rest-api).

### Desktop klient (Avalonia)

Multiplatformní GUI, které přes SDK plně ovládá server a **živě sleduje běžící úlohy**:

```bash
dotnet run --project src/DiffPdf.Api        # server
dotnet run --project src/DiffPdf.DesktopUI  # GUI klient
```

Připojení nastavíš v **ozubeném kolečku (⚙) vpravo nahoře** — URL serveru (a ClientId/Secret, je-li zapnutá autentizace) se **uloží** a klient se příště **připojí sám**. Levé menu: **Přehled · Větve · Instance · Automatizace · Úlohy · Jednorázové porovnání · Notifikace · Sdílené složky · Konfigurace**. Sekce **Úlohy** ukazuje živý progress (SignalR), verdikty a stažení zvýrazněných diff-PDF; synchronizaci složek se scope stromem najdeš ve **Větvích**.

## Konfigurace ve zkratce

Vše přes `appsettings.json` / proměnné prostředí. Detaily a příklady v [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

| Oblast | Výchozí | Poznámka |
|---|---|---|
| **Databáze** | in-memory (dev) | Nastav `ConnectionStrings__SqlServer`. Schéma se vytvoří idempotentně při startu; bez DB jede jednoinstanční dev režim. |
| **Renderer** | Ghostscript (AGPL) | `gs` na `PATH` / `GHOSTSCRIPT_PATH`. Bez něj vizuální režim selže — alternativa je **PDFium** (BSD, in-process): `options.renderer = "Pdfium"`. |
| **Autentizace** | vypnuto | `Auth:Enabled=true` (vyžaduje DB) → každý endpoint chce bearer token (mimo `/health` a OAuth). Token přes client-credentials na `/connect/token`. |
| **Notifikace** | — | SMTP transport + `BaseUrl` v `appsettings`; samotné odběry jsou API resource (`/api/v1/subscriptions`). |
| **Síťové složky** | — | `basePath` může být lokální / UNC (`\\server\share`) / alias `share:<jméno>`; credentialy jako pojmenované profily v sekci `Network`. |
| **Logy / metriky** | konzole + `logs/` | Serilog (rotace 14 dní, dir přes `DIFFPDF_LOG_DIR`); metriky na `/metrics`. |

## Postavené na

| Technologie | Role |
|---|---|
| **.NET 10 / ASP.NET Core Minimal API** | Runtime a štíhlé HTTP API. |
| **PdfPig** | Extrakce textu s pozicemi slov pro slovní diff. |
| **Ghostscript** / **PDFium** | Rendering stránek na obrázky pro pixelový diff. |
| **SkiaSharp** | Porovnání bitmap a detekce prázdných stránek. |
| **PdfSharp** | Generování zvýrazněného diff-PDF (raster i vektor). |
| **SQL Server** + **EF Core** + **Mapperly** | Perzistentní zdroj pravdy; mapování bez reflexe. |
| **Wolverine** | DB-backed durable queues (inbox/outbox, retry, dead-letter) — bez externího brokeru. |
| **SignalR** | Realtime push progressu úloh. |
| **Serilog** · **OpenIddict** | Strukturované logování · OAuth2 server. |

## Dokumentace

Architektura, kompletní REST API reference, volby porovnání, klientské SDK, interní popis pipeline a postup buildu/testů: **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

## Licence

Výchozí renderer **Ghostscript** je pod **AGPL v3** — pro interní/serverové nasazení v pořádku, ale distribuce uzavřeného produktu s Ghostscriptem vyžaduje komerční licenci od Artifexu. Licenčně čistá alternativa je **PDFium** (BSD): nastav `"renderer": "Pdfium"`. Ostatní knihovny: PdfPig (Apache 2.0), PdfSharp (MIT), SkiaSharp (MIT).

