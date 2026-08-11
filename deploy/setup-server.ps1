<#
.SYNOPSIS
    One-command setup, update, repair and diagnostics for the DiffPdf API Windows Service.

.DESCRIPTION
    This is the admin-friendly entry point. It can download a server ZIP from GitHub Releases,
    prepare ProgramData folders, install/update the Windows Service, configure
    appsettings.Production.json, add firewall rules and run health checks.

.EXAMPLE
    .\setup-server.ps1 -Mode Install -Version latest -SqlServer SQLHOST -Database diffpdf

.EXAMPLE
    .\setup-server.ps1 -Mode Install -SourceZip .\DiffPdf-Server-1.2.3-win-x64.zip -SqlServer SQLHOST -Database diffpdf

.EXAMPLE
    .\setup-server.ps1 -Mode Update -Version latest

.EXAMPLE
    .\setup-server.ps1 -Mode Diagnose
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Update', 'Repair', 'Diagnose')]
    [string] $Mode = 'Install',

    [string] $Repository = 't-vanek/diffpdf',
    [string] $Version = 'latest',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    # Local server ZIP. When omitted, the script downloads DiffPdf-Server-<version>-<runtime>.zip from GitHub Releases.
    [Alias('Source')]
    [string] $SourceZip,

    [string] $InstallDir = 'C:\DiffPdf\app',
    [string] $ProgramDataDir = 'C:\ProgramData\DiffPdf',
    [string] $ServiceName = 'DiffPdfApi',
    [string] $DisplayName = 'DiffPdf API',
    [string] $DependsOn = 'MSSQLSERVER',

    # Urls stored in appsettings.Production.json. PublicUrl is used for smoke checks and notification links.
    [string] $Url = 'http://0.0.0.0:5275',
    [string] $PublicUrl = 'http://localhost:5275',

    # Provide either a complete connection string, or SqlServer/Database (+ optional SqlUser).
    [string] $ConnectionString,
    [string] $SqlServer,
    [string] $Database = 'diffpdf',
    [string] $SqlUser,
    [System.Security.SecureString] $SqlPassword,
    [switch] $TrustServerCertificate,
    [switch] $AllowInMemoryProduction,

    [string] $ServiceAccount,
    [System.Security.SecureString] $ServicePassword,

    [switch] $NoFirewall,
    [switch] $NoStart,
    [int] $StartTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated (Administrator) PowerShell prompt.'
    }
}

function ConvertFrom-SecureStringToPlainText {
    param([System.Security.SecureString] $Value)
    if (-not $Value) { return $null }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function ConvertTo-JsonStringLiteral {
    param([AllowNull()][string] $Value)
    if ($null -eq $Value) { return 'null' }
    return ($Value | ConvertTo-Json -Compress)
}

function Invoke-Robocopy {
    param([string] $From, [string] $To)
    $opts = @('/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:2', '/W:1')
    & robocopy $From $To @opts | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy '$From' -> '$To' failed (exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Remove-DirectoryIfExists {
    param([AllowNull()][string] $Path)
    if ($Path -and [System.IO.Directory]::Exists($Path)) {
        [System.IO.Directory]::Delete($Path, $true)
    }
}

function New-ConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) { return $ConnectionString }
    if ([string]::IsNullOrWhiteSpace($SqlServer)) { return '' }

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Server'] = $SqlServer
    $builder['Database'] = $Database
    if ([string]::IsNullOrWhiteSpace($SqlUser)) {
        $builder['Trusted_Connection'] = 'True'
    }
    else {
        if (-not $SqlPassword) {
            $SqlPassword = Read-Host -Prompt "Password for SQL user '$SqlUser'" -AsSecureString
        }
        $builder['User Id'] = $SqlUser
        $builder['Password'] = ConvertFrom-SecureStringToPlainText $SqlPassword
    }
    $builder['TrustServerCertificate'] = $TrustServerCertificate.IsPresent.ToString()
    return $builder.ConnectionString
}

function Get-ServerBundle {
    param([string] $Repo, [string] $RequestedVersion, [string] $Rid)

    if (-not [string]::IsNullOrWhiteSpace($SourceZip)) {
        $resolved = (Resolve-Path -LiteralPath $SourceZip).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Local source must be a server ZIP file, not a folder: '$resolved'."
        }
        if (-not $resolved.ToLowerInvariant().EndsWith('.zip')) {
            throw "Local source must be a .zip file produced by the release/server bundle workflow: '$resolved'."
        }
        return @{ Path = $resolved; Temp = $null }
    }

    $headers = @{ 'User-Agent' = 'DiffPdf-Setup' }
    $releasesUri = "https://api.github.com/repos/$Repo/releases?per_page=100"
    Write-Host "Looking up server bundle in GitHub Releases: $Repo" -ForegroundColor Cyan
    $releases = Invoke-RestMethod -Uri $releasesUri -Headers $headers
    if (-not $releases) { throw "No releases found in $Repo." }

    $asset = $null
    if ($RequestedVersion -eq 'latest') {
        $asset = $releases |
            Sort-Object -Property published_at -Descending |
            ForEach-Object { $_.assets } |
            Where-Object { $_.name -like "DiffPdf-Server-*-$Rid.zip" } |
            Select-Object -First 1
    }
    else {
        $assetName = "DiffPdf-Server-$RequestedVersion-$Rid.zip"
        $asset = $releases |
            ForEach-Object { $_.assets } |
            Where-Object { $_.name -eq $assetName } |
            Select-Object -First 1
    }

    if (-not $asset) {
        throw "Could not find a server bundle for version '$RequestedVersion' and runtime '$Rid'."
    }

    $downloadDir = Join-Path ([System.IO.Path]::GetTempPath()) ("diffpdf-download-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
    $zipPath = Join-Path $downloadDir $asset.name
    Write-Host "Downloading $($asset.name)." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers $headers
    return @{ Path = $zipPath; Temp = $downloadDir }
}

function Expand-BundleIfNeeded {
    param([string] $Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        throw "Source '$resolved' is a folder. Pass a server ZIP with -SourceZip, or omit -SourceZip to download it from GitHub Releases."
    }
    if (-not $resolved.ToLowerInvariant().EndsWith('.zip')) {
        throw "Source '$resolved' is not a .zip file. Pass DiffPdf-Server-<version>-<runtime>.zip."
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("diffpdf-setup-" + [guid]::NewGuid().ToString('N'))
    Write-Host "Expanding '$resolved' -> '$temp'." -ForegroundColor Cyan
    Expand-Archive -LiteralPath $resolved -DestinationPath $temp -Force
    return @{ Path = $temp; Temp = $temp }
}

function Ensure-ProgramDataFolders {
    foreach ($sub in @('', 'data', 'storage', 'logs', 'backups')) {
        $path = if ($sub) { Join-Path $ProgramDataDir $sub } else { $ProgramDataDir }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function Set-NestedStringSetting {
    param([string] $Content, [string] $Section, [string] $Name, [AllowNull()][string] $Value)
    $literal = ConvertTo-JsonStringLiteral $Value
    $pattern = '("' + [regex]::Escape($Section) + '"\s*:\s*\{(?:(?!\n\s*\}).)*?"' + [regex]::Escape($Name) + '"\s*:\s*)(?:"(?:\\.|[^"\\])*"|null)'
    $regex = [regex]::new($pattern, [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($Content)) { return $Content }
    return $regex.Replace($Content, "`$1$literal", 1)
}

function Set-FirstStringSetting {
    param([string] $Content, [string] $Name, [AllowNull()][string] $Value)
    $literal = ConvertTo-JsonStringLiteral $Value
    $pattern = '("' + [regex]::Escape($Name) + '"\s*:\s*)(?:"(?:\\.|[^"\\])*"|null)'
    $regex = [regex]::new($pattern)
    if (-not $regex.IsMatch($Content)) { return $Content }
    return $regex.Replace($Content, "`$1$literal", 1)
}

function Update-ProductionConfig {
    $configPath = Join-Path $InstallDir 'appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "appsettings.Production.json was not found in '$InstallDir'."
    }

    $content = Get-Content -LiteralPath $configPath -Raw
    $content = Set-NestedStringSetting -Content $content -Section 'Storage' -Name 'RootPath' -Value (Join-Path $ProgramDataDir 'storage')
    $content = Set-NestedStringSetting -Content $content -Section 'ScopeSync' -Name 'RootPath' -Value (Join-Path $ProgramDataDir 'data')
    $content = Set-NestedStringSetting -Content $content -Section 'Notifications' -Name 'BaseUrl' -Value $PublicUrl
    $content = Set-FirstStringSetting -Content $content -Name 'path' -Value (Join-Path (Join-Path $ProgramDataDir 'logs') 'diffpdf-.log')
    Set-Content -LiteralPath $configPath -Value $content -Encoding UTF8
}

function Set-FirewallRule {
    if ($NoFirewall) { return }
    $uri = [Uri]($Url.Split(';')[0])
    $port = $uri.Port
    $name = "DiffPdf API (HTTP $port)"
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null
        Write-Host "Created firewall rule '$name' for TCP $port." -ForegroundColor Green
    }
}

function Test-Port {
    try {
        $uri = [Uri]($Url.Split(';')[0])
        $listeners = Get-NetTCPConnection -LocalPort $uri.Port -State Listen -ErrorAction SilentlyContinue
        if ($listeners) {
            Write-Warning "Port $($uri.Port) is already listening. If DiffPdf is already installed, this is expected during repair/update."
        }
    }
    catch {
        Write-Warning "Could not inspect listening port from Url '$Url': $($_.Exception.Message)"
    }
}

function Invoke-HealthCheck {
    if ($NoStart) { return }
    $health = $PublicUrl.TrimEnd('/') + '/health'
    $ready = $PublicUrl.TrimEnd('/') + '/health/ready'
    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $health -TimeoutSec 5 | Out-Null
            Write-Host "Health OK: $health" -ForegroundColor Green
            try {
                Invoke-RestMethod -Uri $ready -TimeoutSec 5 | Out-Null
                Write-Host "Readiness OK: $ready" -ForegroundColor Green
            }
            catch {
                Write-Warning "Liveness is OK, readiness is not ready yet: $($_.Exception.Message)"
            }
            return
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }
    Write-Warning "Health check did not respond within $StartTimeoutSeconds seconds: $health"
    Show-RecentLogs -Lines 80
}

function Show-RecentLogs {
    param([int] $Lines = 40)
    $logDir = Join-Path $ProgramDataDir 'logs'
    $latest = Get-ChildItem -LiteralPath $logDir -Filter 'diffpdf-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($latest) {
        Write-Host "Recent log lines from $($latest.FullName):" -ForegroundColor Cyan
        Get-Content -LiteralPath $latest.FullName -Tail $Lines
    }
}

function Invoke-Install {
    Assert-Administrator
    Ensure-ProgramDataFolders
    Test-Port

    $bundle = Get-ServerBundle -Repo $Repository -RequestedVersion $Version -Rid $Runtime
    $expanded = Expand-BundleIfNeeded -Path $bundle.Path
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            Write-Host "Stopping existing service '$ServiceName' before copying files." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force
        }

        $existingConfigPath = Join-Path $InstallDir 'appsettings.Production.json'
        $existingConfig = if (Test-Path -LiteralPath $existingConfigPath -PathType Leaf) {
            Get-Content -LiteralPath $existingConfigPath -Raw
        }
        else {
            $null
        }

        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Invoke-Robocopy -From $expanded.Path -To $InstallDir
        if ($null -ne $existingConfig) {
            Set-Content -LiteralPath $existingConfigPath -Value $existingConfig -Encoding UTF8
            Write-Host "Preserved existing appsettings.Production.json." -ForegroundColor Cyan
        }
        Update-ProductionConfig

        $installScript = Join-Path $InstallDir 'install-service.ps1'
        if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
            throw "The bundle does not contain install-service.ps1."
        }

        $conn = New-ConnectionString
        $args = @(
            '-BinPath', (Join-Path $InstallDir 'DiffPdf.Api.exe'),
            '-Name', $ServiceName,
            '-DisplayName', $DisplayName,
            '-DependsOn', $DependsOn,
            '-Url', $Url
        )
        if (-not [string]::IsNullOrWhiteSpace($conn)) { $args += @('-ConnectionString', $conn) }
        if ($AllowInMemoryProduction) { $args += '-AllowInMemoryProduction' }
        if ($ServiceAccount) { $args += @('-ServiceAccount', $ServiceAccount) }
        if ($ServicePassword) { $args += @('-ServicePassword', $ServicePassword) }
        if ($NoStart) { $args += '-NoStart' }

        & $installScript @args
        if (-not $?) { throw 'install-service.ps1 failed.' }
        Set-FirewallRule
        Invoke-HealthCheck
    }
    finally {
        Remove-DirectoryIfExists -Path $expanded.Temp
        Remove-DirectoryIfExists -Path $bundle.Temp
    }
}

function Invoke-Update {
    Assert-Administrator
    Ensure-ProgramDataFolders
    $bundle = Get-ServerBundle -Repo $Repository -RequestedVersion $Version -Rid $Runtime
    try {
        $updateScript = Join-Path $InstallDir 'update-service.ps1'
        if (-not (Test-Path -LiteralPath $updateScript -PathType Leaf)) {
            throw "update-service.ps1 was not found in '$InstallDir'. Run Install first."
        }
        Update-ProductionConfig

        $args = @(
            '-InstallDir', $InstallDir,
            '-Source', $bundle.Path,
            '-Name', $ServiceName,
            '-BackupRoot', (Join-Path $ProgramDataDir 'backups'),
            '-StartTimeoutSeconds', $StartTimeoutSeconds
        )
        if ($AllowInMemoryProduction) { $args += '-AllowInMemoryProduction' }
        & $updateScript @args
        if (-not $?) { throw 'update-service.ps1 failed.' }
        Invoke-HealthCheck
    }
    finally {
        Remove-DirectoryIfExists -Path $bundle.Temp
    }
}

function Invoke-Repair {
    Assert-Administrator
    Ensure-ProgramDataFolders
    Set-FirewallRule
    $installScript = Join-Path $InstallDir 'install-service.ps1'
    if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        throw "install-service.ps1 was not found in '$InstallDir'. Run Install first."
    }

    $args = @(
        '-BinPath', (Join-Path $InstallDir 'DiffPdf.Api.exe'),
        '-Name', $ServiceName,
        '-DisplayName', $DisplayName,
        '-DependsOn', $DependsOn,
        '-Url', $Url
    )
    $conn = New-ConnectionString
    if (-not [string]::IsNullOrWhiteSpace($conn)) { $args += @('-ConnectionString', $conn) }
    if ($AllowInMemoryProduction) { $args += '-AllowInMemoryProduction' }
    if ($ServiceAccount) { $args += @('-ServiceAccount', $ServiceAccount) }
    if ($ServicePassword) { $args += @('-ServicePassword', $ServicePassword) }
    if ($NoStart) { $args += '-NoStart' }

    Update-ProductionConfig
    & $installScript @args
    if (-not $?) { throw 'install-service.ps1 failed.' }
    Invoke-HealthCheck
}

function Invoke-Diagnose {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        $service | Format-Table -AutoSize Name, Status, StartType, DisplayName
    }
    else {
        Write-Warning "Service '$ServiceName' is not installed."
    }

    $configPath = Join-Path $InstallDir 'appsettings.Production.json'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        Write-Host "Config: $configPath" -ForegroundColor Cyan
        Select-String -LiteralPath $configPath -Pattern '"Urls"|"SqlServer"|"BaseUrl"|"RootPath"' |
            ForEach-Object { $_.Line }
    }
    else {
        Write-Warning "Config not found: $configPath"
    }

    Invoke-HealthCheck
    Show-RecentLogs -Lines 40
}

switch ($Mode) {
    'Install' { Invoke-Install }
    'Update' { Invoke-Update }
    'Repair' { Invoke-Repair }
    'Diagnose' { Invoke-Diagnose }
}
