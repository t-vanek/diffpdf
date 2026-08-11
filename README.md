<div align="center">

# DiffPdf

### Vidíš každý rozdíl. Dřív, než ho uvidí zákazník.

**Serverový engine, který automaticky porovná dvě verze tvých PDF a přesně řekne — slovy, obrazem i jasným verdiktem — co se změnilo.**

[![CI](https://github.com/t-vanek/diffpdf/actions/workflows/ci.yml/badge.svg)](https://github.com/t-vanek/diffpdf/actions/workflows/ci.yml)
[![Release](https://github.com/t-vanek/diffpdf/actions/workflows/release.yml/badge.svg)](https://github.com/t-vanek/diffpdf/actions/workflows/release.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows Server](https://img.shields.io/badge/platforma-Windows%20Server-0078D6?logo=windows&logoColor=white)](#nasazení-do-produkce)
[![REST API](https://img.shields.io/badge/API-REST%20%2B%20Swagger-85EA2D?logo=swagger&logoColor=black)](#rest-api)
[![License](https://img.shields.io/badge/licence-vícenásobná-blue)](#licence)

[Úvod](#úvod) · [Funkce](#funkce) · [Technické specifikace](#technické-specifikace) · [Rychlý start](#rychlý-start) · [Desktopový klient](#desktopový-klient) · [Nasazení do produkce](#nasazení-do-produkce)

</div>

---

## Úvod

### Proč DiffPdf vznikl

Je půl třetí ráno. Noční build právě přegeneroval tři sta faktur z nové verze šablony. Vypadají dobře. **Vypadají.** Jenže u jedné z nich se kvůli změně v knihovně posunula částka o řádek níž — a teď sedí v kolonce „k úhradě celkem". Nikdo si toho nevšimne. Až zákazník.

Tohle je noční můra každého, kdo generuje dokumenty ve velkém. Tiskové sestavy, faktury, reporty, smlouvy. Změníš jednu šablonu, povýšíš jednu knihovnu, upravíš jeden dotaz — a najednou nemáš ponětí, jestli se mezi stovkami stránek něco nenápadně nerozbilo. Ruční proklikávání nepřipadá v úvahu. A lidské oko stejně to jedno přesunuté číslo ve tři ráno nenajde.

DiffPdf vznikl přesně proti tomuhle pocitu. Z původního desktopového [diffpdf](https://mark-summerfield.github.io/diffpdf.html) — skvělého nástroje pro porovnání dvou souborů rukou — jsme udělali **server, který to dělá sám, pořád a pro celé dávky najednou.**

### K čemu slouží

DiffPdf vezme **referenční** dávku (`old`) a **novou** dávku (`new`), spáruje soubory podle názvu a u každé dvojice najde:

- **změněný text** — slovo po slovu, s přesnou pozicí na stránce,
- **posunuté rozložení** — vizuální, pixelový rozdíl,
- **přidané a odebrané stránky** — bez kaskády falešných poplachů,
- **zprázdnělé stránky a chybové hlášky** vyrenderované přímo do PDF.

A pak to nejdůležitější: **rozdíly zvýrazní do diff-PDF** (staré vlevo, nové vpravo) a vydá **strojově čitelný verdikt**. Z toho uděláš **bránu ve své CI** — build, který vygeneruje rozbitou sestavu, prostě neprojde.

> Žádné klikání. Žádné domněnky. Jen jasná odpověď na otázku, kterou si klade každý: **„Změnilo se něco, co se změnit nemělo?"**

### Jak to funguje

```
                 ┌─────────────┐   spustí automatizace (cron / interval / událost) nebo ruční trigger
   větev ──▶ instance ──▶ dávka ──────────────────────────────────────────┐
                 └─ old/ new/ │                                            ▼
                    reports/  │   durable pipeline po dvojicích (text + vizuál)
                              │   ├─ odolná vůči pádu (retry, zotavení)
                              ▼   └─ paralelní, jeden vadný PDF dávku nezabije
                          výsledek:  verdikt  +  zvýrazněné diff-PDF  +  JSON report
                                     (živý progress přes SignalR + centrum notifikací)
```

1. **Scope = větev → instance.** Každá instance má `basePath` se složkami `old/`, `new/`, `reports/` (server je založí a opraví). Do `reports/` se jen zapisuje, `old/` a `new/` se jen čtou.
2. **Spouští se automaticky** — ne ručně: **automatizací** (cron rozvrh, pevný interval, nebo doménová událost) anebo **on-demand triggerem** přes API/klienta. V HA prostředí vystřelí dávku jen jedna replika (DB leader-lease).
3. **Porovnání** běží jako **durable pipeline** rozpadlá na jednotlivé dvojice — transientní chyby se opakují, spadlý worker se zotaví, dávka pokračuje.
4. **Výsledek** je verdikt každé dvojice (`Identical` / `Differs` / `OnlyInOld` / `OnlyInNew` / `Error`), zvýrazněné diff-PDF a JSON report; volitelná **CI brána** dělá z dávky pass/fail kontrolu. Po doběhnutí jde **e-mailová notifikace** a událost se zapíše do trvalého **centra notifikací**.

---

## Funkce

Pár věcí, kvůli kterým si DiffPdf zamiluješ.

### 🔍 Vidí to, co oko přehlédne
Slovní textový diff (PdfPig) najde každé změněné slovo i jeho pozici. Pixelový vizuální diff (SkiaSharp) zachytí posun o pár bodů, který v tabulce čísel nikdo nerozezná. Přísnost si nastavíš od `Exact` po `Lenient`.

### 🧠 Nepláče zbytečně
Vložila se nová stránka? DiffPdf ji pozná (zarovnání Needleman–Wunsch) a **nezahltí tě stovkou falešných rozdílů** na všech následujících stránkách. Dynamický obsah — datum, číslo stránky — umí ignorovat. Prázdné stránky a chybové hlášky v obsahu naopak aktivně hledá.

### 🖍️ Rozdíl, na který si ukážeš prstem
Výsledkem není tabulka souřadnic, ale **diff-PDF**: dvojstrana se starým vlevo a novým vpravo, rozdíly zvýrazněné. V rasterovém stylu, nebo jako **vektorový overlay** — text zůstane vybíratelný.

### ⚙️ Postavené na automatizaci, ne na klikání
Automatizace s kategoriemi a šablonami (monitorovací, provozní, údržbové, synchronizační), cron i intervalové rozvrhy, spouštění na události, fan-out přes celou větev — a všechno jako **API resource v databázi**. Měníš za běhu, **bez jediného restartu**.

### 🛡️ Nepoloží to jeden vadný soubor
Per-dvojice pipeline s retry a automatickým zotavením. Spadne worker? Dávka pokračuje. Běží víc replik? Díky leader-lease vystřelí dávku jen jedna.

### 🔔 Žádná událost se neztratí
Doběhlé porovnání, běh automatizace, nedoručený e-mail i zotavení po pádu se zapisují do **trvalého event logu**. Klient má **centrum notifikací** se zvonečkem — a po výpadku spojení si chybějící události **dotáhne zpětně** (replay přes kurzor), takže nikdy nepřijdeš o alert. E-maily jdou přes **outbox** s opakováním, dead-letterem a viditelnou historií doručení.

### 🚦 Pass/fail přímo v tvém pipeline
Automatizace s `gate` → `GET …/jobs/{id}/result` vrátí `200`, nebo `422`. Ideální pro `curl --fail`. Rozbitý build neprojde — tečka.

### 🖥️ Server i desktop, jak ti to vyhovuje
Typované **.NET SDK** (`DiffPdf.Client`) pro integraci a **desktopové GUI** (Avalonia) s živým sledováním úloh přes SignalR.

---

## Technické specifikace

| Oblast | Specifikace |
|---|---|
| **Runtime** | .NET 10 / ASP.NET Core Minimal API |
| **Jazyk** | C# |
| **Architektura** | Čistá vrstvená (hexagonální), 12 projektů |
| **Cílová platforma** | Windows Server (nasazení jako Windows služba); desktopový klient pro Windows |
| **API** | REST (`/api/v1`) + interaktivní Swagger; realtime push přes SignalR |
| **Textový engine** | PdfPig — extrakce slov s bounding boxy |
| **Vizuální engine** | Ghostscript / PDFium render → SkiaSharp pixel diff a blank detekce |
| **Diff-PDF** | PdfSharp — raster i vektorový overlay |
| **Perzistence** | SQL Server + EF Core + Mapperly (mapování bez reflexe) |
| **Durable queues** | Wolverine — DB-backed inbox/outbox, retry, dead-letter (bez externího brokeru) |
| **Automatizace** | Cron / interval / událostmi spouštěné automatizace; HA single-fire přes leader-lease |
| **Bezpečnost** | Volitelná OAuth2 (OpenIddict, client-credentials → JWT) |
| **Observabilita** | Serilog · `/metrics` (Prometheus/OTel) · `/health/ready` · `GET /api/v1/status` · trvalý event log |
| **Notifikace** | E-mail (SMTP) jako API resource — outbox s retry, dead-letter a historií doručení |
| **Klienti** | Typované .NET SDK (`DiffPdf.Client`) · desktopové GUI (Avalonia) |

### Postaveno na

| Technologie | Role |
|---|---|
| **.NET 10 / ASP.NET Core Minimal API** | Runtime a štíhlé HTTP API. |
| **PdfPig** | Extrakce textu s pozicemi slov pro slovní diff. |
| **Ghostscript** / **PDFium** | Rendering stránek na obrázky pro pixelový diff. |
| **SkiaSharp** | Porovnání bitmap a detekce prázdných stránek. |
| **PdfSharp** | Generování zvýrazněného diff-PDF (raster i vektor). |
| **SQL Server** + **EF Core** + **Mapperly** | Perzistentní zdroj pravdy; mapování bez reflexe. |
| **Wolverine** | DB-backed durable queues (inbox/outbox, retry, dead-letter) — bez externího brokeru. |
| **SignalR** | Realtime push progressu úloh a systémových událostí. |
| **Serilog** · **OpenIddict** | Strukturované logování · OAuth2 server. |

---

## Rychlý start

> Pro **produkční nasazení** (Windows služba, SQL Server, service účet, firewall) následuj **[návod pro ICT → docs/NASAZENI.md](docs/NASAZENI.md)**. Níže je rychlý lokální start pro vyzkoušení.

### Předpoklady

- **[.NET 10 SDK](https://dotnet.microsoft.com/)** (pro build ze zdrojů)
- **[Ghostscript](https://www.ghostscript.com/)** na `PATH` (nebo `GHOSTSCRIPT_PATH`) — jen pro vizuální režim; alternativa je in-process **PDFium**
- **SQL Server** — volitelně, pro plný produkční stack (bez něj jede dev režim)

### Spuštění za 30 vteřin

```powershell
git clone https://github.com/t-vanek/diffpdf.git
cd diffpdf
dotnet run --project src/DiffPdf.Api        # http://localhost:5275, auth vypnuto
```

Bez connection stringu běží **jednoinstanční dev režim** (in-memory úložiště, žádná DB). Pro plný stack nastav `ConnectionStrings__SqlServer` — relační zdroj pravdy + **DB-backed durable local queues** (žádný broker); schéma se vytvoří idempotentně migracemi při startu. Kořen artefaktů přepíšeš přes `DIFFPDF_STORAGE_ROOT`.

### První porovnání

Založ větev → instanci a spusť dávku triggerem. Pro automatický rozvrh založ **automatizaci** typu „Plánované porovnání".

```powershell
# 1. scope: větev + instance (basePath se odvodí ze ScopeSync:RootPath, nebo zadej vlastní)
curl -X POST http://localhost:5275/api/v1/branches `
  -d '{"key":"Alfa","name":"Alfa"}' -H 'Content-Type: application/json'

curl -X POST http://localhost:5275/api/v1/branches/Alfa/instances `
  -d '{"key":"LamaEnergy","name":"Lama Energy"}' -H 'Content-Type: application/json'

# 2. spusť dávku teď → 202 + jobId (200 když není co porovnávat / nedostupné)
curl -X POST http://localhost:5275/api/v1/triggers/Alfa/LamaEnergy

# 3. stav úlohy a report
curl http://localhost:5275/api/v1/jobs/<jobId>
curl http://localhost:5275/api/v1/jobs/<jobId>/report
```

Pro pravidelné běhy přidej automatizaci s krokem `ScheduledComparison` (cron / interval) — nejjednodušeji z **galerie šablon** v desktopovém klientovi (sekce **Automatizace**), nebo přes `POST /api/v1/automations`.

Interaktivní **Swagger** je na `/swagger` — kompletní API reference včetně schémat všech requestů a voleb porovnání.

---

## Desktopový klient

GUI nad SDK, které plně ovládá server a **živě sleduje běžící úlohy** — pro testera i provoz.

```powershell
dotnet run --project src/DiffPdf.Api        # server
dotnet run --project src/DiffPdf.DesktopUI  # GUI klient
```

> Desktopový klient je ze solution buildu vyloučený — `dotnet build DiffPdf.slnx` ho přeskočí; build/běh přes jeho `.csproj`. V produkci se distribuuje jako self-contained `.exe` (viz [Release artefakty](docs/NASAZENI.md#release-artefakty)).

Připojení nastavíš v **ozubeném kolečku (⚙) vpravo nahoře** — URL serveru (a ClientId/Secret, je-li zapnutá autentizace) se **uloží** a klient se příště **připojí sám** (nebo si server najde sám přes LAN discovery). Vedle kolečka je **zvoneček centra notifikací**.

Levé menu: **Přehled · Větve · Instance · Automatizace · Úlohy · Jednorázové porovnání · Notifikace · Správa souborů · Konfigurace**. Vybrané schopnosti:

- **Úlohy** — živý progress (SignalR), verdikty, stažení zvýrazněných diff-PDF a jejich **odeslání e-mailem** (jednotlivý soubor z detailu dvojice, nebo tlačítkem **„Odeslat odlišné"** celá dávka; početné přílohy se sbalí do ZIP); ⓘ u selhané úlohy ukáže důvod.
- **Automatizace** — galerie šablon, jednoduchý editor (klíč se generuje z názvu, technikálie sbalené v *Pokročilém nastavení*), **náhled příštích spuštění**, shrnutí věty a **přepínač ztlumení notifikací** per automatizace.
- **Konfigurace → E-mail** — SMTP nastavení, test odeslání a **historie doručení** (filtr „jen problémy", tlačítko „Poslat znovu").
- **Centrum notifikací (zvoneček)** — trvalý feed událostí; po výpadku spojení dotáhne zmeškané.

---

## Konfigurace ve zkratce

Vše přes `appsettings.json` / proměnné prostředí (`__` odděluje sekce, např. `ConnectionStrings__SqlServer`). Plný popis pro provoz v **[docs/NASAZENI.md](docs/NASAZENI.md#konfigurace)**.

| Oblast | Výchozí | Poznámka |
|---|---|---|
| **Databáze** | in-memory (dev) | `ConnectionStrings__SqlServer`. Schéma se vytvoří migracemi při startu; bez DB jede jednoinstanční dev režim. |
| **Bind / port** | `http://0.0.0.0:5275` | `ASPNETCORE_URLS`. Služba binduje všechna rozhraní, ať klienti v LAN dosáhnou. |
| **Renderer** | Ghostscript (AGPL) | `gs` na `PATH` / `GHOSTSCRIPT_PATH`. Alternativa je **PDFium** (BSD, in-process): volba `renderer = "Pdfium"`. |
| **Úložiště** | `storage` (rel.) | Kořen složek `old/new/reports`; `DIFFPDF_STORAGE_ROOT` nebo `ScopeSync:RootPath`. |
| **Autentizace** | vypnuto | `Auth:Enabled=true` (vyžaduje DB) → každý endpoint chce bearer token (mimo `/health` a OAuth). |
| **E-mail** | — | SMTP a odběry jsou **runtime resource** (`/api/v1/settings/email`, `/api/v1/subscriptions`); fallback `Notifications:Smtp` v appsettings. |
| **Síťové složky** | — | `basePath` lokální / UNC (`\\server\share`) / alias `share:<jméno>`; credentialy jako profily v sekci `Network`. |
| **Logy / metriky** | konzole + soubor | Serilog (v produkci rotace do `C:\ProgramData\DiffPdf\logs`); metriky na `/metrics`. |

---

## REST API

Všechny aplikační cesty jsou pod prefixem **`/api/v1`**. OpenAPI dokument je na `/openapi/v1.json`, interaktivní **Swagger UI** na `/swagger`. Chyby se vrací jako **`application/problem+json`** (RFC 9457).

Hlavní oblasti (kompletní reference se schématy je ve Swaggeru):

| Oblast | Cesty | Účel |
|---|---|---|
| **Health** | `GET /health`, `/health/ready` | Liveness (vždy `200`) a readiness (`200`/`503`, kontrola DB/rendereru/úložiště). |
| **Větve / instance** | `…/branches`, `…/instances`, `…/structure`, `…/readiness` | Scope, provisioning složek, kontrola připravenosti. |
| **Triggery** | `POST …/triggers/{branch}/{instance}`, `…/branches/{branch}/run` | On-demand spuštění jedné instance / fan-out přes větev. |
| **Automatizace** | `…/automations`, `…/automations/{id}/run`, `…/notifications/enable\|disable` | CRUD, spuštění teď, ztlumení notifikací; katalog kroků a šablony. |
| **Úlohy** | `…/jobs`, `…/jobs/{id}/report\|result\|tasks\|artifacts`, `…/cancel\|pause\|resume\|retry\|send` | Stav, report, CI verdikt, akce, odeslání diff-PDF e-mailem. |
| **Notifikace** | `…/subscriptions`, `…/settings/email`, `…/notifications/deliveries` | Odběry, SMTP nastavení, historie doručení + re-send. |
| **Události** | `GET …/events?sinceSeq=`, SignalR `systemEvent` | Trvalý event log s replayem (centrum notifikací). |
| **Provoz** | `GET …/status`, `/metrics` | Leader, backlog, heartbeaty služeb; Prometheus metriky. |
| **Realtime** | SignalR `/hubs/jobs` | Push progressu úloh, stavu fronty a systémových událostí. |

### Klientské SDK (.NET)

Balíček **`DiffPdf.Client`** je typovaný .NET klient pokrývající celý flow (větve, instance, automatizace, úlohy, notifikace, události). Je **self-contained** (vlastní modely, žádná závislost na server projektech).

```csharp
using DiffPdf.Client;

// registrace (bez auth):
services.AddDiffPdfClient(new Uri("http://localhost:5275"));
// nebo s M2M tokenem (OpenIddict client-credentials):
services.AddDiffPdfClient(new Uri("https://…"), "diffpdf-ci", "secret", "diffpdf.api");

// použití (DiffPdfClient injectnutý z DI):
await diff.CreateBranchAsync(new() { Key = "Alfa", Name = "Alfa" });
await diff.CreateInstanceAsync("Alfa", new() { Key = "LamaEnergy", Name = "Lama Energy" });

// spusť dávku teď a počkej na report:
var result = await diff.TriggerBatchAsync("Alfa", "LamaEnergy");
var report = await diff.WaitForReportAsync(result.JobId!.Value);
Console.WriteLine($"{report.Differing}/{report.Total} se liší");
```

Non-2xx odpovědi vyhodí `DiffPdfApiException` (s HTTP statusem a `detail` z problem+json). Enumy mají tolerantní deserializaci — novější stav ze serveru starší klient neshodí.

---

## Nasazení do produkce

Server běží jako **Windows služba** (Api hostuje workery in-process; migrace se aplikují při startu). Klient je **self-contained** desktop appka — tester jen rozbalí a spustí (bez .NET runtime).

**Kompletní runbook pro ICT:** **[docs/NASAZENI.md](docs/NASAZENI.md)** — požadavky, příprava SQL Serveru a service účtu, instalace, konfigurace, ověření, autentizace, monitoring, zálohy, aktualizace s rollbackem a řešení potíží.

### Ve zkratce

```powershell
# 1. publish (na buildovacím stroji s .NET 10 SDK)
.\deploy\publish.ps1 -Version 1.2.3          # → publish/DiffPdf-Server-1.2.3-win-x64.zip
.\deploy\publish.ps1 -Version 1.2.3 -ClientOnly # → publish/DiffPdf-Client-1.2.3-win-x64.zip

# 2. instalace služby (na serveru, elevated PowerShell, z rozbaleného zipu)
.\install-service.ps1 -BinPath 'C:\DiffPdf\app\DiffPdf.Api.exe' `
    -ConnectionString 'Server=.;Database=diffpdf;Trusted_Connection=True;TrustServerCertificate=True'

# 3. aktualizace (bezpečná, s rollbackem při nenaběhnutí)
.\update-service.ps1 -InstallDir 'C:\DiffPdf\app' -Source '.\DiffPdf-Server-1.2.3-win-x64.zip'
```

Tag `v*` v Gitu spustí `release.yml`, který vytvoří GitHub Release se serverovým i klientským zipem.
Ručně spuštěné workflow **Server Bundle** a **Client Bundle** také vytvoří položku v GitHub Releases.

---

## Licence

Výchozí renderer **Ghostscript** je pod **AGPL v3** — pro interní/serverové nasazení v pořádku, ale distribuce uzavřeného produktu s Ghostscriptem vyžaduje komerční licenci od Artifexu. Licenčně čistá alternativa je **PDFium** (BSD): nastav volbu `"renderer": "Pdfium"`. Ostatní knihovny: PdfPig (Apache 2.0), PdfSharp (MIT), SkiaSharp (MIT).
