<#
.SYNOPSIS
    Installs (or reconfigures) the DiffPdf API as an auto-starting Windows Service.

.DESCRIPTION
    Registers the published DiffPdf.Api.exe under the Windows Service Control Manager with
    delayed automatic start, a dependency on the database service (so SQL Server starts first
    after a reboot), and automatic restart on failure. On start the service waits for the
    database, creates the application database if missing, applies EF Core migrations, then
    begins serving. The script is idempotent: re-running it updates the existing service.
    Must be run from an elevated (Administrator) prompt.

.EXAMPLE
    .\install-service.ps1 -BinPath 'C:\DiffPdf\app\DiffPdf.Api.exe'

.EXAMPLE
    .\install-service.ps1 -BinPath 'C:\DiffPdf\app\DiffPdf.Api.exe' `
        -ConnectionString 'Server=.;Database=diffpdf;Trusted_Connection=True;TrustServerCertificate=True' `
        -DependsOn 'MSSQLSERVER'

#>
[CmdletBinding()]
param(
    # Full path to the published DiffPdf.Api.exe.
    [Parameter(Mandatory = $true)]
    [string] $BinPath,

    [string] $Name = 'DiffPdfApi',
    [string] $DisplayName = 'DiffPdf API',
    [string] $Description = 'DiffPdf comparison API and background workers.',

    # delayed-auto is recommended: it lets the database service finish starting first.
    [ValidateSet('delayed-auto', 'auto', 'manual')]
    [string] $StartupType = 'delayed-auto',

    # Database service this one should start after. Default = SQL Server default instance.
    # Use 'MSSQL$INSTANCE' for a named instance, or '' to skip the dependency.
    [string] $DependsOn = 'MSSQLSERVER',

    # Optional connection string, written to appsettings.Production.json.
    [string] $ConnectionString,

    # Clears the connection string in appsettings.Production.json.
    [switch] $ClearConnectionString,

    # Kept for compatibility with older command lines. Windows Service hosting uses Production by default.
    [string] $Environment = 'Production',

    # Bind URL written to appsettings.Production.json. 0.0.0.0 binds all interfaces so LAN clients can reach the server.
    [string] $Url = 'http://0.0.0.0:5275',

    # Production must normally use SQL Server. This switch permits the in-memory fallback intentionally.
    [switch] $AllowInMemoryProduction,

    # Optional logon account for the service (default: LocalSystem).
    [string] $ServiceAccount,
    [System.Security.SecureString] $ServicePassword,

    # Install without starting the service.
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated (Administrator) PowerShell prompt.'
    }
}

function Invoke-Sc {
    param([Parameter(Mandatory = $true)][string[]] $ScArgs)
    $output = & sc.exe @ScArgs
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($ScArgs -join ' ') failed (exit code $LASTEXITCODE): $output"
    }
}

function ConvertTo-JsonStringLiteral {
    param([AllowNull()][string] $Value)
    if ($null -eq $Value) { return 'null' }
    return ($Value | ConvertTo-Json -Compress)
}

function ConvertFrom-JsonStringLiteral {
    param([string] $Literal)
    if ([string]::IsNullOrWhiteSpace($Literal) -or $Literal -eq 'null') { return $null }
    return ($Literal | ConvertFrom-Json)
}

function Get-TopLevelStringSetting {
    param([string] $Content, [string] $Name)
    $pattern = '(?m)^\s*"' + [regex]::Escape($Name) + '"\s*:\s*(?<value>"(?:\\.|[^"\\])*"|null)'
    $match = [regex]::Match($Content, $pattern)
    if (-not $match.Success) { return $null }
    return ConvertFrom-JsonStringLiteral $match.Groups['value'].Value
}

function Set-TopLevelStringSetting {
    param([string] $Content, [string] $Name, [AllowNull()][string] $Value)
    $literal = ConvertTo-JsonStringLiteral $Value
    $pattern = '(?m)^(\s*"' + [regex]::Escape($Name) + '"\s*:\s*)(?:"(?:\\.|[^"\\])*"|null)'
    $regex = [regex]::new($pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($regex.IsMatch($Content)) {
        return $regex.Replace($Content, "`$1$literal", 1)
    }

    $insert = "  `"$Name`": $literal,"
    return [regex]::Replace($Content, '^\s*\{', "{`r`n$insert", [Text.RegularExpressions.RegexOptions]::None)
}

function Get-SqlServerConnectionString {
    param([string] $Content)
    $pattern = '"ConnectionStrings"\s*:\s*\{(?:(?!\}).)*?"SqlServer"\s*:\s*(?<value>"(?:\\.|[^"\\])*"|null)'
    $match = [regex]::Match($Content, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) { return $null }
    return ConvertFrom-JsonStringLiteral $match.Groups['value'].Value
}

function Set-SqlServerConnectionString {
    param([string] $Content, [AllowNull()][string] $Value)
    $literal = ConvertTo-JsonStringLiteral $Value
    $pattern = '("ConnectionStrings"\s*:\s*\{(?:(?!\}).)*?"SqlServer"\s*:\s*)(?:"(?:\\.|[^"\\])*"|null)'
    $regex = [regex]::new($pattern, [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($Content)) {
        throw "appsettings.Production.json must contain ConnectionStrings:SqlServer."
    }
    return $regex.Replace($Content, "`$1$literal", 1)
}

Assert-Administrator

if ($Environment -ne 'Production') {
    throw "-Environment '$Environment' is no longer written to the service. Windows Service hosting uses Production by default; use dotnet run/launchSettings for non-production hosting."
}

$resolved = Resolve-Path -LiteralPath $BinPath -ErrorAction SilentlyContinue
if (-not $resolved -or -not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) {
    throw "BinPath '$BinPath' not found. Publish first, e.g.: dotnet publish src/DiffPdf.Api -c Release -o C:\DiffPdf\app"
}
$BinPath = $resolved.Path

$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$Name' already exists - stopping and reconfiguring." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
    }
    Invoke-Sc @('config', $Name, 'binPath=', $BinPath, 'start=', $StartupType, 'DisplayName=', $DisplayName)
}
else {
    Write-Host "Creating service '$Name'." -ForegroundColor Green
    Invoke-Sc @('create', $Name, 'binPath=', $BinPath, 'start=', $StartupType, 'DisplayName=', $DisplayName)
}

Invoke-Sc @('description', $Name, $Description)

if (-not [string]::IsNullOrWhiteSpace($DependsOn)) {
    Invoke-Sc @('config', $Name, 'depend=', $DependsOn)
}

# Reset the failure counter daily; restart 5s after each of the first three failures.
Invoke-Sc @('failure', $Name, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/5000')

if (-not [string]::IsNullOrWhiteSpace($ServiceAccount)) {
    $plainPassword = ''
    if ($ServicePassword) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword)
        try { $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    Invoke-Sc @('config', $Name, 'obj=', $ServiceAccount, 'password=', $plainPassword)
    Write-Host "Service logon account set to '$ServiceAccount'." -ForegroundColor Green
}

# Operational configuration lives in appsettings.Production.json next to DiffPdf.Api.exe. Migrate old
# service-scoped values when present, then remove them so they cannot override the file.
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
$legacyEnv = [ordered]@{}
$current = (Get-ItemProperty -Path $serviceKey -Name 'Environment' -ErrorAction SilentlyContinue).Environment
foreach ($entry in @($current)) {
    if ($entry -match '^(.*?)=(.*)$') { $legacyEnv[$matches[1]] = $matches[2] }
}

$configPath = Join-Path (Split-Path -Parent $BinPath) 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "appsettings.Production.json was not found next to '$BinPath'. Publish the server bundle again."
}

$config = Get-Content -LiteralPath $configPath -Raw
$configuredConnectionString = Get-SqlServerConnectionString $config
if ($ClearConnectionString) {
    $configuredConnectionString = ''
}
elseif (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $configuredConnectionString = $ConnectionString
}
elseif ([string]::IsNullOrWhiteSpace($configuredConnectionString) -and $legacyEnv.Contains('ConnectionStrings__SqlServer')) {
    $configuredConnectionString = $legacyEnv['ConnectionStrings__SqlServer']
    Write-Host "Migrated existing service-scoped ConnectionStrings__SqlServer into appsettings.Production.json." -ForegroundColor Cyan
}

$config = Set-TopLevelStringSetting -Content $config -Name 'Urls' -Value $Url
$config = Set-SqlServerConnectionString -Content $config -Value $configuredConnectionString
Set-Content -LiteralPath $configPath -Value $config -Encoding UTF8

$hasConn = -not [string]::IsNullOrWhiteSpace($configuredConnectionString)
if (-not $hasConn -and -not $AllowInMemoryProduction) {
    throw "Production service '$Name' requires ConnectionStrings:SqlServer in appsettings.Production.json. Pass -ConnectionString or use -AllowInMemoryProduction only for an intentional non-persistent install."
}

if ($hasConn -and $configuredConnectionString -match '(?i)(^|;)\s*TrustServerCertificate\s*=\s*True\s*(;|$)') {
    Write-Warning "The production connection string uses TrustServerCertificate=True. Prefer a SQL Server certificate trusted by this host and remove that flag before long-term operation."
}

$preservedEnv = [ordered]@{}
foreach ($entry in $legacyEnv.GetEnumerator()) {
    if ($entry.Key -notin @('ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ConnectionStrings__SqlServer')) {
        $preservedEnv[$entry.Key] = $entry.Value
    }
}
if ($preservedEnv.Count -gt 0) {
    $multi = @($preservedEnv.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    New-ItemProperty -Path $serviceKey -Name 'Environment' -PropertyType MultiString -Value $multi -Force | Out-Null
}
else {
    Remove-ItemProperty -Path $serviceKey -Name 'Environment' -ErrorAction SilentlyContinue
}

Write-Host "Updated appsettings.Production.json: Urls=$Url$(if ($hasConn) { ', ConnectionStrings:SqlServer=***' })." -ForegroundColor Green

if ($NoStart) {
    Write-Host "Service '$Name' installed (not started; -NoStart was specified)." -ForegroundColor Green
}
else {
    Start-Service -Name $Name
    Write-Host "Service '$Name' installed and started." -ForegroundColor Green
}

Get-Service -Name $Name | Format-Table -AutoSize Name, Status, StartType, DisplayName
