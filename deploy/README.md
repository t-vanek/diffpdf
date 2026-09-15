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

### Missing branches or instances in the client's file manager

New installations configure a single absolute `DataRoot` (`<ProgramDataDir>`). The API derives
`data` for branches, instances and the file manager, and `storage` for comparison artifacts.
A nonempty `DataRoot` overrides all three legacy root settings, including environment overrides of
those legacy keys. Set `DataRoot` itself (or the `DataRoot` environment variable) to change the base.
When `DataRoot` is absent or empty, legacy configuration is preserved: the file manager uses
`FileManager:RootPath`, falling back to `ScopeSync:RootPath` when empty.
Earlier versions of `setup-server.ps1` incorrectly pointed the file manager at `storage`.

For an existing installation, check **Konfigurace → Správa souborů** in the client
(or `GET /api/v1/files/status`) for the effective root and its configuration source.
If it points at `storage` while the branches live under `ScopeSync:RootPath`, back up the installed
`appsettings.Production.json` and set `FileManager:RootPath` to `""`. Keep `ScopeSync:RootPath`
pointing at the existing branch/instance tree. Restart the API service and refresh the server panel
at its root (empty path). A custom file-manager root should only be changed if the managed instance
tree is the intended location. Instance paths outside that tree are not exposed by the file manager.

Updating the binaries or running `Repair` preserves the installed configuration, so it does not fix
this setting on an existing server. To adopt `DataRoot`, first verify that the existing instance tree
is exactly `<DataRoot>\data` and artifacts are under `<DataRoot>\storage`, then back up the config,
set `DataRoot`, restart, and check `/api/v1/files/status`. This does not move files or rewrite stored
instance BasePath values. Custom layouts can continue using the legacy settings with empty `DataRoot`.

The status endpoint probes both directory listing (`readable`) and writing (`writable`). Missing
roots are reported as unavailable; diagnostics and file-manager requests do not create them.
ScopeSync with AutoCreateFolders enabled still provisions its configured tree during an apply run.

For an existing UNC BasePath that represents the same location as a server-local root, explicitly
configure both `Network:Shares:<name>:Root` (UNC) and `LocalMountPath` (local absolute path).
For example, map `\\d3s-diffpdf\DiffPdfData` to the verified server-local directory backing that share.
The resolver matches complete directory boundaries, prefers the longest matching root, and uses
the local mount for both share aliases and matching UNC paths. It never infers a mapping from the
hostname. This preserves stored instance identities and makes managed-folder protection consistent.

The file manager resolves configured share aliases through the same network resolver as ScopeSync.
When using the ScopeSync fallback it also inherits `ScopeSync:CredentialProfile`; an explicit
file-manager root can use `FileManager:CredentialProfile` or the alias's default profile.
Managed branch/instance folders and their required `old`, `new`, and `reports` directories cannot
be renamed, moved or deleted through the file manager (HTTP 403). Existing symbolic links and
junctions are rejected in file-manager paths and skipped in listings, search and copies.

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
