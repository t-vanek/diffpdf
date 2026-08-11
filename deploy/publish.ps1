<#
.SYNOPSIS
    Publishes the DiffPdf server (API / Windows Service) and the desktop client as self-contained
    win-x64 bundles and zips them for release.

.DESCRIPTION
    Produces two artifacts under <OutDir>:
      * server/  + DiffPdf-Server-<version>-<rid>.zip  — the API published as a self-contained folder
        (so no .NET runtime is required on the server), bundled with the install/uninstall/update scripts.
      * client/  + DiffPdf-Client-<version>-<rid>.zip  — the desktop client published self-contained as a
        single .exe (testers just unzip and run; no runtime prerequisite).
    Used by the Release workflow (.github/workflows/release.yml) and runnable locally for a manual release.

.EXAMPLE
    .\deploy\publish.ps1 -Version 1.2.3

.EXAMPLE
    .\deploy\publish.ps1 -Version 1.2.3 -Runtime win-x64 -OutDir publish -SelfContained:$false

.EXAMPLE
    .\deploy\publish.ps1 -Version 1.2.3 -IncludeDevelopmentSettings

.EXAMPLE
    .\deploy\publish.ps1 -Version 1.2.3 -ServerOnly

.EXAMPLE
    .\deploy\publish.ps1 -Version 1.2.3 -ClientOnly
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0-dev',
    [string] $Runtime = 'win-x64',
    [string] $OutDir = 'publish',
    [string] $Configuration = 'Release',
    # Self-contained (bundle the .NET runtime, no prerequisite) vs framework-dependent (smaller, needs runtime).
    [bool] $SelfContained = $true,
    # Release server artifacts should not contain development-only config such as local connection strings.
    [switch] $IncludeDevelopmentSettings,
    # Publish only the API / Windows Service bundle.
    [switch] $ServerOnly,
    # Publish only the desktop client bundle.
    [switch] $ClientOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot   # deploy/ lives directly under the repo root

if ($ServerOnly -and $ClientOnly) {
    throw 'Use either -ServerOnly or -ClientOnly, not both.'
}

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]] $DotnetArgs)
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs -join ' ') failed (exit code $LASTEXITCODE)." }
}

Push-Location $repoRoot
try {
    $out = Join-Path $repoRoot $OutDir
    $serverDir = Join-Path $out 'server'
    $clientDir = Join-Path $out 'client'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    if (-not $ClientOnly) { New-Item -ItemType Directory -Force -Path $serverDir | Out-Null }
    if (-not $ServerOnly) { New-Item -ItemType Directory -Force -Path $clientDir | Out-Null }

    $scArg = "--self-contained=$($SelfContained.ToString().ToLowerInvariant())"

    if (-not $ClientOnly) {
        Write-Host "Publishing server (API / Windows Service) -> $serverDir" -ForegroundColor Cyan
        Invoke-Dotnet @(
            'publish', 'src/DiffPdf.Api/DiffPdf.Api.csproj',
            '-c', $Configuration, '-r', $Runtime, $scArg,
            "-p:Version=$Version", '-p:PublishReadyToRun=true',
            '-o', $serverDir
        )
        if (-not $IncludeDevelopmentSettings) {
            $developmentSettings = Join-Path $serverDir 'appsettings.Development.json'
            if (Test-Path -LiteralPath $developmentSettings -PathType Leaf) {
                Remove-Item -LiteralPath $developmentSettings -Force
                Write-Host "Removed appsettings.Development.json from the server artifact." -ForegroundColor Cyan
            }
        }
        # Bundle the operational scripts alongside the server binaries.
        foreach ($script in 'setup-server.ps1', 'install-service.ps1', 'uninstall-service.ps1', 'update-service.ps1') {
            $src = Join-Path $PSScriptRoot $script
            if (Test-Path $src) { Copy-Item $src $serverDir }
        }
    }

    if (-not $ServerOnly) {
        Write-Host "Publishing desktop client (single-file) -> $clientDir" -ForegroundColor Cyan
        Invoke-Dotnet @(
            'publish', 'src/DiffPdf.DesktopUI/DiffPdf.DesktopUI.csproj',
            '-c', $Configuration, '-r', $Runtime, $scArg,
            "-p:Version=$Version",
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-o', $clientDir
        )
    }

    $serverZip = Join-Path $out "DiffPdf-Server-$Version-$Runtime.zip"
    $clientZip = Join-Path $out "DiffPdf-Client-$Version-$Runtime.zip"
    if (-not $ClientOnly) { Compress-Archive -Path (Join-Path $serverDir '*') -DestinationPath $serverZip -Force }
    if (-not $ServerOnly) { Compress-Archive -Path (Join-Path $clientDir '*') -DestinationPath $clientZip -Force }

    Write-Host "`nDone. Artifacts:" -ForegroundColor Green
    if (-not $ClientOnly) { Write-Host "  $serverZip" }
    if (-not $ServerOnly) { Write-Host "  $clientZip" }
}
finally {
    Pop-Location
}
