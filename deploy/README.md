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

```powershell
.\deploy\install-service.ps1 -BinPath 'C:\DiffPdf\app\DiffPdf.Api.exe'
```

With a connection string and explicit DB dependency:

```powershell
.\deploy\install-service.ps1 -BinPath 'C:\DiffPdf\app\DiffPdf.Api.exe' `
    -ConnectionString 'Server=.;Database=diffpdf;Trusted_Connection=True;TrustServerCertificate=True' `
    -DependsOn 'MSSQLSERVER'
```

For `Production`, the script requires a SQL Server connection string unless you explicitly pass
`-AllowInMemoryProduction`. This prevents an accidental non-persistent in-memory service.

The service is registered with:

- **Delayed automatic start** — starts on boot, after eager-auto services, giving the database time to come up.
- **Dependency on the database service** (`-DependsOn`, default `MSSQLSERVER`) — SQL Server starts first after a reboot.
- **Auto-restart on failure** — restarts 5s after each of the first three failures.

On start the service waits for the database server, **creates the application database if missing**,
applies EF Core migrations, then begins serving — so a not-yet-ready database makes it wait rather than crash.

### Options

| Parameter | Default | Notes |
|---|---|---|
| `-BinPath` | (required) | Full path to the published `DiffPdf.Api.exe`. |
| `-Name` | `DiffPdfApi` | Service name. |
| `-StartupType` | `delayed-auto` | `delayed-auto`, `auto`, or `manual`. |
| `-DependsOn` | `MSSQLSERVER` | DB service to start first. Named instance: `MSSQL$INSTANCE`. `''` to skip. |
| `-ConnectionString` | — | Required for `Production` unless already stored on the service or `-AllowInMemoryProduction` is used; stored as `ConnectionStrings__SqlServer`. |
| `-ClearConnectionString` | (off) | Removes a previously stored service-scoped connection string. |
| `-Environment` | `Production` | ASP.NET Core environment stored as `ASPNETCORE_ENVIRONMENT`. |
| `-Url` | `http://0.0.0.0:5275` | Bind URL stored as `ASPNETCORE_URLS`. |
| `-AllowInMemoryProduction` | (off) | Explicitly permits production startup without SQL Server; intended only for short-lived/lab installs. |
| `-ServiceAccount` / `-ServicePassword` | LocalSystem | Optional logon account. |
| `-NoStart` | (off) | Install without starting. |

### Notes

- Run from an elevated prompt (the script enforces this).
- The service account needs **`CREATE DATABASE`** permission (role `dbcreator`), or pre-create the empty
  database and the startup gate just verifies reachability.
- Prefer an install path without spaces (e.g. `C:\DiffPdf\app`).
- Logs: `<install dir>\logs\diffpdf-*.log` (override with the `DIFFPDF_LOG_DIR` environment variable).
- Release artifacts created by `publish.ps1` omit `appsettings.Development.json` by default so local dev
  connection strings are not shipped to servers. Use `-IncludeDevelopmentSettings` only for a deliberate dev artifact.

## 3. Uninstall

```powershell
.\deploy\uninstall-service.ps1
```
