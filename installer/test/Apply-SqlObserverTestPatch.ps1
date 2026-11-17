#requires -Version 7.5
[CmdletBinding()]
param(
    [string] $InstallRoot = 'C:\Program Files\SqlObserver Test',
    [string] $DataRoot = 'C:\ProgramData\SqlObserverTestIdentityFix',
    [switch] $ValidateOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedManifestHash = '__PATCH_MANIFEST_SHA256__'

function Assert-Tree([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installation paths are not supported.' }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installation content is not supported.' }
    }
    $parent = $item.Parent
    while ($null -ne $parent) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installation parents are not supported.' }
        $parent = $parent.Parent
    }
}
function Assert-Hash([string] $Path, [string] $Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) { throw "Checksum mismatch: $([IO.Path]::GetFileName($Path))" }
}
function Assert-Child([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Patch path escaped its installation root.' }
    return $full
}
function Move-InstallDirectory([string] $Source, [string] $Destination) {
    $sourcePath = Assert-Child $Source $InstallRoot
    $destinationPath = Assert-Child $Destination $InstallRoot
    if (Test-Path -LiteralPath $destinationPath) { throw 'Patch move destination already exists.' }
    Assert-Tree $sourcePath
    Move-Item -LiteralPath $sourcePath -Destination $destinationPath
}
function Set-TargetTrust($Configuration, $Target) {
    if (!$Configuration.Contains('SqlObserver') -or $Configuration.SqlObserver -isnot [System.Collections.IDictionary]) {
        throw 'Expected SqlObserver configuration is missing.'
    }
    $settings = $Configuration.SqlObserver
    $entries = @()
    if ($settings.Contains('SqlServerCertificateTrustOverrides')) { $entries = @($settings.SqlServerCertificateTrustOverrides) }
    if ($entries.Count -gt 32) { throw 'Too many existing certificate trust exceptions.' }
    $others = @($entries | Where-Object { [string]$_.TargetId -ine [string]$Target.TargetId })
    if ($others.Count -ge 32) { throw 'Too many certificate trust exceptions.' }
    $settings.SqlServerCertificateTrustOverrides = @($others) + @([ordered]@{
        TargetId = [string]$Target.TargetId; Host = [string]$Target.Host; TcpPort = [int]$Target.TcpPort
    })
    return $Configuration
}
function Write-Configuration([string] $Path, [byte[]] $Bytes) {
    $temp = Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.patch-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temp, $Bytes)
        Set-Acl -LiteralPath $temp -AclObject (Get-Acl -LiteralPath $Path)
        [IO.File]::Replace($temp, $Path, [NullString]::Value)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}
function Stop-TestServices {
    foreach ($name in @('SqlObserverTestCollector', 'SqlObserverTestServer')) {
        $service = Get-Service -Name $name
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $name -ErrorAction Stop
            (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }
}
function Restore-ServiceStates($States) {
    foreach ($name in @('SqlObserverTestServer', 'SqlObserverTestCollector')) {
        if ($States[$name] -eq 'Running') {
            Start-Service -Name $name -ErrorAction Stop
            (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        }
    }
    Start-Sleep -Seconds 3
    foreach ($name in $States.Keys) {
        if ([string](Get-Service -Name $name).Status -cne $States[$name]) { throw "Service did not retain its previous state: $name" }
    }
}
function Assert-Payload([string] $Root, $Files) {
    $actual = @(foreach ($component in @('Application', 'Setup')) {
        $directory = Join-Path $Root $component
        Assert-Tree $directory
        Get-ChildItem -LiteralPath $directory -Recurse -File -Force
    })
    if ($actual.Count -ne $Files.Count) { throw 'Patch payload file count differs from its manifest.' }
    foreach ($file in $Files) {
        if ($file.Path -notmatch '^(Application|Setup)/[A-Za-z0-9_./-]+$' -or $file.Path -match '(^|/)\.\.(/|$)') { throw 'Unsafe patch manifest path.' }
        $path = Assert-Child (Join-Path $Root $file.Path) $Root
        Assert-Hash $path $file.Sha256
    }
}

if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw 'Use Windows x64 PowerShell 7.5 or later.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this patch from an elevated PowerShell 7 console.'
    }
} finally { $identity.Dispose() }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$DataRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
if ($InstallRoot -ieq $DataRoot -or $DataRoot.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Configuration must be outside the application installation.' }
Assert-Tree $InstallRoot
Assert-Tree $DataRoot
Assert-Tree $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot 'patch-manifest.json'
Assert-Hash $manifestPath $expectedManifestHash
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Kind -cne 'sqlobserver-test-certificate-trust-patch' -or $manifest.SchemaVersion -ne 1) { throw 'Unsupported patch manifest.' }
$payload = Join-Path $PSScriptRoot 'Payload'
Assert-Payload $payload $manifest.Files
Assert-Hash (Join-Path $InstallRoot 'Application/package-manifest.json') $manifest.BaseApplicationManifestSha256
Assert-Hash (Join-Path $InstallRoot 'Setup/SqlObserver.TestSetup.dll') $manifest.BaseSetupDllSha256
$baseManifest = Get-Content -LiteralPath (Join-Path $InstallRoot 'Application/package-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $baseManifest.files) {
    $path = Assert-Child (Join-Path $InstallRoot ('Application/' + $file.path)) (Join-Path $InstallRoot 'Application')
    Assert-Hash $path $file.sha256
}
& (Join-Path $InstallRoot 'Application/Tools/test-lab-package.ps1') -PackageDirectory (Join-Path $InstallRoot 'Application')
if (!$?) { throw 'Installed application verification failed.' }
Assert-Hash (Join-Path $InstallRoot 'Application/Docs/repository-migrations.sha256') $manifest.MigrationCatalogSha256
Assert-Hash (Join-Path $payload 'Application/Docs/repository-migrations.sha256') $manifest.MigrationCatalogSha256
& (Join-Path $payload 'Setup/SqlObserver.TestSetup.exe') --verify
if ($LASTEXITCODE -ne 0) { throw 'Incoming setup helper verification failed.' }
if (@(Get-Process -Name 'SqlObserver.TestSetup' -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Close the setup configuration console before patching.' }

$states = @{}
$configurations = @{}
$originals = @{}
foreach ($component in @('Server', 'Collector')) {
    $name = 'SqlObserverTest' + $component
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'"
    $expected = '"' + (Join-Path $InstallRoot "Application/$component/SqlObserver.$component.exe") + '" --contentRoot "' + (Join-Path $DataRoot $component) + '"'
    if ($null -eq $service -or $service.PathName -ine $expected -or $service.State -notin @('Running', 'Stopped')) { throw "Unexpected service registration or state: $name" }
    $states[$name] = [string]$service.State
    $path = Join-Path $DataRoot "$component/appsettings.json"
    $originals[$component] = [IO.File]::ReadAllBytes($path)
    $configuration = [Text.Encoding]::UTF8.GetString($originals[$component]).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable -DateKind String
    $configuration = Set-TargetTrust $configuration $manifest.Target
    $configurations[$component] = [Text.UTF8Encoding]::new($false).GetBytes(($configuration | ConvertTo-Json -Depth 100) + "`n")
}
Write-Host 'Verified base application, migration catalog, incoming payload, and service paths.'
Write-Host "Certificate trust exception: $($manifest.Target.TargetId), $($manifest.Target.Host):$($manifest.Target.TcpPort). Encryption remains required."
if ($ValidateOnly) { Write-Host 'Preflight passed. No files or services were changed.'; return }

$run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$backup = Assert-Child (Join-Path $InstallRoot "PatchBackups/$run") $InstallRoot
$stage = Assert-Child (Join-Path $InstallRoot ('.patch-stage-' + $run)) $InstallRoot
# Protect the backup before writing configuration containing repository credentials.
[void][IO.Directory]::CreateDirectory($backup)
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $backup -AclObject $acl
foreach ($component in $originals.Keys) { [IO.File]::WriteAllBytes((Join-Path $backup "$component.appsettings.json"), $originals[$component]) }
[void][IO.Directory]::CreateDirectory($stage)
Copy-Item -LiteralPath (Join-Path $payload 'Application'), (Join-Path $payload 'Setup') -Destination $stage -Recurse
Assert-Payload $stage $manifest.Files
$moved = [Collections.Generic.List[string]]::new()
$configChanged = [Collections.Generic.List[string]]::new()
$phase = 'stop-services'
try {
    Write-Host 'Stopping test services and retaining the existing application/helper...'
    Stop-TestServices
    $phase = 'replace-application'
    foreach ($component in @('Application', 'Setup')) {
        Move-InstallDirectory (Join-Path $InstallRoot $component) (Join-Path $backup $component)
        $moved.Add($component)
        Move-InstallDirectory (Join-Path $stage $component) (Join-Path $InstallRoot $component)
    }
    $phase = 'configure-target-trust'
    foreach ($component in @('Server', 'Collector')) {
        $configChanged.Add($component)
        Write-Configuration (Join-Path $DataRoot "$component/appsettings.json") $configurations[$component]
    }
    $phase = 'verify-installed-payload'
    Assert-Payload $InstallRoot $manifest.Files
    & (Join-Path $InstallRoot 'Setup/SqlObserver.TestSetup.exe') --verify
    if ($LASTEXITCODE -ne 0) { throw 'Installed setup helper verification failed.' }
    $phase = 'restart-services'
    Restore-ServiceStates $states
    $receipt = [ordered]@{ Status='applied'; SourceCommit=$manifest.SourceCommit; Target=$manifest.Target; PreviousServiceStates=$states; CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    [IO.File]::WriteAllText((Join-Path $backup 'patch-result.json'), ($receipt | ConvertTo-Json -Depth 5))
    Write-Host 'Patch applied. Previous service running states were restored. Repository data and service identities were retained.'
    Write-Host "Protected backup: $backup"
    Write-Host 'Open SQL-QA-DB03 target settings, request rediscovery, then refresh/retry health access.'
} catch {
    $failure = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    try { Stop-TestServices } catch { $rollbackErrors.Add('stop-services') }
    foreach ($component in $configChanged) {
        try { Write-Configuration (Join-Path $DataRoot "$component/appsettings.json") $originals[$component] } catch { $rollbackErrors.Add("restore-$component-configuration") }
    }
    foreach ($component in $moved) {
        try {
            $current = Join-Path $InstallRoot $component
            if (Test-Path -LiteralPath $current) { Move-InstallDirectory $current (Join-Path $backup ("Failed-$component")) }
            Move-InstallDirectory (Join-Path $backup $component) $current
        } catch { $rollbackErrors.Add("restore-$component-files") }
    }
    if ($rollbackErrors.Count -eq 0) { try { Restore-ServiceStates $states } catch { $rollbackErrors.Add('restore-service-states') } }
    $receipt = [ordered]@{ Status='failed'; Phase=$phase; ExceptionType=$failure.Exception.GetType().Name; RollbackErrors=@($rollbackErrors); FailedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    [IO.File]::WriteAllText((Join-Path $backup 'patch-result.json'), ($receipt | ConvertTo-Json -Depth 5))
    Write-Host "Patch failed during $phase. Protected backup: $backup"
    if ($rollbackErrors.Count -eq 0) { Write-Host 'Previous files, configurations, and service states were restored.' }
    else { Write-Host "Rollback needs attention: $($rollbackErrors -join ', '). Inspect the protected patch-result.json." }
    throw 'The test patch did not complete.'
}
