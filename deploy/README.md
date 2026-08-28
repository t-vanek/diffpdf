# Deploying DiffPdf API as a Windows Service

> **Kompletní provozní runbook pro ICT** (česky — požadavky, SQL Server, service účet,
> firewall, konfigurace, monitoring, zálohy, řešení potíží) je v **[../docs/NASAZENI.md](../docs/NASAZENI.md)**.
> Tento soubor je jen stručná referenční příručka k deployment skriptům.

The API runs as a normal console app and also supports the Windows Service Control Manager
(`AddWindowsService` in `Program.cs`). These scripts register it as an **auto-starting** service.

## 1. Publish

```powershell
dotnet publish src/DiffPdf.Api -c Release -o C:\DiffPdf\app
```

Admins can also download a ready ZIP from GitHub Actions:

1. Open **Actions** → **Server Bundle** → **Run workflow**.
2. Download `DiffPdf-Server-...zip` from GitHub **Releases**; the completed workflow
   run also keeps the same ZIP as an artifact.

## 2. Install (run from an elevated PowerShell prompt)

The server ZIP contains `setup-server.ps1`. Extract the ZIP and run it without parameters:

```powershell
.\setup-server.ps1
```

The script asks for the operation (`Install`, `Update`, `Repair`, `Diagnose`), release source,
application/data directories, SQL authentication, service identity, URLs, firewall and final health check.
It shows the complete plan before making changes. It does not call any other deployment script.

For unattended automation the same script accepts parameters:

```powershell
.\setup-server.ps1 -NonInteractive -Mode Install `
    -SourceZip '.\DiffPdf-Server-1.2.3-win-x64.zip' `
    -InstallDir 'D:\DiffPdf\app' -ProgramDataDir 'D:\DiffPdf\data' `
    -SqlServer 'SQLHOST' -Database 'DiffPdf'
```

`-SourceZip` accepts a local server ZIP or an expanded release directory; `-Version latest` downloads
the newest matching GitHub release. `-Source` remains an alias for `-SourceZip`.

Updates preserve every installed `appsettings*.json` and `web.config`. Different incoming configs are
saved as `.incoming` files under `config-review`; the update makes a full backup and rolls back files if
copy, service start or liveness fails. `Repair` changes only service registration, recovery, firewall and
legacy service environment overrides after validating the production JSON.

The local SQL service dependency is empty by default, which is correct for remote SQL Server. Specify
`MSSQLSERVER` (or `MSSQL$INSTANCE`) only when SQL runs as a Windows service on the same machine.

The SCM-facing service reaches `Running` without waiting for SQL. Its background supervisor reports a
missing database connection to the Windows Application event log, keeps retrying, creates the database
when permitted and then starts the API. The rolling file log remains the detailed application log.

### Main automation parameters

| Parameter | Default | Notes |
|---|---|---|
| `-Mode` | `Install` with `-NonInteractive` | `Install`, `Update`, `Repair`, or `Diagnose`. |
| `-Version` / `-SourceZip` | `latest` with `-NonInteractive` | GitHub version, local ZIP, or expanded release directory. |
| `-InstallDir` | `<script>\app` | Application directory; interactive mode always shows and allows changing it. |
| `-ProgramDataDir` | `<script>\data` | Root for data, storage, logs, backups and config review. |
| `-SqlServer` / `-Database` | — / `DiffPdf` | Builds `ConnectionStrings:SqlServer`; Windows auth when `-SqlUser` is empty. |
| `-ConnectionString` | — | Complete connection string alternative. |
| `-ServiceName` / `-DisplayName` | `DiffPdfApi` / `DiffPdf API` | Internal and displayed service names. |
| `-StartupType` | `delayed-auto` | `delayed-auto`, `auto`, or `manual`. |
| `-DependsOn` | empty | Local SQL Windows service only; keep empty for remote SQL. |
| `-Url` / `-PublicUrl` | `0.0.0.0:5275` / `localhost:5275` | Listener and client/health/notification URLs. |
| `-AllowInMemoryProduction` | off | Explicit non-persistent laboratory mode. |
| `-NoFirewall` / `-NoStart` | off | Skip firewall or final service start. |

Release artifacts omit `appsettings.Development.json` unless `-IncludeDevelopmentSettings` is explicitly
used during publishing.

## 3. Uninstall

```powershell
Stop-Service DiffPdfApi
sc.exe delete DiffPdfApi
```

The repository's `uninstall-service.ps1` remains a developer/admin convenience, but it is not required
or included in the server release ZIP.
