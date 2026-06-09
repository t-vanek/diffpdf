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
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0-dev',
    [string] $Runtime = 'win-x64',
    [string] $OutDir = 'publish',
    [string] $Configuration = 'Release',
    # Self-contained (bundle the .NET runtime, no prerequisite) vs framework-dependent (smaller, needs runtime).
    [bool] $SelfContained = $true
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot   # deploy/ lives directly under the repo root

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
    New-Item -ItemType Directory -Force -Path $serverDir, $clientDir | Out-Null

    $scArg = "--self-contained=$($SelfContained.ToString().ToLowerInvariant())"

    Write-Host "Publishing server (API / Windows Service) -> $serverDir" -ForegroundColor Cyan
    Invoke-Dotnet @(
        'publish', 'src/DiffPdf.Api/DiffPdf.Api.csproj',
        '-c', $Configuration, '-r', $Runtime, $scArg,
        "-p:Version=$Version", '-p:PublishReadyToRun=true',
        '-o', $serverDir
    )
    # Bundle the operational scripts alongside the server binaries.
    foreach ($script in 'install-service.ps1', 'uninstall-service.ps1', 'update-service.ps1') {
        $src = Join-Path $PSScriptRoot $script
        if (Test-Path $src) { Copy-Item $src $serverDir }
    }

    Write-Host "Publishing desktop client (single-file) -> $clientDir" -ForegroundColor Cyan
    Invoke-Dotnet @(
        'publish', 'src/DiffPdf.DesktopUI/DiffPdf.DesktopUI.csproj',
        '-c', $Configuration, '-r', $Runtime, $scArg,
        "-p:Version=$Version",
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $clientDir
    )

    $serverZip = Join-Path $out "DiffPdf-Server-$Version-$Runtime.zip"
    $clientZip = Join-Path $out "DiffPdf-Client-$Version-$Runtime.zip"
    Compress-Archive -Path (Join-Path $serverDir '*') -DestinationPath $serverZip -Force
    Compress-Archive -Path (Join-Path $clientDir '*') -DestinationPath $clientZip -Force

    Write-Host "`nDone. Artifacts:" -ForegroundColor Green
    Write-Host "  $serverZip"
    Write-Host "  $clientZip"
}
finally {
    Pop-Location
}
