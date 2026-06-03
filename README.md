# diffpdf — serverové porovnávání PDF

Serverová náhrada desktopového [diffpdf](https://mark-summerfield.github.io/diffpdf.html),
postavená v **C# / .NET 10** jako REST API pro **hromadné porovnávání PDF** (složka
`old` vs složka `new`) i jednotlivých dvojic.

**Hlavní use-case — regresní testování tiskových sestav.** QA porovnává čerstvě
vygenerovanou dávku reportů (`new`) proti známé dobré referenci (`old`) a ověřuje, že
nová verze nerozbila stávající tisky. Engine cílí přesně na tyhle regrese: změněný
obsah, rozbité rozložení, prázdné stránky a chybové hlášky vyrenderované přímo do PDF.

Porovnání se **nespouští ručně** — jede automaticky: každá instance má své **rozvrhy**
(cron) a **notifikační odběry**, spravované za běhu přes API a uložené v databázi.
Klient tedy obsluhuje jen automatizaci a sleduje výsledky.

> 📖 Vývojářská dokumentace (architektura, kompletní REST API, SDK, build & testy) je
> v **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

## Funkce

- **Textové porovnání** — slovní diff s pozičním zvýrazněním (PdfPig).
- **Pixelové vizuální porovnání** — diff po pixelech s nastavitelnou tolerancí (až po
  přesnou shodu) a granularitou shluků (až po jednotlivý pixel); ve výchozím stavu
  Ghostscript, PDFium jako záloha.
- **Nastavitelná přísnost** — preset `Exact` / `Strict` / `Balanced` / `Lenient` řídí
  prahy pro hlášení rozdílů; každý práh lze i přepsat jednotlivě.
- **Zarovnání stránek** — vložené/odebrané stránky se detekují (Needleman–Wunsch nad
  podobností textu), takže nezpůsobí kaskádu falešných rozdílů.
- **Typovaná klasifikace stránek** — štítky `TextChanged`, `VisualChanged`, `PageAdded`,
  `PageRemoved`, `SizeChanged`, `BecameBlank`, `WasBlank`.
- **Detekce prázdných stránek** — hlásí stránky, které se staly (nebo přestaly být)
  prázdné; zkoumá pixely, takže funguje i na skeny.
- **Detekce chybových hlášek v obsahu** — prohledá text na chyby vyrenderované do PDF
  (např. `subreport error`, `#error`); vzory jsou konfigurovatelné.
- **Ignorování dynamického obsahu** — vyloučí oblasti (datum/čas v patičce, čísla
  stránek, vodoznaky) a/nebo textové vzory z diffu, aby legitimně se měnící obsah
  neflagoval každý report.
- **Robustní ošetření chyb** — poškozené, šifrované, chybějící nebo prázdné PDF se
  ohlásí jako `Error` s důvodem místo pádu celé dávky.
- **Oboustranné zvýrazněné diff-PDF** — dvojstrana se starou stránkou (vlevo, odebrané
  červeně) vedle nové (vpravo, přidané zeleně, vizuální změny oranžově). Na výběr je
  rasterový styl nebo **vektorový overlay** (zvýraznění nad originálem, text zůstává
  vybíratelný).
- **Hromadné porovnání složek** — páruje soubory podle relativní cesty, běží paralelně,
  klasifikuje každou dvojici jako `Identical` / `Differs` / `OnlyInOld` / `OnlyInNew` /
  `Error`.
- **Větve a instance** — scope je hierarchie **větev → instance**; instance nese
  `basePath` s podsložkami `old` / `new` / `reports`, které server **založí a opraví**.
- **Readiness (pre-flight)** — jedním voláním stav složek, počty PDF a spárování
  `old`/`new` s verdiktem `ready`, takže prázdná nebo nekompletní dávka se zachytí dřív,
  než se spustí.
- **Automatizace (runtime resources)** — dávky se spouští **jen** automaticky: periodicky
  podle **cron rozvrhu**, nebo akcí **„spusť teď"** nad rozvrhem. Každý rozvrh nese
  vlastní porovnávací volby a CI bránu. Po doběhnutí se rozešle **notifikace** (webhook
  Slack/Teams nebo e-mail) při `Completed` / `GateViolated`. Rozvrhy i odběry jsou
  plnohodnotné **API resources v DB** (CRUD za běhu, bez restartu).
- **Durable pipeline** — dávka se rozpadne na jednotlivé dvojice; jeden poškozený PDF
  dávku nezabije, transientní chyby se opakují a spadlý worker se zotaví, takže dávka
  pokračuje místo zaseknutí.
- **CI brána** — rozvrh s `gate` se stane pass/fail kontrolou; `GET …/result` vrací
  `200`/`422` (ideální pro `curl --fail` v pipeline).
- **Síťové složky** — porovnání lokálních, namountovaných nebo UNC (`\\server\share`)
  složek; credentialy a sdílení se konfigurují centrálně (pojmenované profily + aliasy).
- **Klientské SDK (.NET)** — typovaný `HttpClient` klient (`DiffPdf.Client`, balitelný
  jako NuGet) pokrývající celý flow vč. správy rozvrhů a odběrů.
- **Volitelná OAuth2 autentizace** — vestavěný OpenIddict server s client-credentials
  (M2M) flow vydávajícím JWT bearer tokeny.

## Použité technologie

| Technologie | Role |
|---|---|
| **.NET 10 / ASP.NET Core Minimal API** | Moderní výkonný runtime a štíhlé HTTP API. |
| **PdfPig** | Extrakce textu s pozicemi slov pro slovní diff. |
| **Ghostscript** / **PDFium** | Rendering stránek na obrázky pro pixelové porovnání. |
| **SkiaSharp** | Rychlé porovnání bitmap a detekce prázdných stránek. |
| **PdfSharp** | Generování zvýrazněného diff-PDF (raster i vektorový overlay). |
| **PostgreSQL / SQL Server** | Perzistentní zdroj pravdy o stavu úloh (volitelný provider). |
| **EF Core** (Npgsql / Microsoft.Data.SqlClient) | Typovaný přístup k DB s optimistic concurrency. |
| **Mapperly** | Source-generated mapování entit na doménové modely bez reflexe. |
| **RabbitMQ** | Distribuovaný transport práce mezi API a workery. |
| **Wolverine** | Orchestrace zpráv s durable inbox/outbox, retry a dead-letterem. |
| **SignalR** | Realtime push progressu úloh ke klientům. |
| **Serilog** | Strukturované logování do konzole i rotovaného souboru. |
| **OpenIddict** | OAuth2 server pro bezpečný přístup strojových klientů. |

## Nastavení a spuštění

### Rychlý start (Docker, doporučeno)

```bash
docker compose up --build
# Spustí PostgreSQL + RabbitMQ + API na http://localhost:8080.
# Vstup je složka instance ./samples/LamaEnergy (old/ vs new/); reports se píší tamtéž.
```

Image instaluje Ghostscript a nativní závislosti pro SkiaSharp/PDFium; compose nastaví
`ConnectionStrings__Postgres` / `__RabbitMq` a `Storage__RootPath`. Pak stačí vytvořit
větev + instanci + rozvrh:

```bash
# větev + instance (instance nese basePath se složkami old/new/reports)
curl -X POST http://localhost:8080/api/v1/branches \
  -d '{"key":"Alfa","name":"Alfa"}' -H 'Content-Type: application/json'
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances \
  -d '{"key":"LamaEnergy","name":"Lama Energy","basePath":"/pdfs/LamaEnergy"}' -H 'Content-Type: application/json'

# rozvrh (cron + volby + volitelně CI brána) — od teď běží automaticky
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/schedules \
  -d '{"key":"nightly","cron":"0 2 * * *","options":{"mode":"Both"}}' -H 'Content-Type: application/json'

# spusť teď (mimo rozvrh) -> 202 + jobId, pak polling /jobs/{id} a /jobs/{id}/report
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/schedules/nightly/run
```

Interaktivní **Swagger UI** je na `/swagger`. Kompletní popis endpointů viz
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#rest-api).

### Struktura složek instance

Každá instance nese **základní cestu** (`basePath`); vstup i výstup jsou její podsložky:

```
{basePath}/old                  # vstupní PDF (reference)
{basePath}/new                  # vstupní PDF (nová verze)
{basePath}/reports/{jobId}/...  # výstup běhu: diff-PDF + JSON report + logy
```

Aplikace zapisuje **jen** do `reports/`; `old/` a `new/` pouze čte. Strukturu při startu
i při zakládání instance založí/opraví automaticky.

### Volba databáze (PostgreSQL nebo SQL Server)

Zdroj pravdy běží na **PostgreSQL** nebo **Microsoft SQL Serveru** — vybírá se podle
connection stringu (SQL Server má přednost, je-li nastaven):

```
ConnectionStrings__SqlServer: Server=sqlserver,1433;Database=diffpdf;User Id=sa;Password=…;TrustServerCertificate=True
# jinak:
ConnectionStrings__Postgres:  Host=postgres;Port=5432;Database=diffpdf;Username=diffpdf;Password=diffpdf
ConnectionStrings__RabbitMq:  amqp://diffpdf:diffpdf@rabbitmq:5672/
```

Schéma se vytvoří idempotentně při startu. **Bez** DB + RabbitMQ spadne API zpět na
in-memory úložiště a in-process transport (jednoinstanční dev režim).

### Síťové složky a credentialy

`basePath` instance může být lokální cesta, namountované sdílení, UNC cesta
(`\\server\share\...`) nebo **pojmenovaný alias** sdílení (`share:<jméno>`). Credentialy
se na instanci **neukládají** — instance jen odkáže na **credential profil**; heslo
zůstává v konfiguraci. Sdílení a profily se definují jednou v sekci `Network`:

```jsonc
"Network": {
  "MountReadOnly": false,            // Linux: false — do reports/ se zapisuje
  "CredentialProfiles": {
    "corp": { "username": "svc_diff", "password": "…", "domain": "CORP" }
  },
  "Shares": {
    "lama": { "root": "\\\\fileserver\\reports\\LamaEnergy", "credentialProfile": "corp" }
  }
}
```

Instance pak odkáže na alias / profil (`"basePath": "share:lama", "credentialProfile": "corp"`).
Windows připojuje přes `WNetAddConnection2`, Linux přes CIFS mount (vyžaduje `cifs-utils`
a `--cap-add SYS_ADMIN`). Detaily viz [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#síťové-složky-a-credentialy).

### Notifikace (SMTP transport)

Notifikační **odběry** se spravují za běhu přes API (`/api/v1/subscriptions`). V
`appsettings.json` zůstává jen e-mailový transport a veřejná base URL:

```jsonc
"Notifications": {
  "BaseUrl": "http://localhost:8080",
  "Smtp": { "Host": "smtp.corp", "Port": 587, "UseSsl": true, "Username": "svc", "Password": "…", "From": "diffpdf@corp" }
}
```

### Autentizace (OAuth2 / OIDC)

Ve výchozím stavu **vypnutá**. Zapne se přes `Auth:Enabled=true` (vyžaduje PostgreSQL /
SQL Server — OpenIddict tam ukládá klienty a tokeny). Když je zapnutá, **každý endpoint
vyžaduje bearer token** kromě `/health`, OAuth endpointů a OpenAPI dokumentu.

```jsonc
"Auth": {
  "Enabled": true,
  "ClientId": "diffpdf-ci", "ClientSecret": "…", "Scope": "diffpdf.api",
  "AccessTokenMinutes": 60
}
```

Strojový klient si vyžádá token přes client-credentials a volá s ním API:

```bash
curl -X POST http://localhost:8080/connect/token \
  -d 'grant_type=client_credentials&client_id=diffpdf-ci&client_secret=…&scope=diffpdf.api'
curl -H "Authorization: Bearer <access_token>" http://localhost:8080/api/v1/jobs
```

Seedovaný secret změň přes `Auth:ClientSecret` a nedávej ho do gitu. V produkci použij
reálné certifikáty a HTTPS.

### Logování

Logování používá **Serilog** (sekce `Serilog` v `appsettings.json`): strukturované logy
na konzoli a do denně rotovaného souboru v `logs/` (14 dní historie), jeden souhrnný
řádek na HTTP request. Adresář souborového logu nastavuje `DIFFPDF_LOG_DIR` (Docker image
ho míří na `/data/logs`).

### Lokální běh

```bash
dotnet run --project src/DiffPdf.Api
```

Pro vizuální režim vyžaduje Ghostscript na `PATH` (nebo nastav `GHOSTSCRIPT_PATH`). Kořen
artefaktů přepíšeš přes `DIFFPDF_ARTIFACT_ROOT`. Build & testy viz
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#build-testy-a-ci).

## Dokumentace pro vývojáře

Architektura, kompletní REST API reference, volby porovnání, klientské SDK, interní popis
pipeline a postup buildu/testů: **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

## Licenční poznámka

Výchozí renderer volá **Ghostscript (AGPL v3)** — pro interní / serverové použití je to
v pořádku, ale distribuce uzavřeného produktu s Ghostscriptem vyžaduje komerční licenci
od Artifexu. Renderer **PDFium** (BSD) je licenčně čistá alternativa (`"renderer": "Pdfium"`).
Další knihovny: PdfPig (Apache 2.0), PdfSharp (MIT), SkiaSharp (MIT).

## Roadmap / zatím neimplementováno

- SSIM perceptuální skórování; strukturální shlukování regionů.
- Multi-tenant izolace artefaktů a oprávnění per scope.
- Single-fire plánovače napříč replikami (DB leader-lease).
- Notifikace na tvrdě `Failed` úlohu (dnes `Completed` + `GateViolated`).
- Runtime mount autentizovaného UNC přímo v durable pipeline.
