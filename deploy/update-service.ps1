<#
.SYNOPSIS
    Updates an installed DiffPdf API Windows Service in place, with backup + automatic rollback.

.DESCRIPTION
    Stops the service, backs up the current install directory, copies the new published files over it,
    then starts the service and waits until it reports Running. If it fails to start within the timeout,
    the previous files are restored from the backup and the service is started again, so a bad build never
    leaves the server down. Operational config (connection string, environment, bind URL) lives in
    service-scoped environment variables set by install-service.ps1, so it survives the file update.
    Must be run from an elevated (Administrator) prompt.

.EXAMPLE
    .\update-service.ps1 -InstallDir 'C:\DiffPdf\app' -Source '.\publish\server'

.EXAMPLE
    .\update-service.ps1 -InstallDir 'C:\DiffPdf\app' -Source '.\DiffPdf-Server-1.2.3-win-x64.zip'
#>
[CmdletBinding()]
param(
    # Directory the service currently runs from (contains DiffPdf.Api.exe).
    [Parameter(Mandatory = $true)][string] $InstallDir,

    # New build to deploy: a published folder or a .zip produced by publish.ps1.
    [Parameter(Mandatory = $true)][string] $Source,

    [string] $Name = 'DiffPdfApi',

    # Where timestamped backups are kept (default: <InstallDir>\..\backups).
    [string] $BackupRoot,

    [int] $StartTimeoutSeconds = 60,

    # Production must normally use SQL Server. This switch permits the in-memory fallback intentionally.
    [switch] $AllowInMemoryProduction,

    # Keeps appsettings.Development.json in the install directory even when the service is not Development.
    [switch] $KeepDevelopmentSettings
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated (Administrator) PowerShell prompt.'
    }
}

function Invoke-Robocopy {
    param([string] $From, [string] $To, [switch] $Mirror)
    $opts = @('/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:2', '/W:1')
    if ($Mirror) { $opts = @('/MIR') + $opts[1..($opts.Length - 1)] }
    & robocopy $From $To @opts | Out-Null
    # robocopy exit codes < 8 are success (0 = nothing copied, 1 = files copied, 2/3 = extras, …).
    if ($LASTEXITCODE -ge 8) { throw "robocopy '$From' -> '$To' failed (exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Wait-Running {
    param([string] $ServiceName, [int] $TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $s = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($s.Status -eq 'Running') { return $true }
        Start-Sleep -Seconds 2
    }
    return ((Get-Service -Name $ServiceName).Status -eq 'Running')
}

function Get-ServiceEnvironment {
    param([string] $ServiceName)

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $env = [ordered]@{}
    $current = (Get-ItemProperty -Path $serviceKey -Name 'Environment' -ErrorAction SilentlyContinue).Environment
    foreach ($entry in @($current)) {
        if ($entry -match '^(.*?)=(.*)$') { $env[$matches[1]] = $matches[2] }
    }
    return ,$env
}

Assert-Administrator

$svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
if (-not $svc) { throw "Service '$Name' is not installed. Use install-service.ps1 first." }

$serviceEnv = Get-ServiceEnvironment -ServiceName $Name
$environment = if ($serviceEnv.Contains('ASPNETCORE_ENVIRONMENT') -and -not [string]::IsNullOrWhiteSpace($serviceEnv['ASPNETCORE_ENVIRONMENT'])) { $serviceEnv['ASPNETCORE_ENVIRONMENT'] } else { 'Production' }
$hasConn = $serviceEnv.Contains('ConnectionStrings__SqlServer') -and -not [string]::IsNullOrWhiteSpace($serviceEnv['ConnectionStrings__SqlServer'])

if ($environment -eq 'Production' -and -not $hasConn -and -not $AllowInMemoryProduction) {
    throw "Production service '$Name' has no service-scoped ConnectionStrings__SqlServer. Re-run install-service.ps1 with -ConnectionString before updating, or use -AllowInMemoryProduction only for an intentional non-persistent install."
}

if ($environment -eq 'Production' -and $hasConn -and $serviceEnv['ConnectionStrings__SqlServer'] -match '(?i)(^|;)\s*TrustServerCertificate\s*=\s*True\s*(;|$)') {
    Write-Warning "The production connection string uses TrustServerCertificate=True. Prefer a SQL Server certificate trusted by this host and remove that flag before long-term operation."
}

$InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
if (-not (Test-Path -LiteralPath $InstallDir -PathType Container)) { throw "InstallDir '$InstallDir' not found." }

# Resolve the source to a folder (expanding a .zip into a temp dir if needed).
$srcResolved = (Resolve-Path -LiteralPath $Source).Path
$tempExtract = $null
if ($srcResolved.ToLowerInvariant().EndsWith('.zip')) {
    $tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) ("diffpdf-update-" + [guid]::NewGuid().ToString('N'))
    Write-Host "Expanding '$srcResolved' -> '$tempExtract'." -ForegroundColor Cyan
    Expand-Archive -LiteralPath $srcResolved -DestinationPath $tempExtract -Force
    $sourceDir = $tempExtract
}
else {
    if (-not (Test-Path -LiteralPath $srcResolved -PathType Container)) { throw "Source '$srcResolved' is neither a folder nor a .zip." }
    $sourceDir = $srcResolved
}

if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) { $backupRootResolved = $BackupRoot }
else { $backupRootResolved = Join-Path (Split-Path -Parent $InstallDir) 'backups' }
$backupDir = Join-Path $backupRootResolved ("$Name-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

try {
    Write-Host "Backing up '$InstallDir' -> '$backupDir'." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    Invoke-Robocopy -From $InstallDir -To $backupDir

    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping service '$Name'." -ForegroundColor Yellow
        Stop-Service -Name $Name -Force
    }

    Write-Host "Copying new files into '$InstallDir'." -ForegroundColor Cyan
    Invoke-Robocopy -From $sourceDir -To $InstallDir

    $developmentSettings = Join-Path $InstallDir 'appsettings.Development.json'
    if ($environment -ne 'Development' -and -not $KeepDevelopmentSettings -and (Test-Path -LiteralPath $developmentSettings -PathType Leaf)) {
        Remove-Item -LiteralPath $developmentSettings -Force
        Write-Host "Removed appsettings.Development.json from the non-development install directory." -ForegroundColor Cyan
    }

    Write-Host "Starting service '$Name'." -ForegroundColor Cyan
    Start-Service -Name $Name

    if (Wait-Running -ServiceName $Name -TimeoutSeconds $StartTimeoutSeconds) {
        Write-Host "Update complete - service '$Name' is Running. Backup kept at '$backupDir'." -ForegroundColor Green
    }
    else {
        throw "Service '$Name' did not reach Running within $StartTimeoutSeconds s after the update."
    }
}
catch {
    Write-Warning "Update failed: $($_.Exception.Message)"
    Write-Host "Rolling back from '$backupDir'." -ForegroundColor Yellow
    try { if ((Get-Service -Name $Name).Status -ne 'Stopped') { Stop-Service -Name $Name -Force } } catch { }
    if (Test-Path -LiteralPath $backupDir) {
        Invoke-Robocopy -From $backupDir -To $InstallDir -Mirror
        Start-Service -Name $Name
        Write-Host "Rolled back to the previous build and restarted '$Name'." -ForegroundColor Green
    }
    throw
}
finally {
    if ($tempExtract -and (Test-Path -LiteralPath $tempExtract)) { Remove-Item $tempExtract -Recurse -Force }
}
