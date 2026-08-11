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

.EXAMPLE
    .\install-service.ps1 -BinPath 'C:\DiffPdf\app\DiffPdf.Api.exe' `
        -Environment Production `
        -AllowInMemoryProduction
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

    # Optional connection string, stored as a service-scoped environment variable.
    [string] $ConnectionString,

    # Removes a previously stored service-scoped connection string.
    [switch] $ClearConnectionString,

    # ASP.NET Core environment + bind URL, stored as service-scoped environment variables.
    # Url 0.0.0.0 binds all interfaces so LAN clients can reach the server.
    [string] $Environment = 'Production',
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

Assert-Administrator

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

# Service-scoped environment variables (visible only to this service), picked up on next start. Merge with any
# existing values so re-running without -ConnectionString keeps the previously stored one.
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
$env = [ordered]@{}
$current = (Get-ItemProperty -Path $serviceKey -Name 'Environment' -ErrorAction SilentlyContinue).Environment
foreach ($entry in @($current)) {
    if ($entry -match '^(.*?)=(.*)$') { $env[$matches[1]] = $matches[2] }
}
$env['ASPNETCORE_ENVIRONMENT'] = $Environment
$env['ASPNETCORE_URLS'] = $Url
if ($ClearConnectionString) { $env.Remove('ConnectionStrings__SqlServer') }
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) { $env['ConnectionStrings__SqlServer'] = $ConnectionString }
$hasConn = $env.Contains('ConnectionStrings__SqlServer') -and -not [string]::IsNullOrWhiteSpace($env['ConnectionStrings__SqlServer'])

if ($Environment -eq 'Production' -and -not $hasConn -and -not $AllowInMemoryProduction) {
    throw "Production service '$Name' requires a SQL Server connection string. Pass -ConnectionString, keep an existing service-scoped ConnectionStrings__SqlServer, or use -AllowInMemoryProduction only for an intentional non-persistent install."
}

if ($Environment -eq 'Production' -and $hasConn -and $env['ConnectionStrings__SqlServer'] -match '(?i)(^|;)\s*TrustServerCertificate\s*=\s*True\s*(;|$)') {
    Write-Warning "The production connection string uses TrustServerCertificate=True. Prefer a SQL Server certificate trusted by this host and remove that flag before long-term operation."
}

$multi = @($env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
New-ItemProperty -Path $serviceKey -Name 'Environment' -PropertyType MultiString -Value $multi -Force | Out-Null
Write-Host "Set service environment: ASPNETCORE_ENVIRONMENT=$Environment, ASPNETCORE_URLS=$Url$(if ($hasConn) { ', ConnectionStrings__SqlServer=***' })." -ForegroundColor Green

if ($NoStart) {
    Write-Host "Service '$Name' installed (not started; -NoStart was specified)." -ForegroundColor Green
}
else {
    Start-Service -Name $Name
    Write-Host "Service '$Name' installed and started." -ForegroundColor Green
}

Get-Service -Name $Name | Format-Table -AutoSize Name, Status, StartType, DisplayName
