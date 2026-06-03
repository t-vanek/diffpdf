<#
.SYNOPSIS
    Stops and removes the DiffPdf API Windows Service.

.EXAMPLE
    .\uninstall-service.ps1
#>
[CmdletBinding()]
param(
    [string] $Name = 'DiffPdfApi'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated (Administrator) PowerShell prompt.'
}

$service = Get-Service -Name $Name -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$Name' is not installed; nothing to do." -ForegroundColor Yellow
    return
}

if ($service.Status -ne 'Stopped') {
    Write-Host "Stopping service '$Name'." -ForegroundColor Yellow
    Stop-Service -Name $Name -Force
}

$output = & sc.exe delete $Name
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe delete $Name failed (exit code $LASTEXITCODE): $output"
}

Write-Host "Service '$Name' removed." -ForegroundColor Green
