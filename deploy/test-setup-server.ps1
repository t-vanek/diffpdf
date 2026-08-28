<#
.SYNOPSIS
Offline smoke tests for setup-server.ps1. Does not require elevation or touch Windows services.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$setupPath = Join-Path $PSScriptRoot 'setup-server.ps1'
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($setupPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "setup-server.ps1 has syntax errors: $($parseErrors -join [Environment]::NewLine)"
}

. $setupPath

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('diffpdf-setup-test-' + [guid]::NewGuid().ToString('N'))
$releaseDir = Join-Path $testRoot 'release'
$script:InstallDir = Join-Path $testRoot 'install'
$script:ProgramDataDir = Join-Path $testRoot 'program-data'
$backupDir = Join-Path $testRoot 'backup'

try {
    New-Item -ItemType Directory -Force -Path $releaseDir, $InstallDir, $ProgramDataDir, $backupDir | Out-Null
    Set-Content -LiteralPath (Join-Path $releaseDir 'DiffPdf.Api.exe') -Value 'new binary'
    Set-Content -LiteralPath (Join-Path $releaseDir 'appsettings.json') -Value '{ "release": true }'
    Set-Content -LiteralPath (Join-Path $releaseDir 'web.config') -Value '<release />'
    Set-Content -LiteralPath (Join-Path $InstallDir 'DiffPdf.Api.exe') -Value 'old binary'
    Set-Content -LiteralPath (Join-Path $InstallDir 'appsettings.json') -Value '{ "installed": true }'
    Set-Content -LiteralPath (Join-Path $InstallDir 'web.config') -Value '<installed />'

    $script:ConnectionString = 'Server=sql;Database=DiffPdf;Trusted_Connection=True;TrustServerCertificate=True'
    $script:Url = 'http://0.0.0.0:5275'
    $script:PublicUrl = 'https://diffpdf.example.test'
    $script:AllowInMemoryProduction = $false
    New-ProductionConfig

    $production = Get-Content -LiteralPath (Join-Path $InstallDir 'appsettings.Production.json') -Raw | ConvertFrom-Json
    if ($production.Urls -ne $Url) { throw 'New-ProductionConfig did not write Urls.' }
    if ($production.ConnectionStrings.SqlServer -ne $ConnectionString) { throw 'New-ProductionConfig did not write the SQL connection string.' }
    if ($production.Storage.RootPath -ne (Join-Path $ProgramDataDir 'storage')) { throw 'Storage:RootPath is incorrect.' }
    if ($production.FileManager.RootPath -ne (Join-Path $ProgramDataDir 'storage')) { throw 'FileManager:RootPath is incorrect.' }
    if ($production.ScopeSync.RootPath -ne (Join-Path $ProgramDataDir 'data')) { throw 'ScopeSync:RootPath is incorrect.' }
    if ($production.Notifications.BaseUrl -ne $PublicUrl) { throw 'Notifications:BaseUrl is incorrect.' }

    $installedAppSettings = Get-Content -LiteralPath (Join-Path $InstallDir 'appsettings.json') -Raw
    $installedWebConfig = Get-Content -LiteralPath (Join-Path $InstallDir 'web.config') -Raw
    Ensure-DataFolders
    Compare-ReleaseConfigs -ReleaseDir $releaseDir
    Invoke-Robocopy -From $releaseDir -To $InstallDir -ExcludeFiles $protectedConfigPatterns

    if ((Get-Content -LiteralPath (Join-Path $InstallDir 'appsettings.json') -Raw) -ne $installedAppSettings) {
        throw 'Update overwrote installed appsettings.json.'
    }
    if ((Get-Content -LiteralPath (Join-Path $InstallDir 'web.config') -Raw) -ne $installedWebConfig) {
        throw 'Update overwrote installed web.config.'
    }
    $incoming = @(Get-ChildItem -LiteralPath (Join-Path $ProgramDataDir 'config-review') -File -Recurse -Filter '*.incoming')
    if ($incoming.Count -lt 2) { throw 'Config-review did not keep incoming release configurations.' }

    Invoke-Robocopy -From $InstallDir -To $backupDir
    Set-Content -LiteralPath (Join-Path $InstallDir 'DiffPdf.Api.exe') -Value 'broken binary'
    Invoke-Robocopy -From $backupDir -To $InstallDir -Mirror
    if ((Get-Content -LiteralPath (Join-Path $InstallDir 'DiffPdf.Api.exe') -Raw).Trim() -ne 'new binary') {
        throw 'File rollback did not restore the backed-up application.'
    }

    Write-Host 'setup-server.ps1 smoke tests passed.' -ForegroundColor Green
}
finally {
    if ([System.IO.Directory]::Exists($testRoot)) { [System.IO.Directory]::Delete($testRoot, $true) }
}
