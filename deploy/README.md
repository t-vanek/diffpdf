# Deploying DiffPdf API as a Windows Service

The API runs as a normal console app and also supports the Windows Service Control Manager
(`AddWindowsService` in `Program.cs`). These scripts register it as an **auto-starting** service.

## 1. Publish

```powershell
dotnet publish src/DiffPdf.Api -c Release -o C:\DiffPdf\app
```

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
| `-ConnectionString` | — | Optional; stored as the service-scoped env var `ConnectionStrings__SqlServer`. |
| `-ServiceAccount` / `-ServicePassword` | LocalSystem | Optional logon account. |
| `-NoStart` | (off) | Install without starting. |

### Notes

- Run from an elevated prompt (the script enforces this).
- The service account needs **`CREATE DATABASE`** permission (role `dbcreator`), or pre-create the empty
  database and the startup gate just verifies reachability.
- Prefer an install path without spaces (e.g. `C:\DiffPdf\app`).
- Logs: `<install dir>\logs\diffpdf-*.log` (override with the `DIFFPDF_LOG_DIR` environment variable).

## 3. Uninstall

```powershell
.\deploy\uninstall-service.ps1
```
