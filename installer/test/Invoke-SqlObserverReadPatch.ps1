#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $InstallRoot = 'C:\Program Files\SqlObserver Test',
    [string] $PostgresBin = 'C:\Program Files\PostgreSQL\18\bin',
    [string] $Administrator = 'postgres',
    [switch] $Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedManifestHash = '__PATCH_MANIFEST_SHA256__'

function Assert-OrdinaryPath([string] $Path) {
    if (![IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\') -or $Path.Contains('"')) {
        throw 'Use an absolute local path without quotation marks.'
    }
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked paths are not supported.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
function Assert-Hash([string] $Path, [string] $Hash) {
    Assert-OrdinaryPath $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Hash) {
        throw "File differs from the reviewed package: $([IO.Path]::GetFileName($Path))"
    }
}
function Get-ChildPath([string] $Root, [string] $Relative) {
    if ($Relative -notmatch '^[A-Za-z0-9_./-]+$' -or $Relative.Split('/') -contains '..') {
        throw 'Invalid package-relative path.'
    }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$full.StartsWith([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package path escaped its root.'
    }
    return $full
}
function Assert-TestRepositoryDatabase([string] $Database) {
    if ($Database -cnotmatch '\Asqlobserver_test[a-z0-9_]{0,32}\z') {
        throw 'Invalid patch repository identity: expected an installer-supported sqlobserver_test database name.'
    }
}
function Get-ServiceRepository($Service, [string] $Component, [string] $ExpectedDatabase) {
    Assert-TestRepositoryDatabase $ExpectedDatabase
    $exe = Join-Path $InstallRoot ('Application\' + $Component + '\SqlObserver.' + $Component + '.exe')
    $pattern = '^"' + [regex]::Escape($exe) + '" --contentRoot "([^"]+)"$'
    if ($Service.PathName -notmatch $pattern) { throw "Unexpected service installation: $($Service.Name)" }
    $config = Join-Path $Matches[1] 'appsettings.json'
    Assert-OrdinaryPath $config
    $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $connection = New-Object System.Data.Common.DbConnectionStringBuilder
    $connection.set_ConnectionString($settings.ConnectionStrings.SqlObserverRepository)
    foreach ($key in @('Host', 'Port', 'Database', 'SSL Mode', 'Root Certificate')) {
        if (!$connection.ContainsKey($key)) { throw "Repository connection is missing $key." }
    }
    # Keep failures specific without including connection values or credentials.
    $configurationHint = "Check the $($Service.Name) service configuration: $config"
    if ([string]$connection['Database'] -cne $ExpectedDatabase) {
        throw "Repository Database mismatch: this package requires exactly $ExpectedDatabase. $configurationHint"
    }
    if ([string]$connection['Host'] -notin @('localhost', '127.0.0.1')) {
        throw "Repository Host mismatch: this package requires localhost or 127.0.0.1. $configurationHint"
    }
    $port = 0
    if (![int]::TryParse([string]$connection['Port'], [ref]$port) -or $port -ne 5432) {
        throw "Repository Port mismatch: this package requires 5432. $configurationHint"
    }
    if (([string]$connection['SSL Mode']).Replace('-', '') -ine 'VerifyFull') {
        throw "Repository SSL Mode mismatch: this package requires VerifyFull certificate and hostname verification. $configurationHint"
    }
    $rootCertificate = [string]$connection['Root Certificate']
    Assert-OrdinaryPath $rootCertificate
    return [pscustomobject]@{ Host = [string]$connection['Host']; Port = 5432; Database = $ExpectedDatabase; RootCertificate = $rootCertificate }
}
function New-PrivateBackupDirectory {
    $parent = Join-Path $env:ProgramData 'SqlObserverPatchBackups'
    if (!(Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    Assert-OrdinaryPath $parent
    $path = Join-Path $parent ('read-380-' + [Guid]::NewGuid().ToString('N'))
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $operator = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($operator)
    foreach ($sid in @($operator, [Security.Principal.SecurityIdentifier]'S-1-5-18', [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    New-Item -ItemType Directory -Path $path | Out-Null
    Set-Acl -LiteralPath $path -AclObject $security
    return $path
}
function Invoke-Psql([string] $File) {
    & $script:psql '--no-psqlrc' '--password' '--set=ON_ERROR_STOP=1' @script:connectionArguments '--file' $File
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL check or update failed (exit $LASTEXITCODE)." }
}
function Restore-ServiceStates($States) {
    $errors = @()
    foreach ($name in @('SqlObserverTestServer', 'SqlObserverTestCollector')) {
        if ($States[$name] -eq 'Running') {
            try {
                Start-Service -Name $name
                (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
            } catch { $errors += $name }
        }
    }
    if ($errors.Count -gt 0) { throw ('Unable to restart: ' + ($errors -join ', ') + '. Check Windows service logs; retain the backup.') }
}

$root = [IO.Path]::GetFullPath($PSScriptRoot)
Assert-Hash (Join-Path $root 'patch-manifest.json') $expectedManifestHash
$manifest = Get-Content -LiteralPath (Join-Path $root 'patch-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.kind -cne 'sqlobserver-test-read-patch-380' -or $manifest.baseSourceCommit -cne 'ad06cedad20834df71f197e445b5dd768781a53f') {
    throw 'Unexpected patch identity.'
}
$repositoryDatabase = [string]$manifest.repositoryDatabase
Assert-TestRepositoryDatabase $repositoryDatabase
foreach ($file in $manifest.payload) { Assert-Hash (Get-ChildPath $root $file.path) $file.sha256 }
$application = Join-Path $InstallRoot 'Application'
foreach ($file in $manifest.baseFiles) { Assert-Hash (Get-ChildPath $application $file.path) $file.sha256 }
foreach ($tool in @('psql.exe', 'pg_dump.exe', 'pg_restore.exe')) { Assert-OrdinaryPath (Join-Path $PostgresBin $tool) }
$script:psql = Join-Path $PostgresBin 'psql.exe'
$version = & $script:psql '--version'
if ($LASTEXITCODE -ne 0 -or $version -notmatch '\(PostgreSQL\) 18\.') { throw 'PostgreSQL 18 client tools are required.' }
if ($Administrator -notmatch '^[a-zA-Z_][a-zA-Z0-9_]{0,62}$') { throw 'Invalid PostgreSQL administrator login.' }
$services = @{}
$states = @{}
$repository = $null
foreach ($component in @('Server', 'Collector')) {
    $name = 'SqlObserverTest' + $component
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $name + "'")
    if ($null -eq $service -or $service.State -notin @('Running', 'Stopped')) { throw "Service is missing or changing state: $name" }
    $candidate = Get-ServiceRepository $service $component $repositoryDatabase
    if ($null -ne $repository -and ($candidate | ConvertTo-Json -Compress) -cne ($repository | ConvertTo-Json -Compress)) {
        throw 'Server and Collector point to different repositories or trust roots.'
    }
    $repository = $candidate
    $services[$name] = $service
    $states[$name] = $service.State
}
$script:connectionArguments = @(('--host=' + $repository.Host), '--port=5432', ('--dbname=' + $repository.Database), ('--username=' + $Administrator))
$environmentNames = @('PGSSLMODE', 'PGSSLROOTCERT', 'PGCONNECT_TIMEOUT', 'PGOPTIONS', 'PGAPPNAME')
$oldEnvironment = @{}
$stoppingAttempted = $false
$backup = $null
$committed = $false
try {
    foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $env:PGSSLMODE = 'verify-full'
    $env:PGSSLROOTCERT = $repository.RootCertificate
    $env:PGCONNECT_TIMEOUT = '10'
    $env:PGOPTIONS = '-c statement_timeout=300000 -c lock_timeout=5000 -c idle_in_transaction_session_timeout=300000'
    $env:PGAPPNAME = 'SqlObserver.OfflineReadPatch380'
    Write-Host 'Enter the PostgreSQL administrator password at each prompt. Windows credentials are not used.'
    Invoke-Psql (Join-Path $root 'check.sql')
    if (!$Apply) { Write-Host 'Compatibility checks passed. No services or database objects were changed.'; return }
    $backup = New-PrivateBackupDirectory
    $states | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'service-states.json') -Encoding UTF8
    $stoppingAttempted = $true
    foreach ($name in @('SqlObserverTestCollector', 'SqlObserverTestServer')) {
        if ((Get-Service -Name $name).Status -ne 'Stopped') {
            Stop-Service -Name $name
            (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
        }
    }
    $dump = Join-Path $backup 'before-380.dump'
    Write-Host "Services are stopped. Creating a database backup at $dump"
    & (Join-Path $PostgresBin 'pg_dump.exe') '--password' @script:connectionArguments '--format=custom' '--file' $dump
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $dump) -or (Get-Item -LiteralPath $dump).Length -eq 0) { throw 'Backup failed. The patch was not started.' }
    & (Join-Path $PostgresBin 'pg_restore.exe') '--list' $dump | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Backup archive could not be read. The patch was not started.' }
    (Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash | Set-Content -LiteralPath (Join-Path $backup 'before-380.dump.sha256')
    Invoke-Psql (Join-Path $root 'apply.sql')
    $committed = $true
    Invoke-Psql (Join-Path $root 'verify.sql')
    Write-Host "Migration 380 is verified. Keep the backup at $backup"
} catch {
    if ($null -ne $backup) {
        Write-Warning "Keep the backup and service-state record at $backup."
        Write-Warning 'If the connection failed during COMMIT, rerun compatibility checks to determine whether migration 380 committed before attempting recovery.'
    }
    if ($committed) { Write-Warning 'The database update committed. This later failure did not reverse it.' }
    throw
} finally {
    try { if ($stoppingAttempted) { Restore-ServiceStates $states } }
    finally { foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') } }
}
