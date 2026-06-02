# diffpdf — serverové porovnávání PDF

Serverová náhrada za [diffpdf](https://mark-summerfield.github.io/diffpdf.html),
postavená v **C# / .NET 10** jako REST API. Je navržená pro hromadné porovnávání
velkého množství PDF (složka `old` vs složka `new`) i jednotlivých dvojic.

**Hlavní use-case — regresní testování tiskových sestav.** Testeři porovnávají
čerstvě vygenerovanou dávku reportů (`new`) proti známé dobré referenci (`old`)
a ověřují, že nová verze nerozbila stávající tisky. Engine je stavěný tak, aby
přesně tyhle regrese odhalil: změněný obsah, rozbité rozložení, prázdné stránky
a chybové hlášky vyrenderované přímo do PDF.

## Funkce

- **Textové porovnání** — slovní diff s pozičním zvýrazněním (PdfPig).
- **Pixelové vizuální porovnání** — diff po pixelech s nastavitelnou tolerancí
  (až po přesnou shodu s tolerancí 0) a granularitou shluků (až po jednotlivý
  pixel); ve výchozím stavu Ghostscript, PDFium jako záloha.
- **Nastavitelná přísnost** — preset `Exact` / `Strict` / `Balanced` / `Lenient`
  řídí prahy pro hlášení rozdílů; každý lze i přepsat jednotlivě.
- **Zarovnání stránek** — vložené/odebrané stránky se detekují (Needleman–Wunsch
  nad podobností textu stránek), takže nezpůsobí kaskádu falešných rozdílů.
- **Typovaná klasifikace stránek** — každá stránka dostane štítek `TextChanged`,
  `VisualChanged`, `PageAdded`, `PageRemoved`, `SizeChanged`, `BecameBlank` nebo
  `WasBlank`.
- **Detekce prázdných stránek** — hlásí stránky, které se staly (nebo přestaly
  být) prázdné; zkoumá pixely, takže funguje i na skeny.
- **Detekce chybových hlášek v obsahu** — prohledává text na chyby, které
  reportovací nástroj vyrenderoval do PDF (např. `subreport error`, `#error`);
  vzory jsou konfigurovatelné.
- **Ignorování dynamického obsahu** — vyloučí oblasti (datum/čas v patičce, čísla
  stránek, vodoznaky) a/nebo textové vzory (časová razítka) z textového i
  vizuálního diffu, aby legitimně se měnící obsah nehlásil každý report.
- **Robustní ošetření chyb** — poškozené, šifrované, chybějící nebo prázdné PDF
  se ohlásí jako `Error` s důvodem místo pádu celé dávky.
- **Oboustranné zvýrazněné diff-PDF** — pro každý lišící se soubor dvojstrana se
  starou stránkou (vlevo, odebraný obsah červeně) vedle nové (vpravo, přidané
  zeleně, vizuální změny oranžově); záhlaví označují stranu a číslo stránky
  (`OLD p.3` / `NEW p.4`). Na výběr je rasterový styl nebo **vektorový overlay**,
  který kreslí zvýraznění přímo nad původní stránky, takže text zůstává vybíratelný.
- **Hromadné porovnání složek** — páruje soubory podle relativní cesty, běží
  paralelně, klasifikuje každou dvojici jako `Identical` / `Differs` /
  `OnlyInOld` / `OnlyInNew` / `Error`.
- **Síťové složky** — porovnání lokálních, namountovaných nebo UNC
  (`\\server\share`) složek; přihlašovací údaje a sdílení se konfigurují
  centrálně (`Network` v appsettings: credential profily + pojmenované aliasy
  `share:<jméno>`), nebo volitelně inline per složka (Windows `WNetAddConnection2`,
  Linux CIFS mount).
- **Síťové vyhledání serveru + klientské SDK** — server se ohlašuje na LAN přes UDP
  (broadcast/multicast) a vystavuje `GET /api/v1/server-info`, takže .NET klienti
  (např. WPF) ho najdou bez napevno zadané adresy. Knihovna `DiffPdf.Client`
  přidává typovaný REST přístup, OAuth a živý progress (SignalR) pro vzdálené
  ovládání serveru.
- **Náhled dávky (dry-run)** — přes `/api/v1/preview` ověř dostupnost cesty,
  počet PDF a párování old/new ještě než odešleš dávku.
- **Asynchronní job API** — odeslání dávky, polling stavu, stažení reportu a
  artefaktů.
- **Volitelná OAuth2 autentizace** — vestavěný OpenIddict server s
  client-credentials (M2M) flow vydávajícím JWT bearer tokeny; zapíná se přes
  `Auth:Enabled`.

## Jak software funguje

Tahle sekce popisuje, co se uvnitř děje — od požadavku po výsledek. Celkovou
**mapu toku a odpovědností** (projekty, dávková pipeline, dvě „discovery", auth,
klient) má [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

### A) Porovnávací engine (jádro)

Při porovnání jedné dvojice PDF (`old` vs `new`) projde `ComparisonEngine` tyto
kroky:

1. **Probe** — obě PDF se nejdřív „osahají": zjistí se počet stránek, rozměry a
   stav (`Ok` / `Encrypted` / `Unreadable` / `Empty`). Pokud je jeden soubor
   nečitelný, výsledek je `Failed` s důvodem (žádný pád).
2. **Extrakce textu** — PdfPig vytáhne slova s jejich pozicemi (bounding boxy).
3. **Detekce chyb v obsahu** — text se prohledá konfigurovatelnými regexy
   (`subreport error`, `#error`, …) a nálezy se zapíšou se stranou/stránkou/úryvkem.
4. **Zarovnání stránek** — místo párování podle indexu se stránky zarovnají podle
   podobnosti textu (Needleman–Wunsch). Vložená stránka se rozpozná jako
   `PageAdded`, smazaná jako `PageRemoved` — bez kaskády falešných rozdílů.
5. **Porovnání každé dvojice stránek**:
   - *Filtr ignorování* — slova v ignorovaných oblastech nebo odpovídající
     ignorovaným vzorům (časová razítka) se zahodí ještě před diffem.
   - *Textový diff* — slovní LCS diff; přidaná/odebraná slova se stanou regiony.
   - *Vizuální diff* — stránky se vyrenderují (Ghostscript/PDFium) a porovnají po
     pixelech s tolerancí, shift-tolerancí (pohltí sub-pixelové posuny/AA) a
     shlukováním do regionů.
   - *Prázdné stránky / rozměry* — pixelová detekce prázdnoty a porovnání
     velikosti/orientace.
   - Výsledkem je **typovaná klasifikace** stránky (kombinace příznaků) a skóre.
6. **Zvýrazněné diff-PDF** — pro lišící se stránky vznikne dvojstrana: vlevo stará
   (odebrané červeně), vpravo nová (přidané zeleně, vizuální oranžově), v záhlaví
   čísla stránek.

Všechny regiony rozdílů engine ukládá v **PDF bodech (počátek vlevo dole)**, aby
textové a vizuální výsledky sdílely jeden souřadný systém; raster writer je při
kreslení převede na pixely.

### B) Životní cyklus dávkové úlohy (durable pipeline)

```
Klient → POST /api/v1/batch
   │  validace scope (business instance + projekt), kontrola složek
   ▼
[API]  vloží job do PostgreSQL  +  publikuje RunBatchComparison   ← jedna transakce (outbox)
   ▼
RabbitMQ  ──►  [Worker / handler]
   │   RunBatchComparison  → TryStart (Queued→Running, optimistic concurrency)
   │   IndexBatch          → spáruje složky, založí file_pair_tasks, nastaví total
   │   CompareFilePair × N → porovná jednu dvojici, zapíše výsledek, ++processed
   │   FinalizeBatch       → zagreguje výsledky do reportu, job → Completed
   ▼
PostgreSQL (stav) + storage (artefakty)
   ▼
SignalR (živý progress)  +  REST polling (zdroj pravdy)
```

Principy:

- **PostgreSQL je zdroj pravdy** pro joby, business instance, projekty, progress
  a metadata reportu. Přechody stavů používají optimistic concurrency (`version`)
  a lease (`locked_by`/`locked_until`); `Queued → Running` provede jen jeden worker.
- **RabbitMQ je jen transport** příkazů — nedrží stav úlohy.
- **Wolverine** řídí publish/consume s durable inbox/outbox v PostgreSQL. Handler
  je idempotentní: opakovaně doručená zpráva najde job, který už není `Queued`, a
  přeskočí.
- **Transakční outbox** — vložení jobu a zařazení příkazu proběhne v jedné EF
  transakci; job nikdy neexistuje bez své zprávy a naopak.
- **Klasifikace retry** — transientní chyby (IO/síť/broker) se opakují s
  cooldownem, pak jdou do dead-letteru; permanentní (špatný request, chybějící
  složka, poškozený vstup) se zapíšou jako `Failed` a potvrdí (neopakují se).
- **Per-file-pair tasking** — dávka se rozpadne na řádky `file_pair_tasks`, každá
  dvojice se zpracuje samostatně. Jeden poškozený PDF se zapíše jako `Error`, ale
  **nezabije dávku**. Dvojice se **opakují** při transientních chybách
  (`Worker:MaxFilePairAttempts`) a `StaleTaskRecoveryService` vrátí do fronty a
  znovu rozešle tasky po spadlém workeru (vyprší lease) — dávka **pokračuje**
  místo zaseknutí.
- **Omezený paralelismus renderu** — procesový semafor (`IPdfWorkLimiter`,
  `Pdf:MaxConcurrentOperations`, default 4) omezí počet souběžných renderů napříč
  všemi joby/instancemi, aby paralelismus nevyčerpal CPU/RAM.

Pokud jsou nastavené `ConnectionStrings:Postgres` (nebo `ConnectionStrings:SqlServer`)
a `ConnectionStrings:RabbitMq`, použije se plný stack; jinak API spadne zpět na
in-memory úložiště a in-process Wolverine transport (jednoinstanční dev režim).

### Volba databáze (PostgreSQL nebo SQL Server)

Zdroj pravdy běží buď na **PostgreSQL**, nebo na **Microsoft SQL Serveru** — vybírá
se podle connection stringu:

- `ConnectionStrings:SqlServer` → použije se SQL Server (má přednost, je-li nastaven).
- jinak `ConnectionStrings:Postgres` → použije se PostgreSQL.

Schéma (tabulky `business_instances`, `projects`, `jobs`, `file_pair_tasks`) se
vytvoří idempotentně při startu pro oba providery; Wolverine si spravuje vlastní
inbox/outbox tabulky v téže databázi. Stejná volba platí i pro OpenIddict (auth)
— ten používá stejné připojení. Příklad pro SQL Server:

```
ConnectionStrings__SqlServer: Server=sqlserver,1433;Database=diffpdf;User Id=sa;Password=Your_password123;TrustServerCertificate=True
ConnectionStrings__RabbitMq:  amqp://diffpdf:diffpdf@rabbitmq:5672/
```

## Použité technologie a proč

- **.NET 10 / ASP.NET Core Minimal API** — moderní výkonný runtime a štíhlé HTTP API.
- **PdfPig** — extrakce textu s pozicemi slov, kterou potřebuje slovní diff.
- **Ghostscript** / **PDFium** — spolehlivý rendering stránek na obrázky pro pixelové porovnání.
- **SkiaSharp** — rychlé porovnání bitmap a detekce prázdných stránek.
- **PdfSharp** — generování zvýrazněného diff-PDF (rasterového i vektorového overlay).
- **PostgreSQL / SQL Server** — perzistentní zdroj pravdy o stavu úloh s atomickými přechody (volitelný provider).
- **EF Core (Npgsql / Microsoft.Data.SqlClient)** — typovaný přístup k databázi s optimistic concurrency.
- **Mapperly** — rychlé source-generated mapování entit na doménové modely bez reflexe.
- **RabbitMQ** — distribuovaný transport práce mezi API a workery.
- **Wolverine** — orchestrace zpráv s durable inbox/outbox, retry a dead-letterem.
- **SignalR** — realtime push progressu úloh ke klientům.
- **Serilog** — strukturované logování do konzole i rotovaného souboru.
- **OpenIddict** — standardní OAuth2 server pro bezpečný přístup strojových klientů.

## REST API

Všechny aplikační cesty jsou pod prefixem **`/api/v1`**.

| Metoda | Cesta | Účel |
|---|---|---|
| `GET`  | `/health` | Liveness probe (anonymní). |
| `POST` | `/connect/token` | OAuth2 token endpoint (client-credentials / authorization-code / refresh-token). |
| `*` | `/connect/authorize`, `/connect/revocation`, `/connect/userinfo`, `/connect/logout` | OIDC endpointy (auth-code start, revoke, userinfo, end-session). |
| `GET`  | `/api/v1/server-info` | Self-popis serveru (verze, base URL, hub, auth) pro klienty. |
| `POST` | `/api/v1/business-instances` | Vytvoří business instanci (`Alfa`, `RNew`, …). |
| `GET`  | `/api/v1/business-instances` | Výpis business instancí. |
| `POST` | `/api/v1/business-instances/{key}/projects` | Vytvoří projekt pod instancí. |
| `GET`  | `/api/v1/business-instances/{key}/projects` | Výpis projektů. |
| `POST` | `/api/v1/comparisons` | Porovná jednu dvojici (synchronně). |
| `POST` | `/api/v1/batch` | **Založí** úlohu porovnání složek (async, vrací `202`). |
| `GET`  | `/api/v1/jobs` | **Seznam** úloh (filtr `businessInstanceKey` / `projectKey` / `status`). |
| `GET`  | `/api/v1/jobs/{id}` | Stav úlohy + progress. |
| `PUT`  | `/api/v1/jobs/{id}` | **Aktualizuje** úlohu (jen `Queued`; scope se nemění). |
| `DELETE` | `/api/v1/jobs/{id}` | **Smaže** dokončenou úlohu (+ tasky a artefakty; aktivní → `409`). |
| `GET`  | `/api/v1/jobs/{id}/tasks` | Výpis file-pair tasků úlohy. |
| `GET`  | `/api/v1/jobs/{id}/report` | Agregovaný JSON report (`409` než je hotovo). |
| `GET`  | `/api/v1/jobs/{id}/result` | Verdikt CI brány: `200` když prošlo, `422` když selhalo. |
| `POST` | `/api/v1/jobs/{id}/cancel` | **Zastaví** (zruší) queued/running/paused úlohu (`409` jinak). |
| `POST` | `/api/v1/jobs/{id}/pause` | **Pozastaví** běžící úlohu (`Running → Paused`). |
| `POST` | `/api/v1/jobs/{id}/resume` | **Obnoví** pozastavenou úlohu (`Paused → Running`, znovu rozdá nezpracované páry). |
| `POST` | `/api/v1/jobs/{id}/retry` | Znovu spustí failed file-pairs hotové úlohy. |
| `GET`  | `/api/v1/jobs/{id}/artifacts/{**path}` | Stažení zvýrazněného diff-PDF. |
| `GET`  | `/api/v1/preview/shares` | Výpis nakonfigurovaných sdílení a jmen credential profilů. |
| `POST` | `/api/v1/preview/folder` | Probe složky — dostupnost + počet PDF + ukázka cest. |
| `POST` | `/api/v1/preview/pairing` | Suchý běh párování old/new (bez porovnání). |

OpenAPI dokument je na `/openapi/v1.json`, interaktivní **Swagger UI** na `/swagger`.
Chyby se vrací jako **`application/problem+json`** (RFC 9457 ProblemDetails).

### Příklad — jedna dvojice

```bash
curl -X POST http://localhost:8080/api/v1/comparisons \
  -H 'Content-Type: application/json' \
  -d '{
        "oldPath": "/pdfs/old/report.pdf",
        "newPath": "/pdfs/new/report.pdf",
        "options": { "mode": "Both", "dpi": 150 }
      }'
```

### Příklad — hromadné porovnání složek

```bash
# 0. jednou vytvoř scope (business instance + projekt)
curl -X POST http://localhost:8080/api/v1/business-instances -d '{"key":"Alfa","name":"Alfa"}' -H 'Content-Type: application/json'
curl -X POST http://localhost:8080/api/v1/business-instances/Alfa/projects -d '{"key":"LamaEnergyAlfa","name":"Lama Energy Alfa"}' -H 'Content-Type: application/json'

# 1. odešli dávku pod tímto scope
curl -X POST http://localhost:8080/api/v1/batch \
  -H 'Content-Type: application/json' \
  -d '{
        "scope": { "businessInstanceKey": "Alfa", "projectKey": "LamaEnergyAlfa" },
        "oldFolder": "/pdfs/old",
        "newFolder": "/pdfs/new",
        "recursive": true,
        "options": { "mode": "Both", "produceHighlightedPdf": true }
      }'
# -> { "id": "...", "status": "Queued", ... }

# 2. polling
curl http://localhost:8080/api/v1/jobs/<id>

# 3. stažení reportu
curl http://localhost:8080/api/v1/jobs/<id>/report
```

### Volby porovnání

| Pole | Výchozí | Poznámka |
|---|---|---|
| `mode` | `Both` | `Text`, `Visual`, nebo `Both`. |
| `pages` | vše | `{ "from": 1, "to": 5 }`. |
| `dpi` | `150` | Rozlišení renderu pro vizuál. |
| `strictness` | `Balanced` | Preset: `Exact` / `Strict` / `Balanced` / `Lenient`. Řídí prahy níže. |
| `pixelTolerance` | *(preset)* | Přepis tolerance na kanál (0-255); `0` = přesná shoda pixelů. |
| `visualThreshold` | *(preset)* | Přepis min. podílu odlišných pixelů; `0` flagne i jeden pixel. |
| `textDifferenceThreshold` | *(preset)* | Přepis min. podílu změněných slov; `0` flagne jakoukoli změnu. |
| `shiftTolerance` | *(preset)* | Poloměr (px) pro pohlcení sub-pixelových/AA posunů; `0` = striktně poziční. |
| `visualClusterCellSize` | `24` | Velikost shluku zvýraznění (px); `1` = regiony po pixelech. |
| `alignPages` | `true` | Zarovnat stránky podle obsahu (detekce insert/delete). |
| `pageMatchThreshold` | `0.2` | Min. překryv slov, aby šlo o tutéž změněnou stránku (vs add+remove). |
| `detectBlankPages` | `true` | Hlásit přechody prázdná/neprázdná. |
| `blankPageThreshold` | `0.0002` | Max. podíl ne-bílých pixelů pro prázdnou stránku. |
| `detectContentErrors` | `true` | Hledat chybové hlášky v textu. |
| `contentErrorPatterns` | viz níže | Case-insensitive regexy; default zahrnuje `subreport error`, `#error`. |
| `ignoreRegions` | `[]` | Oblasti vyloučené z porovnání (viz níže). |
| `ignoreTextPatterns` | `[]` | Regexy; odpovídající slova se zahodí před textovým diffem. |
| `produceHighlightedPdf` | `true` | Vytvořit diff-PDF pro lišící se soubory. |
| `highlightLayout` | `SideBySide` | `SideBySide` (stará vlevo / nová vpravo) nebo `Single` (jen změněná strana). |
| `highlightStyle` | `Raster` | `Raster` (stránky jako obrázek) nebo `VectorOverlay` (overlay nad originálem — text zůstává vybíratelný). |
| `renderer` | `Ghostscript` | `Ghostscript` nebo `Pdfium`. |

Výsledek jedné dvojice (`POST /api/comparisons`) vrací `outcome`
(`Compared`/`Failed`), stav per dokument, typovaný rozpis po stránkách
(`changes`, `differenceScore`, příznaky prázdnoty, regiony) a případné
`contentErrors`. Batch report agreguje počty (`identical`, `differing`,
`errors`, `filesWithContentErrors`) a navíc `passed` / `gateViolations`.

#### Ignorování dynamického obsahu

Časové razítko v patičce nebo číslo stránky se mění při každém běhu a jinak by
flagovalo každý report. Vyluč ho oblastí a/nebo textovým vzorem:

```jsonc
{
  "oldPath": "/pdfs/old/report.pdf",
  "newPath": "/pdfs/new/report.pdf",
  "options": {
    "ignoreRegions": [
      // spodních 8 % každé stránky; souřadnice mají počátek vlevo nahoře
      { "area": { "x": 0, "y": 0.92, "width": 1, "height": 0.08 },
        "unit": "Fraction", "label": "footer" }
    ],
    "ignoreTextPatterns": ["\\d{4}-\\d{2}-\\d{2}"]   // ISO data
  }
}
```

`unit` je `Fraction` (0-1 stránky) nebo `Points`; `pages` (volitelné) omezí
oblast na konkrétní čísla stránek.

#### CI brána (pass/fail dávky)

Přidej do batch požadavku `gate` a běh se stane pass/fail kontrolou. Endpoint
`GET /api/jobs/{id}/result` pak vrátí `200` při úspěchu a `422` při selhání —
ideální pro `curl --fail` v pipeline.

```jsonc
{
  "oldFolder": "/pdfs/old",
  "newFolder": "/pdfs/new",
  "gate": {
    "failOnAnyDifference": true,   // nebo nastav maxDifferingFiles
    "maxErrors": 0,
    "maxFilesWithContentErrors": 0
  }
}
```

Hodnota `null` znamená „bez limitu"; report vystavuje `passed` a `gateViolations`.

#### Síťové složky

`oldFolder` / `newFolder` mohou být lokální cesty, namountovaná sdílení, UNC cesty
(`\\server\share\...` či `//server/share/...`) nebo **pojmenovaný alias** sdílení
(`share:<jméno>[/podcesta]`). Přihlašovací údaje se dají zadat třemi způsoby —
seřazeno od nejvíc doporučeného:

**1) Konfigurace sítě (`Network` v appsettings) — doporučeno.** Credentialy a
sdílení se definují **jednou** v konfiguraci (nebo secret storu), ne v každém
requestu:

```jsonc
"Network": {
  "AllowInlineCredentials": true,   // false = zakaž hesla v tělech requestů
  "MountReadOnly": true,            // Linux: mountuj CIFS read-only
  "CifsMountOptions": "vers=3.0",   // Linux: extra -o volby pro mount.cifs
  "CredentialProfiles": {
    "corp": { "username": "svc_diff", "password": "…", "domain": "CORP" }
  },
  "Shares": {
    "reports":  { "root": "\\\\fileserver\\reports", "credentialProfile": "corp" },
    "baseline": { "localMountPath": "/mnt/reports" }   // předmountováno na každém workeru
  }
}
```

Request se pak odkáže na alias a/nebo profil — heslo nikdy neopustí konfiguraci:

```jsonc
{
  "oldFolder": "share:reports/baseline",        // → \\fileserver\reports\baseline (creds z profilu "corp")
  "newFolder": "share:reports/build-123"
}
```

**2) Profil jménem na vlastní cestě** — `oldFolderCredentialProfile: "corp"`
k libovolné UNC cestě.

**3) Inline credentialy v requestu** (zpětně kompatibilní; jdou zakázat přes
`AllowInlineCredentials: false`):

```jsonc
{
  "oldFolder": "\\\\fileserver\\reports\\baseline",
  "newFolder": "\\\\fileserver\\reports\\build-123",
  "oldFolderCredentials": { "username": "svc_diff", "password": "…", "domain": "CORP" },
  "newFolderCredentials": { "username": "svc_diff", "password": "…", "domain": "CORP" }
}
```

Mechanismus připojení:

- **Windows** připojí sdílení přes `WNetAddConnection2` (jako `net use`, bez
  mapování disku) a po doběhnutí odpojí.
- **Linux** namountuje sdílení přes CIFS do dočasného bodu a poté odmountuje.
  Vyžaduje `cifs-utils` (v Docker image už je) a oprávnění k mountu (kontejner s
  `--privileged` nebo `--cap-add SYS_ADMIN`). `MountReadOnly` a `CifsMountOptions`
  ladí `-o` volby.
- Cesty **bez** credentialů (lokální, mapované disky, předmountovaná sdílení nebo
  UNC pod service účtem) se použijí tak, jak jsou. Credentialy posílej jen přes
  HTTPS; nikdy se nezapisují do logů ani reportů.
- **Durable pipeline** (`/api/v1/batch`) pracuje se stabilními cestami napříč
  workery — proto pro ni preferuj `localMountPath` (předmountováno) nebo UNC pod
  service účtem. Aliasy a profily se vyhodnotí už při submitu, takže do úlohy se
  uloží konkrétní cesta; s `localMountPath` se navíc nepersistuje žádné heslo.

#### Náhled dávky / dry-run (`/api/v1/preview`)

Než odešleš (drahou) dávku, můžeš si ověřit dostupnost cest, credentialy a
párování — bez jediného porovnání:

```bash
# co je nakonfigurované (jen jména, žádná tajemství)
curl http://localhost:8080/api/v1/preview/shares

# je složka dostupná a kolik PDF obsahuje? (vrátí i ukázku cest)
curl -X POST http://localhost:8080/api/v1/preview/folder \
  -H 'Content-Type: application/json' \
  -d '{ "folder": "share:reports/baseline" }'

# suchý běh párování old vs new — kolik matched / onlyInOld / onlyInNew
curl -X POST http://localhost:8080/api/v1/preview/pairing \
  -H 'Content-Type: application/json' \
  -d '{ "oldFolder": "share:reports/baseline", "newFolder": "share:reports/build-123" }'
```

Přijímá stejné odkazy na sdílení/profily jako dávka. Nedostupná cesta nebo
neznámý alias vrátí `reachable: false` s důvodem v `error`, ne chybu 500.

### Síťové vyhledání serveru + klientské SDK

Aby šel server **najít v síti a ovládat vzdáleně** (např. z WPF klienta), ohlašuje
se přes UDP a vystavuje self-popis:

- **UDP responder** poslouchá na portu `Discovery:Port` (default `41234`) — odpovídá
  na broadcast i multicast probe popisem serveru (jméno, base URL, port, verze,
  zda je auth a jaké granty). Multicast joinuje na všech interface (multi-homed).
  Vypíná se přes `Discovery:Enabled=false`; za reverse proxy / s pevným DNS nastav
  `Discovery:AdvertisedBaseUrl`. Provozuj jen na **důvěryhodné LAN** — UDP odpověď
  je větší než probe (riziko reflection); `Discovery:AllowedClientSubnets` (CIDR)
  omezí, komu responder odpoví.
- **`GET /api/v1/server-info`** (anonymní) vrací tentýž popis přes HTTP — pro
  klienta, který už base URL zná.

Knihovna **`DiffPdf.Client`** (`src/DiffPdf.Client`, `net10.0`) pak server najde a
ovládá: discovery, typovaný REST (OAuth, scope, dávky, polling, report, artefakty)
a živý progress přes SignalR. Stručně:

```csharp
var server = await new DiffPdfDiscoveryClient().DiscoverFirstAsync(TimeSpan.FromSeconds(2));
using var client = DiffPdfClient.ForServer(server!);
await client.AuthenticateClientCredentialsAsync("diffpdf-ci", "diffpdf-secret", "diffpdf.api");

await using var live = await client.ConnectLiveProgressAsync();
live.ProgressReceived += p => /* aktualizuj UI */;

var job = await client.SubmitBatchAsync(new BatchComparisonRequest { /* … */ });
await live.JoinJobAsync(job.Id);
var report = await client.GetReportAsync((await client.WaitForJobAsync(job.Id)).Id);
```

Plný příklad (vč. WPF) viz [`src/DiffPdf.Client/README.md`](src/DiffPdf.Client/README.md).

### Business instance, projekty a struktura úložiště

Úlohy mají scope **business instance** (např. `Alfa`, `RNew`, `ROld`) a
**projekt** pod ní (např. `LamaEnergyAlfa`). To jsou data zakládaná přes API a
uložená v PostgreSQL — nikdy ne natvrdo v kódu. Artefakty žijí pod scope:

```
storage/{businessInstanceKey}/{projectKey}/jobs/{jobId}/artifacts|reports|logs
```

Klíče se validují (`[a-zA-Z0-9_.-]`, ≤64 znaků, žádné `..`), takže nemůžou utéct
z kořene úložiště. Struktura výše je příklad dat, ne chování aplikace.

## Spuštění

### Docker (doporučeno)

```bash
docker compose up --build
# Spustí PostgreSQL + RabbitMQ + API na http://localhost:8080,
# porovnává ./samples/old vs ./samples/new. Data jsou v pojmenovaných volumech.
```

Image instaluje Ghostscript a nativní závislosti pro SkiaSharp/PDFium; compose
nastaví `ConnectionStrings__Postgres` / `__RabbitMq` a `Storage__RootPath`.

### Lokálně

```bash
dotnet run --project src/DiffPdf.Api
```

Pro vizuální režim vyžaduje binárku Ghostscript na `PATH` (nebo nastav
`GHOSTSCRIPT_PATH`). Kořen artefaktů přepíšeš přes `DIFFPDF_ARTIFACT_ROOT`.

### Testy

```bash
dotnet test
```

### Logování

Logování používá **Serilog**, konfigurované v `appsettings.json` (sekce
`Serilog`). Defaultně píše strukturované logy na konzoli a do denně rotovaného
souboru v `logs/` (14 dní historie), obohacuje každou událost o source context a
vlastnost `Application` a loguje jeden souhrnný řádek na HTTP request. Sinky a
úrovně se mění v `appsettings.json` — bez zásahu do kódu.

Adresář souborového logu nastavuje `DIFFPDF_LOG_DIR` (default `logs/`); Docker
image ho míří na `/data/logs`, aby logy přežily na namountovaném volume.

### Autentizace (OAuth2 / OIDC)

Autentizace je **ve výchozím stavu vypnutá**. Zapne se přes `Auth:Enabled=true`
(vyžaduje připojení k PostgreSQL / SQL Serveru — OpenIddict tam ukládá klienty a
tokeny). Když je zapnutá, **každý endpoint vyžaduje bearer token** kromě
`/health`, OAuth endpointů a OpenAPI dokumentu.

Vestavěný OpenIddict server vystavuje standardní auto-generované endpointy:

| Endpoint | Účel |
| --- | --- |
| `POST /connect/token` | Vydání access (a refresh) tokenu — všechny granty. |
| `GET/POST /connect/authorize` | Authorization-code start (interaktivní flow). |
| `POST /connect/revocation` | Zneplatnění access/refresh tokenu (RFC 7009). |
| `GET/POST /connect/userinfo` | OIDC claims subjektu (vyžaduje token). |
| `GET/POST /connect/logout` | End-session (odhlášení, post-logout redirect). |

Podporované jsou dva flow a dva seedované klienti:

**1) Client-credentials (M2M / CI).** Confidential klient `Auth:ClientId` /
`Auth:ClientSecret` / `Auth:Scope`. Strojoví klienti si vyžádají token a volají
s ním API:

```bash
# 1. získej token
curl -X POST http://localhost:8080/connect/token \
  -d 'grant_type=client_credentials&client_id=diffpdf-ci&client_secret=diffpdf-secret&scope=diffpdf.api'
# -> { "access_token": "...", "token_type": "Bearer", "expires_in": 3599 }

# 2. volej API s tokenem
curl -H "Authorization: Bearer <access_token>" http://localhost:8080/api/v1/jobs
```

**2) Authorization-code + PKCE + refresh (interaktivní).** Public klient
`Auth:InteractiveClientId` (bez secretu, chráněný PKCE) pro klienty připojující se
za uživatele. Klient pošle uživatele na `/connect/authorize` (`response_type=code`,
`code_challenge`, `scope=openid profile offline_access diffpdf.api`); ten se
přihlásí (`/account/login`, uživatelé z `Auth:Users`) a po redirectu klient
vymění kód na `/connect/token` (`grant_type=authorization_code`) za **access +
refresh** token. Token se obnovuje `grant_type=refresh_token` (refresh tokeny se
rotují) a zneplatňuje přes `/connect/revocation`.

```jsonc
"Auth": {
  "Enabled": true,
  "ClientId": "diffpdf-ci", "ClientSecret": "…", "Scope": "diffpdf.api",
  "InteractiveClientId": "diffpdf-app",
  "RedirectUris": [ "http://localhost:8080/swagger/oauth2-redirect.html" ],
  "Users": [ { "username": "tester", "password": "…", "name": "QA tester" } ],
  "AccessTokenMinutes": 60,
  "RefreshTokenDays": 14
}
```

Tokeny jsou JWT podepsané ephemerálními klíči; pro **více instancí / přežití
restartu** nastav perzistentní certifikát přes `Auth:SigningCertificatePath`
(volitelně `EncryptionCertificatePath`). Seedovaný secret změň přes
`Auth:ClientSecret` a nedávej ho do gitu.

Vestavěné `/account/login` je minimální, ale ošetřené: hesla uživatelů zadávej
jako **PBKDF2 hash** (`Auth:Users[].passwordHash`, `pbkdf2$iter$salt$hash`;
plaintext `password` jen pro dev), porovnání je constant-time, formulář chrání
**antiforgery** token a endpoint má **rate-limit**. V produkci ho přesto raději
nahraď reálným identity providerem.

Z .NET klienta jde použít i tenhle interaktivní flow:
`DiffPdfClient.AuthenticateAuthorizationCodeAsync(...)` otevře browser, zachytí
redirect na loopbacku (PKCE) a vymění kód za access+refresh token (musí sedět
`Auth:RedirectUris`).

## Licenční poznámka

Výchozí renderer volá **Ghostscript (AGPL v3)**. Pro interní / serverové použití
je to v pořádku, ale distribuce uzavřeného produktu s Ghostscriptem vyžaduje
komerční licenci od Artifexu. Renderer **PDFium** (BSD) je licenčně čistá
alternativa — nastav `"renderer": "Pdfium"`. Další knihovny: PdfPig (Apache 2.0),
PdfSharp (MIT), SkiaSharp (MIT).

## Roadmap / zatím neimplementováno

- SSIM perceptuální skórování; strukturální shlukování regionů.
- Multi-tenant izolace artefaktů a oprávnění per scope.
- Runtime mount autentizovaného UNC přímo v durable pipeline (dnes přes
  `localMountPath` / service účet; aliasy a profily se řeší při submitu).
