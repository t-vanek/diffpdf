<div align="center">

# DiffPdf

### Vidíš každý rozdíl. Dřív, než ho uvidí zákazník.

**Serverový engine, který automaticky porovná dvě verze tvých PDF a přesně řekne — slovy, obrazem i jasným verdiktem — co se změnilo.**

[![CI](https://github.com/t-vanek/diffpdf/actions/workflows/ci.yml/badge.svg)](https://github.com/t-vanek/diffpdf/actions/workflows/ci.yml)
[![Release](https://github.com/t-vanek/diffpdf/actions/workflows/release.yml/badge.svg)](https://github.com/t-vanek/diffpdf/actions/workflows/release.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-13-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Platform](https://img.shields.io/badge/platforma-Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-informational)](#instalace)
[![REST API](https://img.shields.io/badge/API-REST%20%2B%20Swagger-85EA2D?logo=swagger&logoColor=black)](#instalace)
[![License](https://img.shields.io/badge/licence-vícenásobná-blue)](#licence)

[Úvod](#úvod) · [Funkce](#funkce) · [Technické specifikace](#technické-specifikace) · [Instalace](#instalace) · [Dokumentace](#dokumentace)

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
Cron rozvrhy, hlídání složek, webhook triggery, fan-out přes celou větev — a všechno jako **API resource v databázi**. Měníš za běhu, **bez jediného restartu**.

### 🛡️ Nepoloží to jeden vadný soubor
Per-dvojice pipeline s retry a automatickým zotavením. Spadne worker? Dávka pokračuje. Běží víc replik? Díky leader-lease vystřelí dávku jen jedna.

### 🚦 Pass/fail přímo v tvém pipeline
Rozvrh s `gate` → `GET …/result` vrátí `200`, nebo `422`. Ideální pro `curl --fail`. Rozbitý build neprojde — tečka.

### 🖥️ Server i desktop, jak ti to vyhovuje
Typované **.NET SDK** (`DiffPdf.Client`) pro integraci a multiplatformní **desktopové GUI** (Avalonia) s živým sledováním úloh přes SignalR.

---

## Technické specifikace

| Oblast | Specifikace |
|---|---|
| **Runtime** | .NET 10 / ASP.NET Core Minimal API |
| **Jazyk** | C# |
| **Architektura** | Čistá vrstvená (hexagonální), 9 projektů — viz [DEVELOPMENT.md](docs/DEVELOPMENT.md) |
| **Platformy** | Windows · Linux · macOS |
| **API** | REST (`/api/v1`) + interaktivní Swagger; realtime push přes SignalR |
| **Textový engine** | PdfPig — extrakce slov s bounding boxy |
| **Vizuální engine** | Ghostscript / PDFium render → SkiaSharp pixel diff a blank detekce |
| **Diff-PDF** | PdfSharp — raster i vektorový overlay |
| **Perzistence** | SQL Server + EF Core + Mapperly (mapování bez reflexe) |
| **Durable queues** | Wolverine — DB-backed inbox/outbox, retry, dead-letter (bez externího brokeru) |
| **Plánování** | Cron rozvrhy, folder-watch, webhook triggery; HA single-fire přes leader-lease |
| **Bezpečnost** | Volitelná OAuth2 (OpenIddict, client-credentials → JWT) |
| **Observabilita** | Serilog · `/metrics` (Prometheus/OTel) · `/health/ready` · `GET /api/v1/status` |
| **Notifikace** | SMTP / webhook (e-mail, Slack, Teams) jako API resource |
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
| **SignalR** | Realtime push progressu úloh. |
| **Serilog** · **OpenIddict** | Strukturované logování · OAuth2 server. |

---

## Instalace

### Předpoklady

- **[.NET 10 SDK](https://dotnet.microsoft.com/)** (povinné)
- **[Ghostscript](https://www.ghostscript.com/)** na `PATH` (nebo `GHOSTSCRIPT_PATH`) — jen pro vizuální režim; alternativa je in-process **PDFium**
- **SQL Server** — volitelně, pro plný produkční stack (bez něj jede dev režim)

### Spuštění za 30 vteřin

```bash
git clone https://github.com/t-vanek/diffpdf.git
cd diffpdf
dotnet run --project src/DiffPdf.Api        # http://localhost:5275, auth vypnuto
```

Bez connection stringu běží **jednoinstanční dev režim** (in-memory úložiště, žádná DB). Pro plný stack nastav `ConnectionStrings__SqlServer` — relační zdroj pravdy + **DB-backed durable local queues** (žádný broker); schéma se vytvoří idempotentně při startu. Kořen artefaktů přepíšeš přes `DIFFPDF_ARTIFACT_ROOT`.

### První porovnání

Založ větev → instanci → rozvrh a nech to běžet:

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

### Desktopový klient (Avalonia)

Multiplatformní GUI, které přes SDK plně ovládá server a **živě sleduje běžící úlohy**:

```bash
dotnet run --project src/DiffPdf.Api        # server
dotnet run --project src/DiffPdf.DesktopUI  # GUI klient
```

Připojení nastavíš v **ozubeném kolečku (⚙) vpravo nahoře** — URL serveru (a ClientId/Secret, je-li zapnutá autentizace) se **uloží** a klient se příště **připojí sám**. Levé menu: **Přehled · Větve · Instance · Automatizace · Úlohy · Jednorázové porovnání · Notifikace · Sdílené složky · Konfigurace**. Sekce **Úlohy** ukazuje živý progress (SignalR), verdikty, stažení zvýrazněných diff-PDF a jejich **odeslání e-mailem** — jednotlivý soubor z detailu dvojice, nebo tlačítkem **„Odeslat odlišné"** celou dávku najednou (větší množství se sbalí do ZIP); synchronizaci složek se scope stromem najdeš ve **Větvích**.

### Konfigurace ve zkratce

Vše přes `appsettings.json` / proměnné prostředí. Detaily a příklady v [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

| Oblast | Výchozí | Poznámka |
|---|---|---|
| **Databáze** | in-memory (dev) | Nastav `ConnectionStrings__SqlServer`. Schéma se vytvoří idempotentně při startu; bez DB jede jednoinstanční dev režim. |
| **Renderer** | Ghostscript (AGPL) | `gs` na `PATH` / `GHOSTSCRIPT_PATH`. Bez něj vizuální režim selže — alternativa je **PDFium** (BSD, in-process): `options.renderer = "Pdfium"`. |
| **Autentizace** | vypnuto | `Auth:Enabled=true` (vyžaduje DB) → každý endpoint chce bearer token (mimo `/health` a OAuth). Token přes client-credentials na `/connect/token`. |
| **Notifikace** | — | SMTP transport + `BaseUrl` v `appsettings`; samotné odběry jsou API resource (`/api/v1/subscriptions`). |
| **Síťové složky** | — | `basePath` může být lokální / UNC (`\\server\share`) / alias `share:<jméno>`; credentialy jako pojmenované profily v sekci `Network`. |
| **Logy / metriky** | konzole + `logs/` | Serilog (rotace 14 dní, dir přes `DIFFPDF_LOG_DIR`); metriky na `/metrics`. |

---

## Dokumentace

Architektura, kompletní REST API reference, volby porovnání, klientské SDK, interní popis pipeline a postup buildu/testů: **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

---

## Licence

Výchozí renderer **Ghostscript** je pod **AGPL v3** — pro interní/serverové nasazení v pořádku, ale distribuce uzavřeného produktu s Ghostscriptem vyžaduje komerční licenci od Artifexu. Licenčně čistá alternativa je **PDFium** (BSD): nastav `"renderer": "Pdfium"`. Ostatní knihovny: PdfPig (Apache 2.0), PdfSharp (MIT), SkiaSharp (MIT).
