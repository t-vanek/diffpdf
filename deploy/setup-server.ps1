<#
.SYNOPSIS
Interactive installer, updater and diagnostics for the DiffPdf Windows Service.

.DESCRIPTION
This is the only deployment script required on the server. It installs and
configures the Windows Service directly; it does not call install-service.ps1
or update-service.ps1 from the release bundle.

The default mode is interactive question/answer. During Update, installed
appsettings*.json and web.config files are never overwritten. Incoming versions
that differ are reported and saved under ProgramDataDir\config-review.

Update creates a complete file backup and rolls it back if copying, service start,
or the health check fails.

.EXAMPLE
.\setup-server.ps1

.EXAMPLE
.\setup-server.ps1 -Mode Update -Version latest

.EXAMPLE
.\setup-server.ps1 -Mode Update -SourceZip '.\DiffPdf-Server-1.2.3-win-x64.zip'

.EXAMPLE
.\setup-server.ps1 -Mode Repair

.EXAMPLE
.\setup-server.ps1 -Mode Install -SourceZip '.\DiffPdf-Server-1.2.3-win-x64.zip' `
    -InstallDir 'D:\DiffPdfData\DiffPdfServer\app' `
    -ProgramDataDir 'D:\DiffPdfData\DiffPdfServer\data' `
    -SqlServer 'd3s-diffpdf' -Database 'DiffPdf'
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Update', 'Repair', 'Diagnose')]
    [string] $Mode,
    [Alias('Source')]
    [string] $SourceZip,
    [string] $Repository = 't-vanek/diffpdf',
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $InstallDir,
    [string] $ProgramDataDir,
    [string] $ServiceName = 'DiffPdfApi',
    [string] $DisplayName = 'DiffPdf API',
    [string] $Description = 'DiffPdf comparison API and background workers.',
    [ValidateSet('delayed-auto', 'auto', 'manual')]
    [string] $StartupType = 'delayed-auto',
    [AllowEmptyString()][string] $DependsOn = '',
    [string] $Url = 'http://0.0.0.0:5275',
    [string] $PublicUrl = 'http://localhost:5275',
    [string] $ConnectionString,
    [string] $SqlServer,
    [string] $Database = 'DiffPdf',
    [string] $SqlUser,
    [System.Security.SecureString] $SqlPassword,
    [switch] $TrustServerCertificate,
    [switch] $AllowInMemoryProduction,
    [AllowEmptyString()][string] $ServiceAccount = '',
    [System.Security.SecureString] $ServicePassword,
    [switch] $NoFirewall,
    [switch] $NoStart,
    [ValidateRange(10, 3600)][int] $StartTimeoutSeconds = 90,
    [switch] $NonInteractive
)

$minimumPowerShellVersion = [version]'5.1'
if ($PSVersionTable.PSVersion -lt $minimumPowerShellVersion) {
    throw "DiffPdf setup requires PowerShell $minimumPowerShellVersion or newer. Current version: $($PSVersionTable.PSVersion)."
}

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$helperScripts = @('setup-server.ps1', 'install-service.ps1', 'update-service.ps1', 'uninstall-service.ps1')
$protectedConfigPatterns = @('appsettings*.json', 'web.config')
$preserveExistingServiceConfiguration = $false

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated (Administrator) PowerShell prompt.'
    }
}

function Read-TextValue {
    param(
        [Parameter(Mandatory = $true)][string] $Prompt,
        [AllowNull()][AllowEmptyString()][string] $Default,
        [switch] $Required,
        [switch] $AllowEmpty
    )
    while ($true) {
        $suffix = ''
        if (-not [string]::IsNullOrWhiteSpace($Default)) { $suffix = " [$Default]" }
        elseif ($AllowEmpty) { $suffix = ' [empty]' }
        $answer = Read-Host "$Prompt$suffix"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            if (-not [string]::IsNullOrWhiteSpace($Default)) { return $Default }
            if ($AllowEmpty) { return '' }
            if (-not $Required) { return $null }
            Write-Warning 'A value is required.'
            continue
        }
        return $answer.Trim()
    }
}

function Read-ChoiceValue {
    param(
        [Parameter(Mandatory = $true)][string] $Prompt,
        [Parameter(Mandatory = $true)][string[]] $Choices,
        [Parameter(Mandatory = $true)][string] $Default
    )
    while ($true) {
        $answer = Read-Host "$Prompt ($($Choices -join '/')) [$Default]"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
        foreach ($choice in $Choices) {
            if ($choice.Equals($answer.Trim(), [StringComparison]::OrdinalIgnoreCase)) { return $choice }
        }
        Write-Warning "Choose one of: $($Choices -join ', ')."
    }
}

function Read-YesNo {
    param([Parameter(Mandatory = $true)][string] $Prompt, [bool] $Default = $true)
    $defaultText = if ($Default) { 'Y' } else { 'N' }
    while ($true) {
        $answer = Read-Host "$Prompt (Y/N) [$defaultText]"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
        if ($answer -match '^(?i:y|yes|a|ano)$') { return $true }
        if ($answer -match '^(?i:n|no|ne)$') { return $false }
        Write-Warning 'Answer Y/N (or A/N for Czech ano/ne).'
    }
}

function ConvertFrom-SecureStringToPlainText {
    param([System.Security.SecureString] $Value)
    if (-not $Value) { return $null }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string] $Path, [switch] $MustExist)
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if (-not [System.IO.Path]::IsPathRooted($expanded)) { $expanded = Join-Path $scriptRoot $expanded }
    $full = [System.IO.Path]::GetFullPath($expanded).TrimEnd('\')
    if ($MustExist -and -not (Test-Path -LiteralPath $full)) { throw "Path not found: '$full'." }
    return $full
}

function Get-DefaultSource {
    $zip = Get-ChildItem -LiteralPath $scriptRoot -File -Filter 'DiffPdf-Server-*-win-*.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($zip) { return $zip.FullName }
    return $null
}

function Get-RegisteredExecutablePath {
    param([Parameter(Mandatory = $true)][string] $Name)
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $rawPath = (Get-ItemProperty -LiteralPath $serviceKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
    if ([string]::IsNullOrWhiteSpace($rawPath)) { return $null }
    $rawPath = [Environment]::ExpandEnvironmentVariables($rawPath.Trim())
    $executable = $null
    if ($rawPath.StartsWith('"')) {
        $closingQuote = $rawPath.IndexOf('"', 1)
        if ($closingQuote -gt 1) { $executable = $rawPath.Substring(1, $closingQuote - 1) }
    }
    elseif ($rawPath -match '^(.*?\.exe)(?:\s|$)') { $executable = $matches[1] }
    if ([string]::IsNullOrWhiteSpace($executable)) { return $null }
    return [System.IO.Path]::GetFullPath($executable)
}

function Read-DatabaseParameters {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        Write-Host 'Using the complete connection string supplied with -ConnectionString.' -ForegroundColor Cyan
        return
    }
    if ($AllowInMemoryProduction) {
        Write-Warning 'Using explicitly requested in-memory production mode. Data will not survive an application restart.'
        return
    }
    $authentication = Read-ChoiceValue -Prompt 'SQL Server authentication' -Choices @('Windows', 'SqlLogin') -Default 'Windows'
    $script:ConnectionString = $null
    $script:SqlServer = Read-TextValue -Prompt 'SQL Server host or instance' -Default $SqlServer -Required
    $script:Database = Read-TextValue -Prompt 'SQL database name' -Default $Database -Required
    if ($authentication -eq 'SqlLogin') {
        $script:SqlUser = Read-TextValue -Prompt 'SQL login' -Default $SqlUser -Required
        $script:SqlPassword = Read-Host -Prompt "Password for SQL login '$SqlUser'" -AsSecureString
    }
    else {
        $script:SqlUser = $null
        $script:SqlPassword = $null
        Write-Host 'Windows authentication selected. The Windows Service account must have access to SQL Server.' -ForegroundColor Cyan
    }
    $script:TrustServerCertificate = Read-YesNo -Prompt 'Trust the SQL Server certificate without validation' -Default ([bool]$TrustServerCertificate)
    $script:AllowInMemoryProduction = $false
}

function Complete-InteractiveParameters {
    if (-not $Mode) {
        $script:Mode = Read-ChoiceValue -Prompt 'Operation' -Choices @('Install', 'Update', 'Repair', 'Diagnose') -Default 'Install'
    }

    $script:ServiceName = Read-TextValue -Prompt 'Windows service name' -Default $ServiceName -Required
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($Mode -ne 'Diagnose' -and (-not $existingService -or $Mode -eq 'Repair')) {
        $script:DisplayName = Read-TextValue -Prompt 'Windows service display name' -Default $DisplayName -Required
        $script:Description = Read-TextValue -Prompt 'Windows service description' -Default $Description -Required
        $script:StartupType = Read-ChoiceValue -Prompt 'Windows service startup type' -Choices @('delayed-auto', 'auto', 'manual') -Default $StartupType
    }

    if ($Mode -in @('Install', 'Update') -and -not $SourceZip -and -not $Version) {
        $defaultSource = Get-DefaultSource
        $sourceKindDefault = if ($defaultSource) { 'Local' } else { 'Latest' }
        $sourceKind = Read-ChoiceValue -Prompt 'Release source' -Choices @('Local', 'Latest', 'Version') -Default $sourceKindDefault
        switch ($sourceKind) {
            'Local' {
                $script:SourceZip = Read-TextValue -Prompt 'Full path to the DiffPdf server release ZIP or expanded release directory' -Default $defaultSource -Required
            }
            'Latest' { $script:Version = 'latest' }
            'Version' {
                $script:Version = Read-TextValue -Prompt 'Release version (for example 1.2.3)' -Default $null -Required
            }
        }
    }
    if ($Mode -in @('Install', 'Update') -and -not $SourceZip) {
        $script:Repository = Read-TextValue -Prompt 'GitHub repository' -Default $Repository -Required
        $script:Runtime = Read-ChoiceValue -Prompt 'Server runtime' -Choices @('win-x64', 'win-arm64') -Default $Runtime
    }

    $defaultInstall = Join-Path $scriptRoot 'app'
    $registeredExecutable = Get-RegisteredExecutablePath -Name $ServiceName
    if ($registeredExecutable) { $defaultInstall = Split-Path -Parent $registeredExecutable }
    if ($InstallDir) { $defaultInstall = $InstallDir }
    $defaultData = if ($ProgramDataDir) { $ProgramDataDir } else { Join-Path $scriptRoot 'data' }
    $script:InstallDir = Read-TextValue -Prompt 'Application installation directory' -Default $defaultInstall -Required
    $script:ProgramDataDir = Read-TextValue -Prompt 'Data, logs, backups and config-review directory' -Default $defaultData -Required

    if ($Mode -eq 'Diagnose') {
        $script:PublicUrl = Read-TextValue -Prompt 'URL used for the health check' -Default $PublicUrl -Required
        $script:StartTimeoutSeconds = [int](Read-TextValue -Prompt 'Health-check timeout in seconds' -Default ([string]$StartTimeoutSeconds) -Required)
        return
    }

    $existingExe = Test-Path -LiteralPath (Join-Path $InstallDir 'DiffPdf.Api.exe') -PathType Leaf
    if ($Mode -eq 'Install' -and $existingService) {
        Write-Warning 'An existing Windows Service was detected. Install will use safe in-place replacement behavior and preserve the existing configuration.'
    }
    elseif ($Mode -eq 'Install' -and $existingExe) {
        Write-Warning 'Application files exist, but the Windows Service does not. Continuing as installation recovery.'
    }

    if ($Mode -in @('Install', 'Update') -and $existingService) {
        $script:preserveExistingServiceConfiguration = $true
        $healthPrompt = if ($Mode -eq 'Update') { 'URL used for the post-update health check' } else { 'URL used for the post-install health check' }
        $startPrompt = if ($Mode -eq 'Update') { 'Start the service and require a successful health check after update' } else { 'Start the service and require a successful health check after installation' }
        $script:PublicUrl = Read-TextValue -Prompt $healthPrompt -Default $PublicUrl -Required
        $script:StartTimeoutSeconds = [int](Read-TextValue -Prompt 'Start and health-check timeout in seconds' -Default ([string]$StartTimeoutSeconds) -Required)
        $script:NoStart = -not (Read-YesNo -Prompt $startPrompt -Default $true)
        return
    }

    $script:ServiceAccount = Read-TextValue -Prompt 'Windows service account (blank = LocalSystem)' -Default $ServiceAccount -AllowEmpty
    if (-not [string]::IsNullOrWhiteSpace($ServiceAccount)) {
        $script:ServicePassword = Read-Host -Prompt "Password for service account '$ServiceAccount' (blank if not required)" -AsSecureString
    }
    if ($Mode -ne 'Repair') { Read-DatabaseParameters }
    $script:DependsOn = Read-TextValue -Prompt 'SQL Windows service on this computer (MSSQLSERVER for the default instance, MSSQL$INSTANCE for a named instance; blank if SQL Server is remote)' -Default $DependsOn -AllowEmpty
    $script:Url = Read-TextValue -Prompt 'HTTP address and port the API listens on (0.0.0.0 = all IPv4 interfaces; normally keep the default)' -Default $Url -Required
    $script:PublicUrl = Read-TextValue -Prompt 'Public URL used by clients, health checks and notification links' -Default $PublicUrl -Required
    $script:NoFirewall = -not (Read-YesNo -Prompt 'Create or keep the inbound HTTP firewall rule' -Default $true)
    $script:StartTimeoutSeconds = [int](Read-TextValue -Prompt 'Start and health-check timeout in seconds' -Default ([string]$StartTimeoutSeconds) -Required)
    $finishPrompt = if ($Mode -eq 'Repair') { 'Start the service and require a successful health check after repair' } else { 'Start the service and require a successful health check when finished' }
    $script:NoStart = -not (Read-YesNo -Prompt $finishPrompt -Default $true)
}

function Complete-NonInteractiveDefaults {
    if (-not $Mode) { $script:Mode = 'Install' }
    if (-not $InstallDir) { $script:InstallDir = Join-Path $scriptRoot 'app' }
    if (-not $ProgramDataDir) { $script:ProgramDataDir = Join-Path $scriptRoot 'data' }
    if ($Mode -in @('Install', 'Update') -and -not $SourceZip -and -not $Version) { $script:Version = 'latest' }
    if ($Mode -eq 'Update' -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        $script:preserveExistingServiceConfiguration = $true
    }
}

function Show-Plan {
    Write-Host ''
    Write-Host 'Selected setup:' -ForegroundColor Cyan
    Write-Host "  Mode:              $Mode"
    if ($Mode -in @('Install', 'Update')) {
        if ($SourceZip) { Write-Host "  Release source:    $SourceZip" }
        else { Write-Host "  GitHub release:    $Repository, version $Version, $Runtime" }
    }
    Write-Host "  Install directory: $InstallDir"
    Write-Host "  Data directory:    $ProgramDataDir"
    Write-Host "  Service:           $ServiceName"
    if ($preserveExistingServiceConfiguration) { Write-Host '  Service config:    preserved' }
    Write-Host "  Public URL:        $PublicUrl"
    Write-Host ''
    if (-not $NonInteractive) {
        $confirmationPrompt = switch ($Mode) {
            'Install' { 'Start the DiffPdf installation now' }
            'Update' { 'Start the DiffPdf update now' }
            'Repair' { 'Start the DiffPdf service repair now' }
            'Diagnose' { 'Run DiffPdf diagnostics now' }
        }
        if (-not (Read-YesNo -Prompt $confirmationPrompt -Default $true)) {
            Write-Host 'No changes were made.' -ForegroundColor Yellow
            exit 0
        }
    }
}

function Invoke-Robocopy {
    param(
        [Parameter(Mandatory = $true)][string] $From,
        [Parameter(Mandatory = $true)][string] $To,
        [switch] $Mirror,
        [string[]] $ExcludeFiles
    )
    $copyMode = if ($Mirror) { '/MIR' } else { '/E' }
    $options = @($copyMode, '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:2', '/W:1')
    if ($ExcludeFiles -and $ExcludeFiles.Count -gt 0) { $options += @('/XF') + $ExcludeFiles }
    & robocopy.exe $From $To @options | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy '$From' -> '$To' failed (exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Remove-DirectoryIfExists {
    param([AllowNull()][string] $Path)
    if ($Path -and [System.IO.Directory]::Exists($Path)) { [System.IO.Directory]::Delete($Path, $true) }
}

function Get-ServerBundle {
    if ($SourceZip) {
        $resolved = Resolve-FullPath -Path $SourceZip -MustExist
        if ((Test-Path -LiteralPath $resolved -PathType Leaf) -and -not $resolved.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            throw "SourceZip must be a DiffPdf server release ZIP or an expanded release directory: '$resolved'."
        }
        return @{ Path = $resolved; Temp = $null }
    }

    if ([string]::IsNullOrWhiteSpace($Version)) { throw 'Specify SourceZip or Version.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'DiffPdf-Setup' }
    $releasesUri = "https://api.github.com/repos/$Repository/releases?per_page=100"
    Write-Host "Looking up DiffPdf release in GitHub repository '$Repository'." -ForegroundColor Cyan
    $releases = Invoke-RestMethod -Uri $releasesUri -Headers $headers -UseBasicParsing
    if (-not $releases) { throw "No releases found in '$Repository'." }

    if ($Version -eq 'latest') {
        $asset = $releases | Sort-Object published_at -Descending | ForEach-Object { $_.assets } |
            Where-Object { $_.name -like "DiffPdf-Server-*-$Runtime.zip" } | Select-Object -First 1
    }
    else {
        $normalizedVersion = $Version.TrimStart('v')
        $asset = $releases | ForEach-Object { $_.assets } | Where-Object {
            $_.name -like "DiffPdf-Server-$normalizedVersion*-$Runtime.zip" -or
            $_.name -like "DiffPdf-Server-$Version*-$Runtime.zip"
        } | Select-Object -First 1
    }
    if (-not $asset) { throw "No server ZIP found for version '$Version' and runtime '$Runtime'." }

    $downloadDir = Join-Path ([System.IO.Path]::GetTempPath()) ('diffpdf-download-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
    $zipPath = Join-Path $downloadDir $asset.name
    Write-Host "Downloading '$($asset.name)'." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers $headers -UseBasicParsing
    return @{ Path = $zipPath; Temp = $downloadDir }
}

function Expand-ServerBundle {
    param([Parameter(Mandatory = $true)][string] $Path)
    $resolved = Resolve-FullPath -Path $Path -MustExist
    if (Test-Path -LiteralPath $resolved -PathType Container) { return @{ Path = $resolved; Temp = $null } }
    if (-not $resolved.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release source must be a ZIP or directory: '$resolved'."
    }
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('diffpdf-setup-' + [guid]::NewGuid().ToString('N'))
    Write-Host "Expanding '$resolved' -> '$temp'." -ForegroundColor Cyan
    Expand-Archive -LiteralPath $resolved -DestinationPath $temp -Force
    $root = $temp
    if (-not (Test-Path -LiteralPath (Join-Path $root 'DiffPdf.Api.exe') -PathType Leaf)) {
        $children = @(Get-ChildItem -LiteralPath $temp -Directory)
        if ($children.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'DiffPdf.Api.exe') -PathType Leaf)) {
            $root = $children[0].FullName
        }
    }
    return @{ Path = $root; Temp = $temp }
}

function Assert-Release {
    param([Parameter(Mandatory = $true)][string] $ReleaseDir)
    foreach ($required in @('DiffPdf.Api.exe', 'appsettings.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDir $required) -PathType Leaf)) {
            throw "Release '$ReleaseDir' does not contain required file '$required' at its root."
        }
    }
}

function Ensure-DataFolders {
    foreach ($subdir in @('', 'data', 'storage', 'logs', 'backups', 'config-review')) {
        $path = if ($subdir) { Join-Path $ProgramDataDir $subdir } else { $ProgramDataDir }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function New-ConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) { return $ConnectionString }
    if ([string]::IsNullOrWhiteSpace($SqlServer)) { return '' }
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder['Server'] = $SqlServer
    $builder['Database'] = $Database
    if ([string]::IsNullOrWhiteSpace($SqlUser)) {
        $builder['Trusted_Connection'] = 'True'
    }
    else {
        if (-not $SqlPassword) { throw 'SqlPassword is required when SqlUser is specified.' }
        $builder['User Id'] = $SqlUser
        $builder['Password'] = ConvertFrom-SecureStringToPlainText $SqlPassword
    }
    $builder['TrustServerCertificate'] = ([bool]$TrustServerCertificate).ToString()
    return $builder.ConnectionString
}

function New-ProductionConfig {
    $configPath = Join-Path $InstallDir 'appsettings.Production.json'
    $connection = New-ConnectionString
    if ([string]::IsNullOrWhiteSpace($connection) -and -not $AllowInMemoryProduction) {
        throw 'Production configuration requires a SQL Server connection string.'
    }
    $config = [ordered]@{
        Urls = $Url
        ConnectionStrings = [ordered]@{ SqlServer = $connection }
        Serilog = [ordered]@{
            Using = @('Serilog.Sinks.File')
            WriteTo = @([ordered]@{
                Name = 'File'
                Args = [ordered]@{
                    path = Join-Path (Join-Path $ProgramDataDir 'logs') 'diffpdf-.log'
                    rollingInterval = 'Day'
                    retainedFileCountLimit = 14
                    shared = $true
                    outputTemplate = '[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}'
                }
            })
        }
        Notifications = [ordered]@{ BaseUrl = $PublicUrl }
        Storage = [ordered]@{ RootPath = Join-Path $ProgramDataDir 'storage' }
        FileManager = [ordered]@{ RootPath = Join-Path $ProgramDataDir 'storage' }
        ScopeSync = [ordered]@{
            RootPath = Join-Path $ProgramDataDir 'data'
            AutoRegister = $true
            AutoCreateFolders = $true
        }
    }
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8
    Write-Host "Created production configuration '$configPath'." -ForegroundColor Green
}

function Assert-SelfContainedProductionConfig {
    $configPath = Join-Path $InstallDir 'appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "Production configuration not found: '$configPath'." }
    $content = Get-Content -LiteralPath $configPath -Raw
    if ($content -notmatch '"Urls"\s*:\s*"[^"\r\n]+"') {
        throw "Production configuration '$configPath' does not contain Urls. Correct the JSON first."
    }
    if (-not $AllowInMemoryProduction -and $content -notmatch '"SqlServer"\s*:\s*"[^"\r\n]+"') {
        throw "Production configuration '$configPath' does not contain ConnectionStrings:SqlServer. Correct the JSON first."
    }
}

function Get-ProtectedConfigFiles {
    param([Parameter(Mandatory = $true)][string] $Root)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object {
        $_.Name -eq 'web.config' -or $_.Name -like 'appsettings*.json'
    })
}

function Get-RelativeFilePath {
    param([Parameter(Mandatory = $true)][string] $Root, [Parameter(Mandatory = $true)][string] $FullName)
    return $FullName.Substring($Root.TrimEnd('\').Length).TrimStart('\')
}

function Compare-ReleaseConfigs {
    param([Parameter(Mandatory = $true)][string] $ReleaseDir)
    $reviewRoot = Join-Path (Join-Path $ProgramDataDir 'config-review') (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    $changes = New-Object System.Collections.Generic.List[string]
    foreach ($sourceFile in (Get-ProtectedConfigFiles -Root $ReleaseDir)) {
        $relative = Get-RelativeFilePath -Root $ReleaseDir -FullName $sourceFile.FullName
        $installedFile = Join-Path $InstallDir $relative
        $status = $null
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { $status = 'new in release; not deployed automatically' }
        else {
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
            $installedHash = (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash
            if ($sourceHash -ne $installedHash) { $status = 'changed in release; installed file preserved' }
        }
        if ($status) {
            $candidate = Join-Path $reviewRoot ($relative + '.incoming')
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $candidate) | Out-Null
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $candidate -Force
            $changes.Add("$relative - $status; candidate: $candidate")
        }
    }
    foreach ($installedFile in (Get-ProtectedConfigFiles -Root $InstallDir)) {
        $relative = Get-RelativeFilePath -Root $InstallDir -FullName $installedFile.FullName
        if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDir $relative) -PathType Leaf)) {
            $changes.Add("$relative - absent from release; installed file preserved")
        }
    }
    if ($changes.Count -eq 0) { Write-Host 'Release configuration files match installed files.' -ForegroundColor Green }
    else {
        Write-Warning 'Configuration differences detected. No installed JSON or web.config file will be replaced.'
        foreach ($change in $changes) { Write-Host "  $change" -ForegroundColor Yellow }
    }
}

function Invoke-Sc {
    param([Parameter(Mandatory = $true)][string[]] $ScArgs)
    $output = & sc.exe @ScArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($ScArgs -join ' ') failed (exit code $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }
}

function Remove-LegacyServiceEnvironment {
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $property = Get-ItemProperty -LiteralPath $serviceKey -Name 'Environment' -ErrorAction SilentlyContinue
    $current = @($property.Environment)
    if ($current.Count -eq 0) { return }
    $legacyNames = @('ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ConnectionStrings__SqlServer')
    $remaining = @($current | Where-Object {
        $name = ($_ -split '=', 2)[0]
        $legacyNames -notcontains $name
    })
    if ($remaining.Count -eq $current.Count) { return }
    if ($remaining.Count -gt 0) {
        New-ItemProperty -LiteralPath $serviceKey -Name 'Environment' -PropertyType MultiString -Value $remaining -Force | Out-Null
    }
    else { Remove-ItemProperty -LiteralPath $serviceKey -Name 'Environment' -Force }
    Write-Host 'Removed legacy DiffPdf runtime values from the Windows Service environment; appsettings.Production.json is authoritative.' -ForegroundColor Green
}

function Ensure-EventLogSource {
    $sourceName = 'DiffPdf API'
    if (-not [System.Diagnostics.EventLog]::SourceExists($sourceName)) {
        New-EventLog -LogName Application -Source $sourceName
        Write-Host "Created Windows Application event source '$sourceName'." -ForegroundColor Green
    }
}

function New-ServiceRegistration {
    Assert-SelfContainedProductionConfig
    $exePath = Join-Path $InstallDir 'DiffPdf.Api.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "Executable not found: '$exePath'." }
    $quotedExePath = '"' + $exePath + '"'
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Invoke-Sc -ScArgs @('config', $ServiceName, 'binPath=', $quotedExePath, 'start=', $StartupType, 'DisplayName=', $DisplayName)
        Write-Host "Reconfigured Windows service '$ServiceName'." -ForegroundColor Green
    }
    else {
        Invoke-Sc -ScArgs @('create', $ServiceName, 'binPath=', $quotedExePath, 'start=', $StartupType, 'DisplayName=', $DisplayName)
        Write-Host "Created Windows service '$ServiceName'." -ForegroundColor Green
    }
    Invoke-Sc -ScArgs @('description', $ServiceName, $Description)
    # sc.exe uses a single slash to remove all dependencies.
    $dependencyValue = if ([string]::IsNullOrWhiteSpace($DependsOn)) { '/' } else { $DependsOn }
    Invoke-Sc -ScArgs @('config', $ServiceName, 'depend=', $dependencyValue)
    Invoke-Sc -ScArgs @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/5000')
    if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
        Invoke-Sc -ScArgs @('config', $ServiceName, 'obj=', 'LocalSystem')
    }
    else {
        $accountArgs = @('config', $ServiceName, 'obj=', $ServiceAccount)
        if ($ServicePassword) {
            $accountArgs += @('password=', (ConvertFrom-SecureStringToPlainText $ServicePassword))
        }
        Invoke-Sc -ScArgs $accountArgs
    }
    Remove-LegacyServiceEnvironment
    Ensure-EventLogSource
    Write-Host 'Runtime configuration is stored in appsettings.Production.json; no service-scoped environment variables were created.' -ForegroundColor Green
}

function Set-FirewallRule {
    if ($NoFirewall) { return }
    $uri = [Uri]($Url.Split(';')[0])
    $port = $uri.Port
    $ruleName = "DiffPdf API (HTTP $port)"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null
        Write-Host "Created firewall rule '$ruleName' for TCP $port." -ForegroundColor Green
    }
}

function Show-RecentLogs {
    param([int] $Lines = 40)
    $logDir = Join-Path $ProgramDataDir 'logs'
    if (-not (Test-Path -LiteralPath $logDir -PathType Container)) { return }
    $latest = Get-ChildItem -LiteralPath $logDir -Filter 'diffpdf-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Write-Host "Recent log lines from $($latest.FullName):" -ForegroundColor Cyan
        Get-Content -LiteralPath $latest.FullName -Tail $Lines
    }
}

function Start-DiffPdfService {
    if ($NoStart) { Write-Host "Service '$ServiceName' was not started (-NoStart)." -ForegroundColor Yellow; return }
    try {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds($StartTimeoutSeconds))
        Write-Host "Service '$ServiceName' is Running." -ForegroundColor Green
    }
    catch {
        Write-Warning "Service '$ServiceName' could not be started: $($_.Exception.Message)"
        Show-RecentLogs -Lines 80
        throw
    }
}

function Test-DiffPdfHealth {
    if ($NoStart) { return $true }
    $healthUrl = $PublicUrl.TrimEnd('/') + '/health'
    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $service = Get-Service -Name $ServiceName -ErrorAction Stop
            if ($service.Status -eq 'Running') {
                Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5 -UseBasicParsing | Out-Null
                Write-Host "Health check OK: $healthUrl" -ForegroundColor Green
                return $true
            }
        }
        catch { }
        Start-Sleep -Seconds 2
    }
    Write-Warning "Health check failed within $StartTimeoutSeconds seconds: $healthUrl"
    Show-RecentLogs -Lines 80
    return $false
}

function Invoke-UpdateFromRelease {
    param([Parameter(Mandatory = $true)][string] $ReleaseDir)
    Assert-Release -ReleaseDir $ReleaseDir
    Ensure-DataFolders
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $installExists = Test-Path -LiteralPath $InstallDir -PathType Container
    $backupDir = $null
    $serviceCreatedDuringUpdate = $false

    if ($service) {
        if (-not $installExists) {
            throw "Service '$ServiceName' exists, but InstallDir '$InstallDir' does not. A rollback-safe update is not possible."
        }
        $registeredExecutable = Get-RegisteredExecutablePath -Name $ServiceName
        $expectedExecutable = [System.IO.Path]::GetFullPath((Join-Path $InstallDir 'DiffPdf.Api.exe'))
        if ($registeredExecutable -and -not $registeredExecutable.Equals($expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Service '$ServiceName' runs '$registeredExecutable', but InstallDir points to '$InstallDir'. Use InstallDir '$((Split-Path -Parent $registeredExecutable))'."
        }
    }

    if ($installExists) {
        Compare-ReleaseConfigs -ReleaseDir $ReleaseDir
        $backupDir = Join-Path (Join-Path $ProgramDataDir 'backups') ("$ServiceName-" + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
        Write-Host "Backing up '$InstallDir' -> '$backupDir'." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
        Invoke-Robocopy -From $InstallDir -To $backupDir
    }

    try {
        if ($service -and $service.Status -ne 'Stopped') {
            Write-Host "Stopping service '$ServiceName'." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds($StartTimeoutSeconds))
        }
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        $excluded = @($helperScripts)
        if ($installExists) { $excluded += $protectedConfigPatterns }
        Invoke-Robocopy -From $ReleaseDir -To $InstallDir -ExcludeFiles $excluded
        foreach ($helperScript in $helperScripts) {
            $deployedHelper = Join-Path $InstallDir $helperScript
            if (Test-Path -LiteralPath $deployedHelper -PathType Leaf) { Remove-Item -LiteralPath $deployedHelper -Force }
        }

        if (-not $installExists -or -not (Test-Path -LiteralPath (Join-Path $InstallDir 'appsettings.Production.json') -PathType Leaf)) {
            New-ProductionConfig
        }
        if (-not $service) {
            Write-Warning 'No existing service was found; creating its registration after updating the files.'
            New-ServiceRegistration
            $serviceCreatedDuringUpdate = $true
            Set-FirewallRule
        }
        else {
            Write-Host 'Existing Windows Service registration, account and configuration were preserved.' -ForegroundColor Green
            $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
            $legacyEnvironment = @((Get-ItemProperty -LiteralPath $serviceKey -Name Environment -ErrorAction SilentlyContinue).Environment |
                Where-Object { $_ -match '^(ASPNETCORE_ENVIRONMENT|ASPNETCORE_URLS|ConnectionStrings__SqlServer)=' })
            if ($legacyEnvironment.Count -gt 0) {
                $legacyNames = @($legacyEnvironment | ForEach-Object { ($_ -split '=', 2)[0] })
                Write-Warning "Legacy service values override JSON: $($legacyNames -join ', '). Migrate them, then run Repair."
            }
        }

        Start-DiffPdfService
        if (-not (Test-DiffPdfHealth)) { throw "Updated service '$ServiceName' did not pass its health check." }
        Write-Host "Update completed. Backup: $(if ($backupDir) { $backupDir } else { '<none; new directory>' })" -ForegroundColor Green
    }
    catch {
        $failure = $_
        if ($serviceCreatedDuringUpdate) {
            try { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue } catch { }
            try { Invoke-Sc -ScArgs @('delete', $ServiceName) }
            catch { Write-Warning "Could not remove the new service registration: $($_.Exception.Message)" }
        }
        if ($backupDir -and (Test-Path -LiteralPath $backupDir -PathType Container)) {
            Write-Warning "Update failed: $($failure.Exception.Message)"
            Write-Host "Rolling back files from '$backupDir'." -ForegroundColor Yellow
            try { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue } catch { }
            Invoke-Robocopy -From $backupDir -To $InstallDir -Mirror
            if ($service -and -not $NoStart) {
                Start-Service -Name $ServiceName
                (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds($StartTimeoutSeconds))
                if (-not (Test-DiffPdfHealth)) { Write-Warning 'Restored service did not pass its health check.' }
                else { Write-Host 'Rollback completed and the previous version is healthy.' -ForegroundColor Green }
            }
        }
        elseif (-not $installExists) {
            Remove-DirectoryIfExists -Path $InstallDir
            Write-Host 'Removed the new application directory created by the failed update.' -ForegroundColor Yellow
        }
        throw $failure
    }
}

function Invoke-Install {
    Assert-Administrator
    Ensure-DataFolders
    $bundle = Get-ServerBundle
    $expanded = $null
    try {
        $expanded = Expand-ServerBundle -Path $bundle.Path
        Assert-Release -ReleaseDir $expanded.Path
        $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $existingExe = Test-Path -LiteralPath (Join-Path $InstallDir 'DiffPdf.Api.exe') -PathType Leaf
        if ($existingService) {
            Write-Warning 'Existing deployment detected. Switching to safe Update behavior.'
            Invoke-UpdateFromRelease -ReleaseDir $expanded.Path
            return
        }
        if ($existingExe) { Write-Warning 'Recovering an incomplete installation.' }
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Invoke-Robocopy -From $expanded.Path -To $InstallDir -ExcludeFiles $helperScripts
        New-ProductionConfig
        New-ServiceRegistration
        Set-FirewallRule
        Start-DiffPdfService
        if (-not (Test-DiffPdfHealth)) { throw "Installed service '$ServiceName' did not pass its health check." }
        Write-Host 'DiffPdf installation completed successfully.' -ForegroundColor Green
    }
    finally {
        if ($expanded) { Remove-DirectoryIfExists -Path $expanded.Temp }
        if ($bundle) { Remove-DirectoryIfExists -Path $bundle.Temp }
    }
}

function Invoke-Update {
    Assert-Administrator
    $bundle = Get-ServerBundle
    $expanded = $null
    try {
        $expanded = Expand-ServerBundle -Path $bundle.Path
        Invoke-UpdateFromRelease -ReleaseDir $expanded.Path
    }
    finally {
        if ($expanded) { Remove-DirectoryIfExists -Path $expanded.Temp }
        if ($bundle) { Remove-DirectoryIfExists -Path $bundle.Temp }
    }
}

function Invoke-Repair {
    Assert-Administrator
    Ensure-DataFolders
    Assert-Release -ReleaseDir $InstallDir
    Assert-SelfContainedProductionConfig
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping service '$ServiceName' before repairing its registration." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds($StartTimeoutSeconds))
    }
    New-ServiceRegistration
    Set-FirewallRule
    Start-DiffPdfService
    if (-not (Test-DiffPdfHealth)) { throw "Repaired service '$ServiceName' did not pass its health check." }
    Write-Host 'DiffPdf service repair completed successfully. Application and configuration files were not replaced.' -ForegroundColor Green
}

function Invoke-Diagnose {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        $service | Format-Table -AutoSize Name, Status, StartType, DisplayName
        $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
        $imagePath = (Get-ItemProperty -LiteralPath $serviceKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
        Write-Host "Executable: $imagePath" -ForegroundColor Cyan
        $legacyEnvironment = @((Get-ItemProperty -LiteralPath $serviceKey -Name Environment -ErrorAction SilentlyContinue).Environment |
            Where-Object { $_ -match '^(ASPNETCORE_ENVIRONMENT|ASPNETCORE_URLS|ConnectionStrings__SqlServer)=' })
        if ($legacyEnvironment.Count -gt 0) {
            $legacyNames = @($legacyEnvironment | ForEach-Object { ($_ -split '=', 2)[0] })
            Write-Warning "Legacy service configuration overrides JSON: $($legacyNames -join ', ')."
        }
    }
    else { Write-Warning "Service '$ServiceName' is not installed." }
    Write-Host "Install directory: $InstallDir"
    Write-Host "Data directory:    $ProgramDataDir"
    foreach ($name in @('appsettings.json', 'appsettings.Production.json', 'web.config')) {
        $path = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Write-Host "Config present: $path" -ForegroundColor Green }
        else { Write-Warning "Config missing: $path" }
    }
    if (-not (Test-DiffPdfHealth)) { Write-Warning 'DiffPdf diagnostics detected an unhealthy service.' }
    Show-RecentLogs -Lines 40
}

# Dot-sourcing loads the functions for the repository smoke test without starting an installation.
if ($MyInvocation.InvocationName -eq '.') { return }

if ($NonInteractive) { Complete-NonInteractiveDefaults } else { Complete-InteractiveParameters }
$script:InstallDir = Resolve-FullPath -Path $InstallDir
$script:ProgramDataDir = Resolve-FullPath -Path $ProgramDataDir
if ($SourceZip) { $script:SourceZip = Resolve-FullPath -Path $SourceZip -MustExist }

Show-Plan
switch ($Mode) {
    'Install' { Invoke-Install }
    'Update' { Invoke-Update }
    'Repair' { Invoke-Repair }
    'Diagnose' { Invoke-Diagnose }
}
