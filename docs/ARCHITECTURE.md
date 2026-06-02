# Architektura a flow aplikace

Tenhle dokument je mapa: jak teče požadavek systémem, kde co žije a jak do sebe
zapadají jednotlivé funkce. Detaily konfigurace jsou v [README](../README.md);
tady jde o **tok a odpovědnosti**.

## 1. Mapa projektů

Závislosti míří dovnitř — všechno staví na `DiffPdf.Core`.

```
DiffPdf.Core            doménový model + čistá logika (žádné IO frameworky)
  ├─ Comparison         engine, párování složek, zarovnání stránek, diff
  ├─ Models             ComparisonJob, BatchComparisonRequest, výsledky, scope
  ├─ Network            resolver sdílení + credential profily + náhled dávky
  ├─ Discovery          UDP protokol + popis serveru (sdílené s klientem)
  ├─ Storage            cesty/úložiště artefaktů
  └─ Abstractions       rozhraní (engine, renderer, stores, progress…)

DiffPdf.Pdf        → Core    renderery (Ghostscript/PDFium), Skia, writery, konektor sdílení
DiffPdf.Persistence→ Core    rozhraní stores + in-memory implementace
  .Postgres / .SqlServer     EF Core implementace (zdroj pravdy v produkci)
DiffPdf.Messaging  → Core,Persistence,Worker,Wolverine   handlery dávkové pipeline
DiffPdf.Worker     → Core    worker options, identita, provisioning úložiště
DiffPdf.Api        → vše     HTTP/SignalR vrstva, auth, discovery responder, kompozice
DiffPdf.Client     → Core    klientské SDK (discovery, REST, OAuth, SignalR)
```

`DiffPdf.Api/Program.cs` je jediné místo, kde se to celé skládá dohromady a kde
se rozhoduje mezi produkčním a vývojovým režimem (viz §4).

## 2. Vstupní body (co server vystavuje)

| Povrch | Endpoint | K čemu |
| --- | --- | --- |
| **Sync porovnání** | `POST /api/v1/comparisons` | jedna dvojice PDF, odpoví hned výsledkem |
| **Async dávka** | `POST /api/v1/batch` → `GET /api/v1/jobs/{id}` | složka vs složka, durable job |
| **Scope** | `/api/v1/business-instances[...]` | business instance + projekty |
| **Náhled dávky** | `POST /api/v1/discovery/folder \| preview`, `GET …/shares` | dry-run párování (viz §6b) |
| **Server discovery** | UDP `:41234` + `GET /api/v1/server-info` | najdi server v síti (viz §6a) |
| **Auth** | `/connect/token \| authorize \| userinfo \| revocation \| logout` | OAuth2/OIDC (viz §7) |
| **Realtime** | SignalR hub `/hubs/jobs` | živý progress úloh |

## 3. Tok jedné dávky (durable pipeline) — hlavní flow

Tohle je srdce aplikace. Dávka se rozpadne na zprávy, které řídí **Wolverine**;
**PostgreSQL/SQL Server je zdroj pravdy**, RabbitMQ je jen transport.

```
Klient
  │  POST /api/v1/batch   (BatchComparisonRequest)
  ▼
[BatchEndpoints]
  │  1. validuje scope (business instance + projekt)
  │  2. NetworkShareResolver: rozbalí alias share:… a credential profil
  │     → request nese konkrétní cestu (+ creds); aliasy/profily zmizí
  │  3. uloží ComparisonJob + publikuje RunBatchComparison   (jedna transakce = outbox)
  ▼
RabbitMQ ──► [Worker / Wolverine handlery]
  │
  │  RunBatchComparison  → TryStart: Queued→Running (optimistic concurrency, lease)
  │                         └─ publikuje IndexBatch
  │  IndexBatch          → FolderPairing.Pair(old,new): založí file_pair_tasks,
  │                         nastaví Total, pro každý pár publikuje CompareFilePair
  │  CompareFilePair ×N  → claim tasku → ComparisonEngine.CompareAsync → zapíše
  │                         FilePairResult, ++processed; poslední spustí Finalize
  │  FinalizeBatch       → zagreguje výsledky do BatchComparisonReport, job→Completed
  ▼
PostgreSQL (stav, report)  +  storage (artefakty: zvýrazněná diff-PDF)
  ▼
SignalR (živý progress)   ·   REST polling (GET /jobs/{id}) = zdroj pravdy
```

Vlastnosti, na kterých to stojí:

- **Idempotence** — každý handler znovu-doručenou zprávu pozná (job už není
  `Queued`, task už je claimnutý) a přeskočí.
- **Optimistic concurrency + lease** — `Queued → Running` provede jen jeden worker;
  spadlé tasky vyzvedne `StaleTaskRecoveryService`.
- **Per-pár odolnost** — transientní chyba se opakuje s cooldownem, pak se zapíše
  jako `Error` (dávka se nezasekne).

## 4. Dva režimy nasazení (rozcestí v Program.cs)

```
Jsou nastavené ConnectionStrings (Postgres|SqlServer) i RabbitMq?
  ├─ ANO → PRODUKCE: relační stores (EF) + Wolverine durable přes RabbitMQ
  └─ NE  → DEV FALLBACK: in-memory stores + Wolverine lokální (in-process) transport
```

Flow zpráv (§3) je v **obou** režimech stejný — liší se jen úložiště a transport.
Dev fallback je to, co běží v testech a při `dotnet run` bez DB.

## 5. Síťový přístup ke složkám

Dvě role, ať se nepletou:

- **`NetworkShareResolver`** (Core) — *překlad*: `share:<jméno>/<sub>` → konkrétní
  cesta, `credentialProfile` → reálné credentialy, plus politiky (zákaz inline
  hesel, traversal guard). Volá se **při submitu dávky** a v náhledu (§6b).
- **`PlatformShareConnector`** (Pdf) — *připojení*: UNC s credentialy přimountuje
  (Windows `WNetAddConnection2`, Linux CIFS) a po `Dispose` odpojí; lokální/už
  namountované cesty propustí beze změny.

Důležité omezení: **durable pipeline (§3) sama nemountuje** — file-pair tasky
běží na různých workerech, takže cesta musí být stabilní napříč nimi
(`localMountPath`, mapovaný disk, nebo UNC pod service účtem). Runtime mount
s credentialy se reálně děje jen v náhledu (§6b) a v legacy `BatchComparer` (§8).

## 6. Dvě „discovery" — NEPLÉST

Slovo „discovery" nese dvě různé věci. Jsou oddělené záměrně:

### 6a) Server discovery — „najdi server v síti"
- **Kde:** `DiffPdf.Core/Discovery` (protokol, popis) + `DiffPdf.Api/Discovery`
  (UDP responder, builder popisu) + `GET /api/v1/server-info`.
- **K čemu:** klient (např. WPF) pošle UDP probe → server odpoví
  `DiffPdfServerDescriptor` (jméno, base URL, port, verze, auth). Bez napevno
  zadané adresy.

### 6b) Náhled dávky (dry-run) — „co by se porovnávalo"
- **Kde:** `DiffPdf.Core/Network/NetworkDiscoveryService` + `/api/v1/discovery/*`.
- **K čemu:** před odesláním dávky ověř dostupnost cest, počet PDF a párování
  old/new — **bez jediného porovnání**. Sdílí resolver (§5) s dávkou.

> Pojmenování je matoucí (obojí „discovery"). Návrh narovnání je v §9.

## 7. Autentizace (OAuth2 / OIDC)

Zapnutá přes `Auth:Enabled` (vyžaduje relační DB — OpenIddict tam drží klienty a
tokeny). Pak **každý endpoint vyžaduje bearer** kromě `/health`, OAuth endpointů a
OpenAPI.

```
AuthSetup.AddDiffPdfAuth        konfigurace OpenIddict serveru + validace + cookie scheme
  ├─ client-credentials (M2M)   seedovaný confidential klient (CI/testeři)
  └─ authorization-code + PKCE   seedovaný public klient (interaktivní)
       └─ refresh-token grant + rotace, revocation, userinfo, end-session

token endpoint (MapTokenEndpoint)        — vydá token pro všechny 3 granty
InteractiveAuthEndpoints                  — /connect/authorize, /account/login
                                            (hash hesel, antiforgery, rate-limit),
                                            userinfo, logout
OpenIddictClientSeeder                    — při startu vytvoří oba klienty
```

Validaci flow pokrývají integrační testy (`DiffPdf.Api.Tests/AuthFlowTests`) přes
SQLite — včetně plného auth-code + PKCE + refresh + revocation.

## 8. Klientské SDK (`DiffPdf.Client`)

Zrcadlí povrchy serveru pro .NET klienty (WPF…):

```
DiffPdfDiscoveryClient   UDP probe → najde servery (§6a)
DiffPdfClient            typovaný REST: OAuth (CC i auth-code+PKCE), scope, dávky,
                         polling, report, artefakty, náhled (§6b)
DiffPdfLiveProgress      SignalR /hubs/jobs → živý progress
```

## 9. Legacy / zdroje chaosu (kandidáti na úklid)

- **`BatchComparer` / `IBatchComparer`** (Core+Pdf DI) — synchronní in-process
  varianta dávky. **Nikdo ji nevolá** (durable pipeline jede přes handlery §3).
  Drží se jen kvůli DI registraci. → *kandidát na smazání* nebo jasné označení.
- **Kolize názvu „discovery"** (§6a vs §6b) — náhled dávky by se klidně mohl
  jmenovat `preview` (`/api/v1/preview`, `BatchPreviewService`), aby „discovery"
  zůstalo jen pro hledání serveru.
- **`NetworkDiscoveryService` žije v `Core/Network`**, ale `Discovery` (server)
  v `Core/Discovery` — po přejmenování náhledu by namespacy seděly k rolím.
```
