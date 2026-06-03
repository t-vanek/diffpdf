# diffpdf — vývojářská dokumentace

Architektura, kompletní REST API, klientské SDK a interní popis pipeline. Přehled
produktu, funkce a nastavení jsou v [../README.md](../README.md).

## Architektura

Čistá vrstvená (hexagonální) architektura: doménová rozhraní žijí v `DiffPdf.Core`,
konkrétní implementace v perifériích (PDF knihovny, perzistence, messaging). Solution
[`DiffPdf.slnx`](../DiffPdf.slnx) má 10 projektů:

| Projekt | Role |
|---|---|
| **`DiffPdf.Core`** | Doménové jádro — `ComparisonEngine`, modely, abstrakce, zarovnání stránek, text diff, síťová logika. Bez I/O závislostí. |
| **`DiffPdf.Pdf`** | Adaptéry na knihovny: PdfPig (text), Ghostscript/PDFium (render), SkiaSharp (pixel diff + blank detekce), PdfSharp (highlight PDF). |
| **`DiffPdf.Api`** | ASP.NET Core Minimal API — endpointy, SignalR hub, OpenIddict auth, DI wiring. |
| **`DiffPdf.Messaging`** | Wolverine handlery durable pipeline + cron plánovač (`ScheduledBatchService`, `BatchLauncher`). |
| **`DiffPdf.Notifications`** | Outbound notifikace — webhook + SMTP, `NotificationDispatcher`. |
| **`DiffPdf.Persistence`** | Rozhraní stores (`IJobStore`, `IScheduleStore`, `ISubscriptionStore`, …) + in-memory implementace. |
| **`DiffPdf.Persistence.Postgres` / `.SqlServer`** | EF Core implementace stores, DbContext, idempotentní migrátor. |
| **`DiffPdf.Worker`** | Worker DI, storage provisioning, work limiter. |
| **`DiffPdf.Client`** | Typovaný .NET SDK (self-contained, balitelný jako NuGet). |

Pokud je nastavený `ConnectionStrings:Postgres` (nebo `:SqlServer`), použije se plný stack
(relační zdroj pravdy + **DB-backed durable local queues**, žádný broker); jinak API spadne
zpět na in-memory úložiště a in-process Wolverine transport (jednoinstanční dev režim — používá
ho i testovací `WebApplicationFactory`).

## Jak software funguje

### A) Porovnávací engine (jádro)

Při porovnání jedné dvojice PDF (`old` vs `new`) projde `ComparisonEngine` tyto kroky:

1. **Probe** — obě PDF se „osahají": počet stránek, rozměry, stav (`Ok` / `Encrypted` /
   `Unreadable` / `Empty`). Nečitelný soubor → `Failed` s důvodem (žádný pád).
2. **Extrakce textu** — PdfPig vytáhne slova s pozicemi (bounding boxy).
3. **Detekce chyb v obsahu** — text se prohledá konfigurovatelnými regexy
   (`subreport error`, `#error`, …); nálezy se zapíšou se stranou/stránkou/úryvkem.
4. **Zarovnání stránek** — stránky se zarovnají podle podobnosti textu (Needleman–Wunsch).
   Vložená stránka → `PageAdded`, smazaná → `PageRemoved`, bez kaskády falešných rozdílů.
5. **Porovnání každé dvojice stránek**:
   - *Filtr ignorování* — slova v ignorovaných oblastech / odpovídající vzorům se zahodí
     před diffem.
   - *Textový diff* — slovní LCS diff; přidaná/odebraná slova → regiony.
   - *Vizuální diff* — render (Ghostscript/PDFium) + porovnání po pixelech s tolerancí,
     shift-tolerancí (pohltí sub-pixelové posuny/AA) a shlukováním do regionů.
   - *Prázdné stránky / rozměry* — pixelová detekce prázdnoty + porovnání velikosti.
   - Výsledkem je **typovaná klasifikace** stránky (kombinace příznaků) a skóre.
6. **Zvýrazněné diff-PDF** — pro lišící se stránky dvojstrana: vlevo stará (odebrané
   červeně), vpravo nová (přidané zeleně, vizuální oranžově).

Všechny regiony rozdílů engine ukládá v **PDF bodech (počátek vlevo dole)**, aby textové
a vizuální výsledky sdílely jeden souřadný systém; raster writer je převede na pixely.

### B) Životní cyklus dávkové úlohy (durable pipeline)

```
Cron rozvrh (tik)  /  POST …/schedules/{key}/run   (žádné ruční zakládání dávky)
   │  pre-flight „je co porovnávat" → job vzniká rovnou jako Queued
   │  + publikuje RunBatchComparison ve stejné transakci (outbox)
   ▼
Durable local queue (perzistentní v DB)  ──►  [handler]
   │   RunBatchComparison  → TryStart (Queued→Running, optimistic concurrency)
   │   IndexBatch          → spáruje složky, založí file_pair_tasks, nastaví total
   │   CompareFilePair × N → porovná jednu dvojici, zapíše výsledek, ++processed
   │   FinalizeBatch       → zagreguje výsledky do reportu, job → Completed
   ▼
PostgreSQL (stav) + storage (artefakty)
   ▼
SignalR (živý progress)  +  REST polling (zdroj pravdy)  +  notifikace (webhook/SMTP)
```

Principy:

- **Relační DB je zdroj pravdy** pro joby, větve, instance, rozvrhy, odběry, progress
  a metadata reportu. Přechody stavů používají optimistic concurrency (`version`) a lease
  (`locked_by`/`locked_until`); `Queued → Running` provede jen jeden worker.
- **Žádný externí broker** — příkazy jdou přes **durable local queues** perzistentní v téže
  databázi (přežijí restart, retry, dead-letter), zpracované in-process Wolverine agenty.
- **Wolverine** řídí publish/consume s durable inbox/outbox. Handler je idempotentní:
  opakovaně doručená zpráva najde job, který už není `Queued`, a přeskočí.
- **Spuštění jen automatizací** — job nezakládá klient ručně. Cron rozvrh nebo akce
  „spusť teď" po pre-flight kontrole založí job rovnou jako `Queued` a ve **stejné EF
  transakci** zařadí `RunBatchComparison` do outboxu — job tak nikdy neexistuje bez své
  zprávy a naopak. (Stav `Draft` z dřívějšího ručního flow zůstává v enumu jako
  vestigiální — nikdo ho už nevytváří.)
- **Pozastavení a obnovení (graceful)** — `pause` přepne běžící job na `Paused`; handlery
  jsou kooperativní (rozběhnuté porovnání dvojice doběhne, další se neberou). `resume`
  vrátí job do `Running` a znovu rozešle nedokončené `file_pair_tasks`.
- **Klasifikace retry** — transientní chyby (IO/síť/broker) se opakují s cooldownem, pak
  jdou do dead-letteru; permanentní (špatný request, chybějící složka, poškozený vstup)
  se zapíšou jako `Failed` a potvrdí.
- **Per-file-pair tasking** — jeden poškozený PDF se zapíše jako `Error`, ale **nezabije
  dávku**. Dvojice se opakují při transientních chybách (`Worker:MaxFilePairAttempts`)
  a `StaleTaskRecoveryService` vrátí do fronty tasky po spadlém workeru (vyprší lease).
- **Omezený paralelismus renderu** — procesový semafor (`IPdfWorkLimiter`,
  `Pdf:MaxConcurrentOperations`, default 4) omezí souběžné rendery napříč všemi joby.

### Schéma databáze

Tabulky `branches`, `instances`, `jobs`, `file_pair_tasks`, `comparison_schedules`,
`notification_subscriptions`, `automation_leader` (lease vedoucí repliky), `schedule_runs`
(historie běhů) a `folder_watches` (jeden watch na instanci) se vytvoří **idempotentně při
startu** pro oba providery
(raw SQL `create table if not exists` / `if object_id(...) is null`; sloupec
`jobs.artifacts_pruned_at` se přidá idempotentním `ALTER`); Wolverine si spravuje vlastní
inbox/outbox + frontové tabulky v téže databázi. Stejné připojení používá i OpenIddict (auth).

Komplexní pole se ukládají jako JSON sloupce (`jsonb` v Postgresu, `nvarchar(max)` v SQL
Serveru): `jobs.request_json`/`report_json`, `comparison_schedules.options_json`/`gate_json`,
`notification_subscriptions.events_json`. Mapování entit na doménové modely zajišťuje
**Mapperly** (scalar sloupce) + ruční konvertory přes `DiffPdfJson` (JSON sloupce).

## REST API

Všechny aplikační cesty jsou pod prefixem **`/api/v1`**. OpenAPI dokument je na
`/openapi/v1.json`, interaktivní **Swagger UI** na `/swagger`. Chyby se vrací jako
**`application/problem+json`** (RFC 9457 ProblemDetails).

| Metoda | Cesta | Účel |
|---|---|---|
| `GET`  | `/health` | Liveness probe (anonymní). |
| `POST` | `/connect/token` | OAuth2 token endpoint (client-credentials). |
| `*`    | `/connect/revocation` | Zneplatnění access tokenu (RFC 7009). |
| `POST` `GET` | `/api/v1/branches` | Vytvoří / vypíše větve. |
| `GET`  | `/api/v1/branches/{branchKey}` | Detail větve. |
| `POST` `GET` | `/api/v1/branches/{branchKey}/instances` | Vytvoří instanci (`?ensureStructure=false` vypne provisioning) / výpis. |
| `GET`  | `…/instances/{instanceKey}` | Detail instance. |
| `POST` | `…/instances/{instanceKey}/structure` | Založí/opraví složky `old`/`new`/`reports` (`?includeFiles=true`). |
| `GET`  | `…/instances/{instanceKey}/readiness` | Stav složek + počty PDF + párování `old`/`new` + verdikt `ready`. |
| `POST` `GET` | `…/instances/{instanceKey}/schedules` | Vytvoří rozvrh (cron + volby + CI gate) / výpis. |
| `GET` `PUT` `DELETE` | `…/instances/{instanceKey}/schedules/{scheduleKey}` | Detail / úprava (`Version` → `409`) / smazání (`204`). |
| `POST` | `…/instances/{instanceKey}/schedules/{scheduleKey}/run` | Spustí dávku **teď**: `202` + `jobId`; `422` když není co porovnávat. |
| `GET`  | `…/instances/{instanceKey}/schedules/{scheduleKey}/runs` | Historie běhů rozvrhu (nejnovější první; `?limit=N`, default 50). |
| `POST` | `/api/v1/comparisons` | Porovná jednu dvojici (synchronně). |
| `GET`  | `/api/v1/jobs` | Výpis úloh (filtr `branchKey` / `instanceKey` / `status`). |
| `GET`  | `/api/v1/jobs/{id}` | Stav úlohy + progress. |
| `GET`  | `/api/v1/jobs/{id}/tasks` | Výpis file-pair tasků. |
| `GET`  | `/api/v1/jobs/{id}/report` | Agregovaný JSON report (`409` než je hotovo). |
| `GET`  | `/api/v1/jobs/{id}/result` | Verdikt CI brány: `200` prošlo / `422` selhalo. |
| `POST` | `/api/v1/jobs/{id}/cancel` | Zruší queued/running/`Paused` úlohu (`409` jinak). |
| `POST` | `/api/v1/jobs/{id}/pause` | Pozastaví běžící úlohu. |
| `POST` | `/api/v1/jobs/{id}/resume` | Obnoví pozastavenou úlohu. |
| `POST` | `/api/v1/jobs/{id}/retry` | Znovu spustí failed file-pairs hotové úlohy. |
| `GET`  | `/api/v1/jobs/{id}/artifacts/{**path}` | Stažení zvýrazněného diff-PDF. |
| `POST` `GET` | `/api/v1/subscriptions` | Vytvoří / vypíše notifikační odběry. |
| `GET` `PUT` `DELETE` | `/api/v1/subscriptions/{id}` | Detail / úprava (`Version` → `409`) / smazání (`204`). |
| `POST` | `/api/v1/triggers/{branchKey}/{instanceKey}` | On-demand trigger jedné instance: `202` (launched, + jobId) / `200` (skip — nic k porovnání / nedostupné) / `404`. |
| `POST` | `/api/v1/branches/{branchKey}/run` | Fan-out trigger přes všechny enabled instance větve. |
| `PUT` `GET` `DELETE` | `…/instances/{instanceKey}/watch` | Folder-watch instance: nastav (upsert) / detail (`404` když není) / smaž (`204`). |
| `GET`  | `/api/v1/watches` | Výpis všech folder-watchů (přehled). |
| `GET`  | `/api/v1/discovery/shares` | Výpis nakonfigurovaných sdílení a jmen credential profilů. |

### Příklad — jedna dvojice

```bash
curl -X POST http://localhost:8080/api/v1/comparisons \
  -H 'Content-Type: application/json' \
  -d '{
        "oldPath": "/pdfs/LamaEnergy/old/report.pdf",
        "newPath": "/pdfs/LamaEnergy/new/report.pdf",
        "options": { "mode": "Both", "dpi": 150 }
      }'
```

### Příklad — dávka přes rozvrh

Dávky se zakládají **jen** rozvrhem (cron) nebo akcí „spusť teď" — ruční `POST /batch`
+ `/start` neexistují.

```bash
# 1. scope: větev + instance
curl -X POST http://localhost:8080/api/v1/branches -d '{"key":"Alfa","name":"Alfa"}' -H 'Content-Type: application/json'
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances \
  -d '{"key":"LamaEnergy","name":"Lama Energy","basePath":"/pdfs/LamaEnergy"}' -H 'Content-Type: application/json'

# 2. rozvrh — cron + volby + (volitelně) CI brána
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/schedules \
  -H 'Content-Type: application/json' \
  -d '{ "key": "nightly", "cron": "0 2 * * *",
        "options": { "mode": "Both", "produceHighlightedPdf": true },
        "gate": { "failOnAnyDifference": true } }'

# 3. spusť teď -> 202 + jobId (422 když není co porovnávat)
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/schedules/nightly/run

# 4. polling stavu / stažení reportu
curl http://localhost:8080/api/v1/jobs/<id>
curl http://localhost:8080/api/v1/jobs/<id>/report
```

### Volby porovnání

Volby (`options`) nese jednorázové `POST /comparisons` i každý rozvrh.

| Pole | Výchozí | Poznámka |
|---|---|---|
| `mode` | `Both` | `Text`, `Visual`, nebo `Both`. |
| `pages` | vše | `{ "from": 1, "to": 5 }`. |
| `dpi` | `150` | Rozlišení renderu pro vizuál. |
| `strictness` | `Balanced` | Preset: `Exact` / `Strict` / `Balanced` / `Lenient`. Řídí prahy níže. |
| `pixelTolerance` | *(preset)* | Tolerance na kanál (0-255); `0` = přesná shoda pixelů. |
| `visualThreshold` | *(preset)* | Min. podíl odlišných pixelů; `0` flagne i jeden pixel. |
| `textDifferenceThreshold` | *(preset)* | Min. podíl změněných slov; `0` flagne jakoukoli změnu. |
| `shiftTolerance` | *(preset)* | Poloměr (px) pro pohlcení sub-pixelových/AA posunů; `0` = striktně poziční. |
| `visualClusterCellSize` | `24` | Velikost shluku zvýraznění (px); `1` = regiony po pixelech. |
| `alignPages` | `true` | Zarovnat stránky podle obsahu (detekce insert/delete). |
| `pageMatchThreshold` | `0.2` | Min. překryv slov, aby šlo o tutéž změněnou stránku (vs add+remove). |
| `detectBlankPages` | `true` | Hlásit přechody prázdná/neprázdná. |
| `blankPageThreshold` | `0.0002` | Max. podíl ne-bílých pixelů pro prázdnou stránku. |
| `detectContentErrors` | `true` | Hledat chybové hlášky v textu. |
| `contentErrorPatterns` | (default) | Case-insensitive regexy; default zahrnuje `subreport error`, `#error`. |
| `ignoreRegions` | `[]` | Oblasti vyloučené z porovnání (viz níže). |
| `ignoreTextPatterns` | `[]` | Regexy; odpovídající slova se zahodí před textovým diffem. |
| `produceHighlightedPdf` | `true` | Vytvořit diff-PDF pro lišící se soubory. |
| `highlightLayout` | `SideBySide` | `SideBySide` (stará vlevo / nová vpravo) nebo `Single`. |
| `highlightStyle` | `Raster` | `Raster` nebo `VectorOverlay` (text zůstává vybíratelný). |
| `renderer` | `Ghostscript` | `Ghostscript` nebo `Pdfium`. |

Výsledek jedné dvojice vrací `outcome` (`Compared`/`Failed`), stav per dokument, typovaný
rozpis po stránkách (`changes`, `differenceScore`, příznaky prázdnoty, regiony) a případné
`contentErrors`. Batch report agreguje počty (`identical`, `differing`, `errors`,
`filesWithContentErrors`) + `passed` / `gateViolations`.

#### Ignorování dynamického obsahu

Časové razítko v patičce nebo číslo stránky se mění při každém běhu a jinak by flagovalo
každý report. Vyluč ho oblastí a/nebo textovým vzorem:

```jsonc
"options": {
  "ignoreRegions": [
    // spodních 8 % každé stránky; souřadnice mají počátek vlevo nahoře
    { "area": { "x": 0, "y": 0.92, "width": 1, "height": 0.08 },
      "unit": "Fraction", "label": "footer" }
  ],
  "ignoreTextPatterns": ["\\d{4}-\\d{2}-\\d{2}"]   // ISO data
}
```

`unit` je `Fraction` (0-1 stránky) nebo `Points`; `pages` (volitelné) omezí oblast na
konkrétní čísla stránek.

#### CI brána (pass/fail dávky)

Přidej `gate` do **rozvrhu** a každý jeho běh se stane pass/fail kontrolou. Endpoint
`GET /api/v1/jobs/{id}/result` vrátí `200` při úspěchu a `422` při selhání — ideální pro
`curl --fail` v pipeline (jobId získáš z odpovědi `…/schedules/{key}/run`).

```jsonc
// POST …/instances/LamaEnergy/schedules
{
  "key": "ci", "cron": "0 2 * * *",
  "gate": {
    "failOnAnyDifference": true,   // nebo nastav maxDifferingFiles
    "maxErrors": 0,
    "maxFilesWithContentErrors": 0
  }
}
```

Hodnota `null` znamená „bez limitu"; report vystavuje `passed` a `gateViolations`.

### Větve, instance a struktura úložiště

Úlohy mají scope **větev** (např. `Alfa`, `RNew`, `ROld`) a **instanci** pod ní (např.
`LamaEnergy`). To jsou data zakládaná přes API a uložená v DB — nikdy ne natvrdo v kódu.
Klíče se validují (`[a-zA-Z0-9_.-]`, ≤64 znaků, žádné `..`). Každá instance nese
`basePath` se vstupem (`old`/`new`) a výstupem (`reports/{jobId}/...`). Aplikace zapisuje
**jen** do `reports/`.

**Provisioning struktury.** Server umí strukturu `old`/`new`/`reports` zjistit i srovnat:

- `POST …/instances/{key}/structure` — **založí/opraví**: chybějící → `Created`; soubor
  kolidující s názvem se smaže a nahradí složkou (`Repaired`); existující → `Present`.
  Odpověď nese stav každé podsložky, `ok` a u `old`/`new` `pdfCount` (`?includeFiles=true`
  přidá `files`).
- **Při zakládání instance** se ensure spustí automaticky (vypneš `?ensureStructure=false`).
- **Při startu serveru** projde server všechny instance a strukturu zajistí (best-effort).

**Readiness:** `GET …/instances/{key}/readiness` v jednom volání vrátí stav složek
(`structure` + `pdfCount`) **a** suchý běh párování `old` vs `new` (`matched` / `onlyInOld`
/ `onlyInNew` + ukázky) s verdiktem `ready`. Stejnou bránu vyhodnotí i plánovač a „spusť
teď" před každým spuštěním.

### Discovery

`GET /api/v1/discovery/shares` vypíše nakonfigurovaná sdílení a credential profily (jen
jména, žádná tajemství) — vidíš, na jaké aliasy se instance může odkazovat.

### Síťové složky a credentialy

`basePath` může být lokální cesta, UNC (`\\server\share\...`) nebo alias `share:<jméno>`.
Mechanismus připojení:

- **Windows** připojí sdílení přes `WNetAddConnection2` (jako `net use`, bez mapování
  disku) a po doběhnutí odpojí.
- **Linux** namountuje sdílení přes CIFS do dočasného bodu a poté odmountuje. Vyžaduje
  `cifs-utils` (v Docker image už je) a oprávnění k mountu (kontejner s `--privileged`
  nebo `--cap-add SYS_ADMIN`). Protože se do `reports/` zapisuje, nech `MountReadOnly: false`.
- Cesty **bez** credentialů (lokální, mapované disky, předmountovaná sdílení nebo UNC pod
  service účtem) se použijí tak, jak jsou. Credentialy nikdy nekončí v logu ani reportu.
- **Durable pipeline** pracuje se stabilními cestami napříč workery — preferuj
  `localMountPath` (předmountováno) nebo UNC pod service účtem. `basePath` se resolvne při
  založení úlohy, takže se uloží konkrétní cesta.

Konfigurace sekce `Network` (profily + aliasy) viz [../README.md](../README.md#síťové-složky-a-credentialy).

## Klientské SDK (.NET)

Balíček **`DiffPdf.Client`** ([../src/DiffPdf.Client](../src/DiffPdf.Client)) je typovaný
.NET klient pokrývající celý flow — větve, instance, strukturu, readiness, **rozvrhy
a notifikační odběry** i sledování úloh. Je **self-contained** (vlastní modely, žádná
závislost na server projektech).

```csharp
using DiffPdf.Client;

// registrace (bez auth):
services.AddDiffPdfClient(new Uri("http://localhost:8080"));
// nebo s M2M tokenem (OpenIddict client-credentials):
services.AddDiffPdfClient(new Uri("http://localhost:8080"), "diffpdf-ci", "secret", "diffpdf.api");

// použití (DiffPdfClient injectnutý z DI):
await diff.CreateBranchAsync(new("Alfa", "Alfa"));
await diff.CreateInstanceAsync("Alfa", new("LamaEnergy", "Lama Energy", "/pdfs/LamaEnergy"));
await diff.CreateScheduleAsync("Alfa", "LamaEnergy", new() { Key = "nightly", Cron = "0 2 * * *" });

// spusť teď a počkej na report:
var jobId = await diff.RunScheduleNowAsync("Alfa", "LamaEnergy", "nightly");
var report = await diff.WaitForReportAsync(jobId);
Console.WriteLine($"{report.Differing}/{report.Total} se liší");
```

Správa automatizace: `CreateScheduleAsync` / `ListSchedulesAsync` / `UpdateScheduleAsync`
(optimistic concurrency přes `Version`) / `DeleteScheduleAsync` / `RunScheduleNowAsync`
a `CreateSubscriptionAsync` … pro notifikační odběry. Sledování úloh: `GetJobAsync` (poll)
→ `PauseJobAsync` / `ResumeJobAsync` / `CancelJobAsync` / `RetryJobAsync` → `GetReportAsync`
/ `GetResultAsync` / `DownloadArtifactAsync`. Non-2xx odpovědi vyhodí `DiffPdfApiException`
(s HTTP statusem a `detail` z problem+json).

## Automatizace — plánovač a notifikace

Dvě vrstvy nad dávkovou pipeline, obě **spravované za běhu přes API a uložené v DB** (ne
v appsettings). Ve výchozím stavu nečinné: bez rozvrhů se nic nespouští, bez odběrů se nic
neposílá. Mění se za běhu — **bez restartu serveru**.

### Plánovač (rozvrhy jako resource)

`ScheduledBatchService` (hosted service) každých ~20 s načte **aktivní rozvrhy z DB**
(`IScheduleStore.ListEnabledAsync`) a přes `ScheduleReconciler` odsouhlasí, kterým podle
jejich **cron** výrazu (5 polí, **UTC**) nastal čas. Reconciler je čistá, testovatelná
logika klíčovaná podle Id rozvrhu: nově viděný (nebo s změněným cronem) se naseeduje na
„next occurrence" **bez spuštění** na tomtéž tiku; due rozvrh se vrátí a posune (žádné
dvojí spuštění); zmizelý (smazaný/disabled) se zahodí. Spuštění jde přes `BatchLauncher`,
který projde stejnou readiness bránou jako `…/readiness` (prázdné `old`/`new` nebo
nedostupná cesta → běh přeskočí) a založí job atomicky (transactional outbox). Nově
vytvořený / upravený / smazaný rozvrh se projeví do jednoho tiku.

```bash
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/schedules \
  -H 'Content-Type: application/json' \
  -d '{ "key": "nightly", "cron": "0 2 * * *",
        "options": { "mode": "Both" }, "gate": { "failOnAnyDifference": true } }'

# spusť teď (mimo rozvrh) -> 202 + jobId
curl -X POST http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/schedules/nightly/run
```

> **Multi-replika single-fire.** Každý tik nejdřív získá/obnoví sdílenou `automation`
> lease (`ILeaderElection`, tabulka `automation_leader`); reconciluje a spouští jen
> **vedoucí replika**, takže rozvrh vystřelí jednou napříč clusterem (in-memory fallback
> vede vždy). Při pádu vedoucího ji stand-by převezme do ~`AutomationLeader.Lease`; jeho
> reconciler se re-seedne bez spuštění, takže failover nezpůsobí dvojí start. (Lease se
> řídí DB hodinami, takže na clock-skew replik nezáleží.)

### Notifikace (odběry jako resource)

Po doběhnutí dávky vrátí `FinalizeBatchHandler` event `BatchFinished`, který
`BatchFinishedNotificationHandler` přemění na notifikaci a předá `NotificationDispatcher`.
Dispatcher (Scoped, čte `ISubscriptionStore.ListEnabledAsync`) profiltruje **aktivní
odběry v DB** podle události a volitelného `BranchKey` / `InstanceKey` a rozešle je
kanálem. Událost je `Completed` (brána prošla / žádná není), `GateViolated`, nebo `Failed`
(tvrdě spadlá úloha — viz „Spolehlivost & viditelnost" níže). Kanály:

- **`webhook`** — POST JSON na URL; payload má pole `text` (kompatibilní se Slack/Teams)
  a strukturovaný detail pod `diffpdf`.
- **`smtp`** — e-mail přes nakonfigurovaný SMTP server (`Notifications:Smtp` v appsettings).

Doručení je best-effort (selhání jednoho odběru neblokuje ostatní ani nezdrží dávku).

```bash
curl -X POST http://localhost:8080/api/v1/subscriptions \
  -H 'Content-Type: application/json' \
  -d '{ "channel": "webhook", "target": "https://hooks.slack.com/services/…",
        "events": [ "GateViolated", "Completed" ] }'
```

### On-demand triggery a folder-watch

Kromě cron rozvrhu lze dávku spustit **událostí**. Všechny tři cesty jdou přes stejný
`IBatchLauncher` (a tedy stejnou readiness bránu) jako rozvrh; spouští se s výchozími
volbami (`LaunchSpec.Default`), protože nemají kontext rozvrhu.

- **Webhook (jedna instance)** — `POST /api/v1/triggers/{branch}/{instance}` vrátí
  `LaunchResult` jako `TriggerResult` (`202` launched + jobId; `200` když není co
  porovnávat / nedostupné; `404` neznámý scope). SDK: `TriggerBatchAsync(...)`.
- **Fan-out přes větev** — `POST /api/v1/branches/{branch}/run` spustí každou enabled
  instanci a vrátí souhrn (`BranchRunResult`). SDK: `RunBranchAsync(...)`.
- **Folder-watch** — `FolderWatchService` (hosted service) každých ~15 s načte **aktivní
  watche z `IWatchStore`** (runtime resource, tabulka `folder_watches`, **jeden watch na
  instanci**) a skenuje `new/` přes `IFolderManifestScanner`; dávku spustí, jakmile se drop
  souborů **ustálí** (`StabilitySeconds`). Watche se spravují přes API (žádný appsettings,
  žádný restart — splňuje no-RDP požadavek); per-watch debounce stav (`WatchState`) je
  klíčovaný podle watch Id a reconcilovaný každý tik (nový watch → nový stav, smazaný →
  zahozen). Poll (ne `FileSystemWatcher`) funguje uniformně pro lokální, mountované i UNC/CIFS share.

```bash
# nastav (upsert) folder-watch instance přes API:
curl -X PUT http://localhost:8080/api/v1/branches/Alfa/instances/LamaEnergy/watch \
  -H 'Content-Type: application/json' -d '{ "stabilitySeconds": 30, "enabled": true }'
```

> Folder-watch sdílí **stejnou `automation` lease** jako plánovač — skenuje a spouští jen
> vedoucí replika. Per-folder dedupe je in-memory, takže změna vedení může poslední drop
> spustit jednou znovu; souběžné repliky ale nikdy nespustí dvakrát.

### Spolehlivost & viditelnost (Fáze 4)

- **Notifikace na `Failed`** — když job tvrdě spadne (jediná cesta: chyba indexace ve
  `IndexBatchHandler` → `jobStore.FailAsync`), handler vrátí event `BatchFailed`, který
  `BatchFailedNotificationHandler` přemění na notifikaci `NotificationEvent.Failed`. Odběr ji
  dostane jen pokud má `Failed` ve svých `Events`.
- **Historie běhů** — `IScheduleRunStore` + tabulka `schedule_runs`. Run-start zapisuje
  **`BatchLauncher` ještě před publikací příkazu** (přes `LaunchSpec.ScheduleId`), takže
  rychlá in-process / DB-local pipeline nemůže předběhnout zápis (jinak by patch podle `job_id`
  minul prázdný store). Výsledek dopatchuje `FinalizeBatchHandler` (Passed/GateViolated) a
  fail-path `IndexBatchHandler` (Failed) — patch je no-op pro joby bez rozvrhu (triggery,
  folder-watch). `job_id` **není** FK na `jobs`, takže historie přežije retenci.
- **Retence artefaktů** — `RetentionService` (leader-gated, sdílí `automation` lease, interval
  v hodinách) maže `reports/{jobId}` složky doběhnutých jobů starších než `RetentionDays`
  (`IJobStore.ListPrunableArtifactsAsync` + `MarkArtifactsPrunedAsync` přes značku
  `jobs.artifacts_pruned_at`, aby se staré joby neskenovaly donekonečna). DB řádky a historie
  běhů zůstávají; konfigurace `Retention`, ve výchozím stavu vypnuto.

## Build, testy a CI

### Lokální build & testy

```bash
dotnet build DiffPdf.slnx -c Debug
dotnet test  DiffPdf.slnx -c Debug
```

Unit testy (`DiffPdf.Core.Tests`) pokrývají engine, zarovnání, strictness, gate, stores,
plánovač (`ScheduleReconciler`) a notifikační dispatcher. Integrační testy
(`DiffPdf.Client.Tests`) jedou proti živé in-memory instanci API přes
`WebApplicationFactory<Program>` (in-memory store + in-process Wolverine, bez DB /
Ghostscriptu) a ověřují SDK ↔ API end-to-end vč. „create schedule → run-now → Completed →
report". Unit + WebApplicationFactory **neověří relační DDL** — to odzkoušej jednou ručně
přes `docker compose up` (Postgres/SqlServer; žádný broker).

### CI (GitHub Actions)

- **`.github/workflows/ci.yml`** — na každý push/PR: `restore` → `build` → `test`.
- **`.github/workflows/package.yml`** — na push do `main` a tag `v*`:
  - `dotnet pack` SDK → `.nupkg` jako artefakt (verze z tagu `vX.Y.Z`, jinak `0.0.0-ci.<run>`),
  - `docker build` API image → uloží jako artefakt (`docker save | gzip`).

  Bez publikace do NuGet feedu / registry — žádné secrets.
