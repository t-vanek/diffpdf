# DiffPdf — návod pro nasazení serveru (ICT)

Tento dokument je provozní runbook pro IT oddělení: jak nasadit, nakonfigurovat, ověřit,
monitorovat a aktualizovat **DiffPdf server** na Windows Serveru. Produktový přehled je
v [../README.md](../README.md).

> **Cílová platforma:** Windows Server. Server běží jako **Windows služba** a hostuje API
> i workery v jednom procesu. Žádný externí message broker se neinstaluje — fronty jsou
> v SQL Serveru.

---

## 1. Přehled nasazení

```
                ┌──────────────────────────────────────────────┐
   klienti ───► │  Windows Server                              │
  (LAN, SDK,    │   ┌────────────────────────┐                 │
   desktop)     │   │ Služba „DiffPdf API"   │  HTTP :5275      │
                │   │ (DiffPdf.Api.exe)      │◄──── REST + SignalR
                │   └───────────┬────────────┘                 │
                │               │ in-process workery           │
                │               ▼                              │
                │      ┌─────────────────┐   ┌───────────────┐ │
                │      │ SQL Server      │   │ Úložiště PDF  │ │
                │      │ (zdroj pravdy,  │   │ old/new/      │ │
                │      │  durable fronty)│   │ reports/      │ │
                │      └─────────────────┘   └───────────────┘ │
                │               │                              │
                │               ▼ (volitelně)                 │
                │      Ghostscript (renderer)                  │
                └──────────────────────────────────────────────┘
                                │
                                ▼ SMTP (notifikace e-mailem)
```

**Co se nasazuje:**

| Komponenta | Kde | Poznámka |
|---|---|---|
| **DiffPdf server** | Windows služba `DiffPdfApi` | Self-contained `.exe` — **nevyžaduje .NET runtime** na serveru. |
| **SQL Server databáze** | lokální nebo síťová instance | Zdroj pravdy + durable fronty + OAuth. Vytvoří se automaticky při prvním startu. |
| **Úložiště PDF** | lokální disk / UNC sdílení | Stromy `<root>/<větev>/<instance>/{old,new,reports}`. |
| **Ghostscript** *(volitelné)* | na serveru | Jen pro vizuální render. Alternativa **PDFium** je vestavěná (nic se neinstaluje). |
| **SMTP** *(volitelné)* | firemní relay | Pro e-mailové notifikace. Nastavuje se za běhu z klienta. |
| **Desktopový klient** | stanice testerů | Self-contained `.exe`, jen rozbalit a spustit. |

---

## 2. Požadavky

### Hardware (doporučené minimum)

| Zdroj | Minimum | Doporučeno | Poznámka |
|---|---|---|---|
| **CPU** | 4 jádra | 8+ jader | Render PDF je CPU-náročný; paralelismus se ladí (viz [§5.4](#54-výkonové-ladění)). |
| **RAM** | 8 GB | 16+ GB | Render velkých PDF + cache dokumentů. |
| **Disk** | 50 GB | dle objemu | Reporty a diff-PDF rostou; řeší retence (viz [§7.3](#73-retence-artefaktů)). Doporučen SSD. |

### Software

- **Windows Server 2019 / 2022** (nebo Windows 10/11 pro pilotní provoz).
- **SQL Server 2019+** (Express stačí pro menší objemy; LocalDB jen pro test) — lokální instance nebo dosažitelná po síti.
- **Ghostscript** *(volitelné)* — jen pro vizuální režim renderu. Bez něj se použije vestavěný **PDFium**.
- **.NET runtime se NEINSTALUJE** — server se publikuje jako self-contained.

---

## 3. Příprava prostředí

Proveď **před** instalací služby.

### 3.1 SQL Server

Server si databázi i schéma vytvoří sám při prvním startu (čeká, až je DB dosažitelná,
založí ji, pokud chybí, a aplikuje EF Core migrace). Stačí připravit přístup:

**Varianta A — service účet smí zakládat databáze (nejjednodušší):**
přiřaď přihlašovacímu účtu služby roli `dbcreator`. Při startu si server založí prázdnou
databázi `diffpdf` a naplní schéma.

**Varianta B — DB předpřipraví DBA (řízenější):**
```sql
CREATE DATABASE diffpdf;
-- účet služby (Windows autentizace, doporučeno):
CREATE LOGIN [CORP\svc_diffpdf] FROM WINDOWS;
USE diffpdf;
CREATE USER [CORP\svc_diffpdf] FOR LOGIN [CORP\svc_diffpdf];
ALTER ROLE db_owner ADD MEMBER [CORP\svc_diffpdf];   -- kvůli automatickým migracím
```
> Server potřebuje `db_owner` (nebo ekvivalent), protože **migrace mění schéma** při každé
> aktualizaci, která přidá tabulku/sloupec. Bez práva na DDL aktualizace selže.

**Connection string** (předá se instalačnímu skriptu, viz [§4](#4-instalace)):
- Windows autentizace (doporučeno):
  `Server=SQLHOST;Database=diffpdf;Trusted_Connection=True;TrustServerCertificate=True`
- SQL autentizace:
  `Server=SQLHOST;Database=diffpdf;User Id=diffpdf;Password=…;TrustServerCertificate=True`

> `TrustServerCertificate=True` použij jen v interní síti bez ověřeného certifikátu SQL Serveru.

### 3.2 Service účet

Služba běží defaultně jako **LocalSystem**. Pro produkci doporučujeme **dedikovaný doménový
service účet** (`CORP\svc_diffpdf`), který má:

- přístup k **SQL Serveru** (viz §3.1),
- **čtení** na `old/` a `new/`, **zápis** na `reports/` v úložišti PDF,
- přístup k **síťovým sdílením** (pokud `basePath` ukazuje na UNC) — viz [§5.3](#53-síťové-složky-a-credentialy).

Účet předáš skriptu přes `-ServiceAccount` / `-ServicePassword`.

> **Tip:** Když `basePath` instancí ukazuje na UNC sdílení dostupná pod tímto účtem,
> nemusíš v aplikaci vůbec konfigurovat credentialy — sdílení se použijí „jak jsou".

### 3.3 Renderer (Ghostscript vs PDFium)

- **PDFium** (výchozí alternativa, BSD licence, **nic se neinstaluje**) — funguje out-of-the-box.
  V porovnávacích volbách `"renderer": "Pdfium"`.
- **Ghostscript** (výchozí, AGPL) — pokud chceš jeho render, nainstaluj ho a zpřístupni:
  - dej `gs` / `gswin64c.exe` na **systémovou** `PATH`, **nebo**
  - nastav env proměnnou `GHOSTSCRIPT_PATH` na plnou cestu k exe (čistší pro službu).
  - Ověření: `gs --version` (nebo `gswin64c --version`).

> Pozor: služba čte **strojovou** `PATH`, ne uživatelskou. Po změně PATH službu restartuj.
> Licenčně: distribuce uzavřeného produktu s Ghostscriptem vyžaduje komerční licenci Artifexu
> — interní serverové nasazení je v pořádku; jinak použij PDFium.

### 3.4 Úložiště PDF

Server pracuje se stromem `<root>/<větev>/<instance>/{old,new,reports}`. Kořen nastavíš v
`ScopeSync:RootPath` v `appsettings.Production.json`. Když je kořen
nastavený, **`basePath` instancí se odvodí automaticky** a server složky `old/new/reports`
sám založí a opraví. Do `reports/` aplikace **zapisuje**, `old/new` jen **čte**.

### 3.5 Firewall a porty

| Port | Směr | Účel |
|---|---|---|
| **5275/TCP** | příchozí | REST API + SignalR (`Urls` v `appsettings.Production.json`). Otevři pro stanice klientů v LAN. |
| **5276/UDP** | příchozí | LAN auto-discovery (desktop klient si najde server). Volitelné — lze vypnout `Discovery:Enabled=false`. |
| **1433/TCP** | odchozí | SQL Server (pokud je na jiném stroji). |
| **25 / 587/TCP** | odchozí | SMTP relay (pokud používáš e-mailové notifikace). |

```powershell
New-NetFirewallRule -DisplayName "DiffPdf API (HTTP 5275)" -Direction Inbound `
  -Protocol TCP -LocalPort 5275 -Action Allow
New-NetFirewallRule -DisplayName "DiffPdf discovery (UDP 5276)" -Direction Inbound `
  -Protocol UDP -LocalPort 5276 -Action Allow
```

> **TLS:** služba binduje **HTTP**. Pro produkci s autentizací postav před server
> **reverzní proxy** (IIS / nginx) s TLS terminací, nebo nastav HTTPS binding v
> `Urls` / `Kestrel` v `appsettings.Production.json`. Viz [§6.3](#63-tls-a-reverzní-proxy).

---

## 4. Instalace

### 4.1 Release artefakty

Server nebo klienta publikuješ na buildovacím stroji (s .NET 10 SDK) skriptem, stáhneš
z GitHub Release, nebo ručně v GitHub Actions spustíš samostatný bundle workflow:

1. GitHub → **Actions** → **Server Bundle** nebo **Client Bundle** → **Run workflow**.
2. Volitelně vyplň `version`; prázdné pole vytvoří automatickou admin/client verzi.
3. Po doběhnutí stáhni `DiffPdf-Server-...zip` nebo `DiffPdf-Client-...zip` z GitHub
   **Releases**. Stejný ZIP je dostupný i jako artefakt na stránce běhu workflow.

```powershell
# vytvoří publish/DiffPdf-Server-1.2.3-win-x64.zip + DiffPdf-Client-1.2.3-win-x64.zip
.\deploy\publish.ps1 -Version 1.2.3
# jen serverový zip pro admin deployment
.\deploy\publish.ps1 -Version 1.2.3 -ServerOnly
# jen klientský zip pro testery/uživatele
.\deploy\publish.ps1 -Version 1.2.3 -ClientOnly
```

Server zip obsahuje publikovaný `DiffPdf.Api.exe` (self-contained) + skripty
`setup-server.ps1`, `install-service.ps1`, `uninstall-service.ps1`, `update-service.ps1`.
Klient zip je jeden `.exe` pro testery.

> Tag `v*` v Gitu spustí workflow `release.yml`, který oba zipy připne k GitHub Release.
> Ruční workflow `server-bundle.yml` a `client-bundle.yml` vytvoří vlastní GitHub Release
> s tagem `server-bundle-v...` nebo `client-bundle-v...`, aby nekolidovaly s oficiálními `v*` releasy.

### 4.2 Instalace služby

Na serveru v **elevated PowerShellu** spusť hlavní admin skript. Buď necháš skript stáhnout
server ZIP z GitHub Releases:

```powershell
.\setup-server.ps1 -Mode Install -Version latest `
    -SqlServer 'SQLHOST' -Database 'diffpdf' `
    -ServiceAccount 'CORP\svc_diffpdf' -ServicePassword (Read-Host -AsSecureString 'Heslo service účtu')
```

Nebo použiješ už stažený server ZIP a cestu k němu zadáš explicitně:

```powershell
.\setup-server.ps1 -Mode Install -SourceZip '.\DiffPdf-Server-1.2.3-win-x64.zip' `
    -SqlServer 'SQLHOST' -Database 'diffpdf'
```

`-Source` je zachovaný jen jako kompatibilní alias pro `-SourceZip`. Lokální složka ani
rozbalený repozitář nejsou podporovaný vstup pro `setup-server.ps1`; admin musí předat
serverový ZIP vytvořený release/server bundle workflow.

**Co `setup-server.ps1` udělá:**

- stáhne serverový ZIP z GitHub Releases, pokud nezadáš `-SourceZip`,
- vytvoří `C:\ProgramData\DiffPdf\{data,storage,logs,backups}`,
- rozbalí/nakopíruje server do `C:\DiffPdf\app`,
- zaregistruje službu `DiffPdfApi` (display name *DiffPdf API*) s **delayed-auto** startem,
- nastaví **závislost na SQL Serveru** (`-DependsOn`, default `MSSQLSERVER`; named instance `MSSQL$INSTANCE`),
- nastaví **automatický restart** 5 s po každém z prvních tří pádů,
- zapíše provozní hodnoty do `appsettings.Production.json`: `Urls` a `ConnectionStrings:SqlServer`,
- nastaví storage/log/data cesty podle `-ProgramDataDir`,
- volitelně přidá firewall pravidlo pro HTTP port,
- spustí `/health` a `/health/ready`,
- odstraní staré service-scoped hodnoty `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`
  a `ConnectionStrings__SqlServer`, pokud ve službě zůstaly z dřívější instalace,
- odmítne produkční instalaci bez SQL connection stringu, pokud výslovně nepovolíš
  `-AllowInMemoryProduction`,
- spustí službu (pokud nezadáš `-NoStart`).

> Provozní konfigurace žije v **`appsettings.Production.json` vedle `DiffPdf.Api.exe`**.
> `update-service.ps1` při aktualizaci zachová celý lokální production config a zapíše ho zpět
> do nové verze instalace. Skript je idempotentní: re-run bez `-ConnectionString`
> ponechá hodnotu, která už v production configu je.

`install-service.ps1` zůstává dostupný jako nízkoúrovňový fallback, když už máš soubory
ručně připravené v instalační složce.

**Hlavní parametry `setup-server.ps1`:**

| Parametr | Default | Význam |
|---|---|---|
| `-Mode` | `Install` | `Install`, `Update`, `Repair`, `Diagnose`. |
| `-Version` | `latest` | Verze server ZIPu z GitHub Releases; `latest` vybere nejnovější server asset. |
| `-SourceZip` | — | Lokální server ZIP; když chybí, stáhne se release z GitHubu. `-Source` je kompatibilní alias. |
| `-InstallDir` | `C:\DiffPdf\app` | Instalační složka služby. |
| `-ProgramDataDir` | `C:\ProgramData\DiffPdf` | Kořen pro `data`, `storage`, `logs`, `backups`. |
| `-SqlServer` / `-Database` | — / `diffpdf` | Skript z nich složí `ConnectionStrings:SqlServer`. |
| `-ConnectionString` | — | Hotový connection string, pokud ho nechceš skládat z parametrů. |
| `-ServiceAccount` / `-ServicePassword` | LocalSystem | Doménový service účet (doporučeno). |
| `-Url` | `http://0.0.0.0:5275` | Bind adresa (`Urls` v `appsettings.Production.json`). |
| `-PublicUrl` | `http://localhost:5275` | URL pro smoke testy a odkazy v notifikacích. |
| `-AllowInMemoryProduction` | (vyp.) | Nouzově/laboratorně povolí produkční start bez SQL Serveru; data nejsou perzistentní. |
| `-DependsOn` | `MSSQLSERVER` | DB služba, která má startovat dřív. `''` = bez závislosti. |
| `-NoFirewall` | (vyp.) | Nepřidá firewall pravidlo. |
| `-NoStart` | (vyp.) | Nainstaluje bez spuštění. |

---

## 5. Konfigurace

Provozní konfigurace je v **`appsettings.Production.json`**. Instalační skript do něj zapisuje
`Urls` a `ConnectionStrings:SqlServer`; ostatní sekce může upravit admin přímo v souboru.
Soubor obsahuje i citlivé hodnoty, pokud je tam doplníš, proto omez ACL instalační složky na
Administrators a service účet.

### 5.1 Logování (`appsettings.Production.json`)

V produkci nemá služba konzoli, takže Serilog píše do **denně rotovaného souboru**:

```
C:\ProgramData\DiffPdf\logs\diffpdf-YYYYMMDD.log   (14 dní historie)
```

Cesta je v `appsettings.Production.json` (sekce `Serilog`). Adresář se vytvoří automaticky;
chceš-li jinam, uprav `path` v tom souboru.

### 5.2 Přehled konfiguračních sekcí

| Sekce / klíč | Default | Význam |
|---|---|---|
| `ConnectionStrings:SqlServer` | — (dev: in-memory) | SQL Server. Bez něj jede neperzistentní dev režim — **v produkci povinné**. |
| `Urls` | `http://0.0.0.0:5275` | Bind adresa(y) serveru. |
| `Auth:Enabled` | `false` | Zapne OAuth2 (vyžaduje DB). Viz [§6](#6-zabezpečení). |
| `ScopeSync:RootPath` | `D:\diffpdf` | Kořen úložiště `old/new/reports`. |
| `ScopeSync:AutoRegister` | `true` | Automaticky registruje větve/instance nalezené na disku. |
| `Network` | — | Profily credentialů a aliasy sdílení (viz §5.3). |
| `Notifications:BaseUrl` | `http://localhost:8080` | URL pro odkazy v e-mailech — **nastav na reálnou adresu serveru**. |
| `Notifications:Smtp` | — | Fallback SMTP, dokud se nenastaví z klienta (Konfigurace → E-mail). |
| `Automation:TickSeconds` | `20` | Perioda plánovače automatizací. |
| `Automation:MaxConcurrentRuns` | `4` | Souběžné běhy automatizací (na leader replice). |
| `Automation:AutoProvision:Enabled` | `true` | Auto-zakládání standardních automatizací (zdraví, readiness, retence, structure-sync). |
| `Automation:AutoProvision:RetentionDays` | `30` | Stáří, po kterém se mažou artefakty reportů. |
| `Discovery:Enabled` / `Port` | `true` / `5276` | LAN auto-discovery (UDP). |
| `FileManager:RootPath` | (prázdné → ScopeSync) | Kořen pro „Správa souborů" v klientovi. |
| `FileManager:MaxUploadSizeMB` | `256` | Limit uploadu jednoho souboru. |
| `StuckJobWatchdog:Enabled` | `true` | Hlídač zaseknutých úloh (alert, nezasahuje). |
| `StuckJobWatchdog:StallThresholdMinutes` | `30` | Práh „bez postupu" pro alert. |
| `NotificationDelivery:MaxAttempts` | `5` | Počet pokusů o doručení e-mailu, pak dead-letter. |
| `NotificationDelivery:IntervalSeconds` | `15` | Perioda odesílacího workeru outboxu. |

### 5.3 Síťové složky a credentialy

`basePath` instance může být lokální cesta, **UNC** (`\\server\share\...`) nebo alias
`share:<jméno>` definovaný v sekci `Network`. Mechanismus:

- **Nejjednodušší:** UNC sdílení dostupné pod **service účtem** → žádné credentialy v aplikaci.
- **Pojmenované profily a aliasy** v `appsettings`:

```jsonc
"Network": {
  "AllowInlineCredentials": true,
  "CredentialProfiles": {
    "corp": { "username": "svc_diff", "password": "…", "domain": "CORP" }
  },
  "Shares": {
    "reports": { "root": "\\\\fileserver\\reports", "credentialProfile": "corp",
                 "description": "Tiskové reporty" }
  }
}
```

Pak `basePath: "share:reports"`. Windows připojí sdílení přes `WNetAddConnection2` (jako
`net use`, bez mapování disku) a po doběhnutí odpojí. Credentialy **nikdy nekončí v logu ani
reportu**. Heslo v `appsettings` drž mimo repo (env / chráněný soubor).

### 5.4 Výkonové ladění

Stropy hot path (env, např. `Worker__MaxFilePairsPerJob=4`):

| Klíč | Default | Význam |
|---|---|---|
| `Worker:MaxConcurrentJobs` × `Worker:MaxFilePairsPerJob` | `2` × `2` | Součin = strop souběžně porovnávaných dvojic. Zvyš podle jader. |
| `Worker:MaxConcurrentPdfOperations` | `4` | Procesový semafor souběžných renderů (ochrana CPU/RAM). |
| `Worker:FilePairComparisonTimeoutMinutes` | `10` | Tvrdý timeout porovnání jedné dvojice. |
| `Worker:MaxFilePairAttempts` | `3` | Počet pokusů na dvojici při transientní chybě. |
| `Worker:MaxPdfSizeBytes` | 500 MB | Pre-flight strop velikosti jednoho PDF. |
| `Worker:ReclaimOrphansOnStartup` | `true` | Při startu zotaví rozpracované dvojice. **Single-instance:** ponech `true`; **multi-replika:** nastav `false`. |

> Začni s defaulty. Když render saturuje CPU, snižuj `MaxConcurrentPdfOperations`; když máš
> hodně jader a I/O rezervu, zvyšuj součin `MaxConcurrentJobs × MaxFilePairsPerJob`.

---

## 6. Zabezpečení

### 6.1 Zapnutí autentizace (OAuth2, M2M)

Ve výchozím stavu je auth **vypnuté** (API běží anonymně). V produkci doporučeno zapnout:

1. Ujisti se, že je nakonfigurovaná **DB** (auth se aktivuje jen s relačním connection stringem).
2. Nastav sekci `Auth` v produkčním `appsettings.Production.json`:
   ```json
   "Auth": { "Enabled": true, "ClientId": "diffpdf-ci", "ClientSecret": "<silné heslo>", "Scope": "diffpdf.api", "AccessTokenMinutes": 60 }
   ```
   Soubor obsahuje secret, proto musí být instalační složka chráněná ACL.
3. Po zapnutí **každý endpoint vyžaduje bearer token**; výjimky jsou `/`, `/health`,
   `/health/ready`, OpenAPI a `/connect/token`.

Klient (SDK / desktop) si token obstará client-credentials flow sám:
```csharp
services.AddDiffPdfClient(new Uri("https://…"), "diffpdf-ci", "<secret>", "diffpdf.api");
```
V desktopovém klientovi zadáš ClientId/Secret v ozubeném kolečku.

### 6.2 Doporučení

- **Service účet s minimálními právy** (jen potřebné složky + DB), ne LocalSystem.
- **Secret a hesla** pouze v serverovém `appsettings.Production.json` nebo chráněném úložišti,
  nikdy v repu; instalační složku omez na Administrators + service účet.
- **Síť:** API vystav jen do interní LAN; veřejně jen za reverzní proxy s TLS a autentizací.
- **Discovery (UDP 5276)** vypni, pokud ho nepoužíváš (`Discovery:Enabled=false`).

### 6.3 TLS a reverzní proxy

Služba binduje HTTP. Pro šifrovaný provoz:

- **IIS / ARR** nebo **nginx** před serverem s TLS terminací → forward na `http://localhost:5275`.
  Povol forwardování WebSocketů (SignalR `/hubs/jobs`).
- nebo přímý HTTPS: nastav `Urls` / případně sekci `Kestrel` v `appsettings.Production.json`
  a ulož certifikát na serveru s ACL pro service účet.

---

## 7. Provoz a monitoring

### 7.1 Zdravotní endpointy

| Endpoint | Auth | Pro co |
|---|---|---|
| `GET /health` | ne | **Liveness** — vždy `200` (`status`, `version`, `uptimeSeconds`), nezávislé na DB. Pro load-balancer / watchdog. |
| `GET /health/ready` | ne | **Readiness** — `200` ready / `503` degraded; kontroluje DB, renderer a zápis do úložiště. Pro monitoring. |
| `GET /api/v1/status` | ano* | Bohatý dashboard — leader, backlog fronty, heartbeaty služeb, závislosti. (*auth jen když je zapnutá.) |
| `GET /metrics` | ne | Prometheus / OpenTelemetry metriky (fronta, doba úloh, render fáze, doručení notifikací, dead-letter). |

Příklad smoke testu po instalaci:
```powershell
Invoke-RestMethod http://localhost:5275/health           # status=Healthy
Invoke-RestMethod http://localhost:5275/health/ready      # 200 + checks (DB/renderer/storage)
```

### 7.2 Logy a události

- **Soubor:** `C:\ProgramData\DiffPdf\logs\diffpdf-*.log` (14 dní).
- **Event log Windows:** start/stop služby a fatální chyby (zdroj *DiffPdfApi*).
- **Aplikační event log:** trvalý feed v DB (`/api/v1/events`) — doběhlé porovnání, běhy
  automatizací, **nedoručené e-maily**, zotavení po pádu. V desktopu zvoneček (centrum notifikací).
- **Metriky k hlídání:** `diffpdf.notifications.deadletter` (>0 = e-maily se nedoručují),
  `diffpdf.jobs.stuck`, hloubka fronty.

### 7.3 Retence artefaktů

Údržbová automatizace maže staré `reports/{jobId}` složky (default po **30 dnech** —
`Automation:AutoProvision:RetentionDays`). DB řádky a historie běhů zůstávají; promazává je
automatizace „Úklid databáze". Obojí se spravuje za běhu v sekci **Automatizace** klienta.

### 7.4 Zálohy

| Co zálohovat | Jak | Proč |
|---|---|---|
| **SQL Server databáze `diffpdf`** | standardní SQL backup (full + log) | **Zdroj pravdy** — větve, instance, automatizace, odběry, úlohy, event log, fronty, OAuth. Toto je kritická záloha. |
| **Úložiště `reports/`** | dle potřeby | Diff-PDF a reporty jsou **reprodukovatelné** (lze přegenerovat); zálohuj jen pokud je potřebuješ jako důkaz. `old/new` jsou vstupy z jiných systémů. |
| **`appsettings.Production.json`** | s konfigurací serveru | Drobné, ale ušetří rekonstrukci. |

---

## 8. Aktualizace serveru

Bezpečná, s automatickým rollbackem při nenaběhnutí:

```powershell
.\setup-server.ps1 -Mode Update -Version latest
```

Nebo z už staženého ZIPu:

```powershell
.\setup-server.ps1 -Mode Update -SourceZip '.\DiffPdf-Server-1.2.3-win-x64.zip'
```

`setup-server.ps1` stáhne serverový ZIP z GitHub Releases, případně použije `-SourceZip`,
a předá ho `update-service.ps1`.
Low-level varianta s ručním ZIPem zůstává:

```powershell
.\update-service.ps1 -InstallDir 'C:\DiffPdf\app' -Source '.\DiffPdf-Server-1.2.3-win-x64.zip'
```

**Co update udělá:** zastaví službu → **zazálohuje** aktuální složku (do `..\backups\…`) →
nakopíruje novou verzi → spustí a počká na `Running`. Když nová verze do 60 s nenaběhne,
**vrátí zálohu** a službu nastartuje zpět. `appsettings.Production.json` update skript zachová
celý a přenese ho do nové verze instalace.

> **Migrace DB** se aplikují automaticky při startu nové verze. Před velkou aktualizací
> v produkci pořiď **zálohu DB** (rollback skriptu vrátí binárky, ne schéma databáze).

---

## 9. Odinstalace

```powershell
.\uninstall-service.ps1            # zastaví a odebere službu DiffPdfApi
```

Skript jen odregistruje službu. Databáze, úložiště a logy zůstávají — smaž je ručně, pokud je
nechceš zachovat.

---

## 10. Řešení potíží

| Příznak | Pravděpodobná příčina | Řešení |
|---|---|---|
| Služba se rozběhne a hned spadne | Nedosažitelná DB / chybí práva | Zkontroluj connection string a práva účtu (`db_owner`/`dbcreator`). Server na nedostupnou DB **čeká**, ale na chybu práv při migraci spadne. Viz log. |
| `/health/ready` vrací `503` | Renderer / úložiště / DB nedostupné | Tělo odpovědi ukáže který check selhal. Renderer: ověř `gs --version` nebo přepni na PDFium. Úložiště: práva na zápis do `ScopeSync:RootPath`. |
| Vizuální diff selhává | Chybí Ghostscript | Nainstaluj GS a dej na strojovou PATH / `GHOSTSCRIPT_PATH`, **restartuj službu**, nebo použij `"renderer": "Pdfium"`. |
| Klient nenajde server | Discovery vypnuté / blokovaný UDP | Zadej URL ručně v ozubeném kolečku, nebo otevři UDP 5276 a zapni `Discovery:Enabled`. |
| Klient: `401 Unauthorized` | Zapnutá auth bez tokenu | Zadej ClientId/Secret v klientovi; ověř sekci `Auth` a secret v env. |
| E-maily nechodí | SMTP nenastaveno / chybné | Klient → Konfigurace → E-mail: nastav SMTP a pošli test. **Historie doručení** ukáže důvod; dead-letter řádky lze „Poslat znovu". |
| Úloha „Selhalo" | Poškozené PDF / nedostupná složka | ⓘ v řádku úlohy ukáže důvod; jeden vadný soubor je `Error`, dávku nezabije. Zkontroluj `old/new`. |
| Aktualizace selhala | Nová verze nenaběhla | Skript **automaticky vrátil zálohu** — server běží na předchozí verzi. Zkontroluj log nové verze, oprav, opakuj. |
| Vysoké CPU | Příliš souběžných renderů | Sniž `Worker:MaxConcurrentPdfOperations` / součin `MaxConcurrentJobs × MaxFilePairsPerJob`. |

**Užitečné příkazy:**
```powershell
Get-Service DiffPdfApi                                   # stav služby
Get-Content C:\ProgramData\DiffPdf\logs\diffpdf-*.log -Tail 50   # konec logu
Restart-Service DiffPdfApi                               # restart
Invoke-RestMethod http://localhost:5275/health/ready     # readiness vč. checků
```

---

## Příloha — kontrolní seznam nasazení

- [ ] SQL Server dostupný; účet má práva (§3.1)
- [ ] Service účet připraven, práva na složky + sdílení (§3.2)
- [ ] Renderer rozhodnut (Ghostscript na PATH, nebo PDFium) (§3.3)
- [ ] Úložiště (`ScopeSync:RootPath`) existuje, service účet zapisuje (§3.4)
- [ ] Firewall: 5275/TCP (a 5276/UDP) otevřen (§3.5)
- [ ] Server publikován a rozbalen do cesty bez mezer (§4.1)
- [ ] `install-service.ps1` proběhl, služba `Running` (§4.2)
- [ ] `Notifications:BaseUrl` nastaveno na reálnou adresu (§5.2)
- [ ] `/health` a `/health/ready` vrací OK (§7.1)
- [ ] (Produkce) Auth zapnuté, secret v env, TLS přes proxy (§6)
- [ ] Zálohy DB naplánovány (§7.4)
- [ ] Smoke test: vytvoř větev/instanci, spusť trigger, zkontroluj report
