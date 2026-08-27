[CmdletBinding()]
param(
    [string] $ManifestPath,

    [ValidateSet('Local', 'Release')]
    [string] $Profile = 'Local',

    [string] $RepositoryRoot,

    [string] $ExpectedCommitSha,

    [switch] $MatrixOnly
)

# This verifier is deliberately self-contained and read-only.  It is the
# boundary between test runners (which can be noisy or unavailable) and
# release evidence.  Do not add logging of input values here: manifests can
# contain host names and other sensitive operational details.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This is the reviewed policy anchor.  The checked-in matrix, schemas and pin
# file are inputs, not authority: changing them requires a source review and a
# new verifier provenance/release.  Keep these inventories in lockstep with
# the versioned policy update procedure in certification/README.md.
$PolicyId = 'sqlobserver-m12-policy-v1'
$PolicySchemaVersion = 1
$ApprovedLaneIds = @('repository-contract','release-build','unit','api','security','performance','end-to-end','frontend','mcp','lifecycle','trusted-tls','postgresql','sqlserver','browser','installer','observability','reports','sustained-performance','supply-chain','signing')
$ApprovedCaseIds = @('m12-repository-contract','m12-release-build','m12-unit-tests','m12-api-contract-tests','m12-security-tests','m12-performance-tests','m12-e2e-composition','m12-frontend','m12-mcp-protocol','m12-windows-server-2022-lifecycle','m12-windows-server-2025-lifecycle','m12-trusted-tls-wia-gmsa-kerberos','m12-postgresql-min-patch','m12-postgresql-current-patch','m12-sqlserver-2019-passive','m12-sqlserver-2022-passive','m12-sqlserver-2025-passive','m12-edge-browser','m12-chrome-browser','m12-installer-install','m12-installer-upgrade','m12-installer-recovery','m12-installer-uninstall','m12-readiness-observability','m12-telemetry-observability','m12-reports-exports','m12-report-contract','m12-export-contract','m12-sustained-performance','m12-sbom','m12-licenses','m12-vulnerability-scan','m12-provenance','m12-runbooks','m12-signing')
$ApprovedAssetHashes = @{
    'm12-certification-matrix.v1.json' = 'accdbd6d90f3012a7841daebf51b039b2ef574865476fcb75d682ff1c50a9330'
    'm12-certification-matrix.v1.schema.json' = '32c6a01aee7cb5884572410e85c5efa5b1dc2b2fef11049aab069512723bd5fe'
    'm12-certification-manifest.v1.schema.json' = '3613c4ac3fa4d39b6e96bb8a6aa2546f625f594ef287483b8c8e33749774dda6'
}

if (-not ('M12FileIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
public static class M12FileIdentity {
  [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation; public System.Runtime.InteropServices.ComTypes.FILETIME Access; public System.Runtime.InteropServices.ComTypes.FILETIME Write; public uint Volume; public uint SizeHigh; public uint SizeLow; public uint Links; public uint IndexHigh; public uint IndexLow; }
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info info);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern uint GetFinalPathNameByHandle(SafeFileHandle h, System.Text.StringBuilder path, uint length, uint flags);
  public static string Identity(SafeFileHandle h) { if (!GetFileInformationByHandle(h, out var i)) throw new InvalidOperationException(); return i.Volume.ToString("x8") + ":" + ((ulong)i.IndexHigh << 32 | i.IndexLow).ToString("x16"); }
  public static long Length(SafeFileHandle h) { if (!GetFileInformationByHandle(h, out var i)) throw new InvalidOperationException(); return ((long)i.SizeHigh << 32) | i.SizeLow; }
  public static uint Links(SafeFileHandle h) { if (!GetFileInformationByHandle(h, out var i)) throw new InvalidOperationException(); return i.Links; }
  public static DateTime LastWriteUtc(SafeFileHandle h) { if (!GetFileInformationByHandle(h, out var i)) throw new InvalidOperationException(); long ticks = ((long)i.Write.dwHighDateTime << 32) | (uint)i.Write.dwLowDateTime; return DateTime.FromFileTimeUtc(ticks); }
  public static string FinalPath(SafeFileHandle h) { var b = new System.Text.StringBuilder(32768); var n = GetFinalPathNameByHandle(h, b, (uint)b.Capacity, 0); if (n == 0 || n >= b.Capacity) throw new InvalidOperationException(); return b.ToString(); }
}
'@
}

function Reject([string] $Code) {
    throw "M12-EVIDENCE-$Code"
}

function Require-Shape {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)] [string] $Name,
        [string[]] $Required = @(),
        [string[]] $Allowed = @()
    )

    if ($null -eq $Value -or $Value -is [System.Array] -or $Value -is [string] -or
        $null -eq $Value.PSObject.Properties) { Reject 'SCHEMA' }
    $names = @($Value.PSObject.Properties | ForEach-Object { [string] $_.Name })
    foreach ($property in $Required) {
        if ($names -cnotcontains $property) { Reject 'SCHEMA' }
    }
    foreach ($property in $names) {
        if ($Allowed -cnotcontains $property) { Reject 'SCHEMA' }
    }
}

function Require-Array([object] $Value, [string] $Code = 'SCHEMA') {
    if ($null -eq $Value -or $Value -is [string] -or $Value -isnot [System.Collections.IEnumerable]) { Reject $Code }
    return @($Value)
}

function Require-String([object] $Value, [string] $Pattern, [string] $Code = 'SCHEMA') {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch $Pattern) { Reject $Code }
    return [string] $Value
}

function Require-Integer([object] $Value, [int] $Minimum = 0, [string] $Code = 'SCHEMA') {
    if ($Value -isnot [int] -and $Value -isnot [long] -and $Value -isnot [decimal]) { Reject $Code }
    $number = [long] $Value
    if ($number -lt $Minimum) { Reject $Code }
    return $number
}

function Require-Boolean([object] $Value, [string] $Code = 'SCHEMA') {
    if ($Value -isnot [bool]) { Reject $Code }
    return [bool] $Value
}

function Require-UtcTimestamp([object] $Value) {
    $text = Require-String $Value '^[0-9]{4}-[0-9]{2}-[0-9]{2}T' 'TIME'
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref] $parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero) { Reject 'TIME' }
    return $parsed
}

function Ensure-Unique([string[]] $Values, [string] $Code = 'DUPLICATE') {
    if ((@($Values | Sort-Object -Unique)).Count -ne @($Values).Count) { Reject $Code }
}

function Resolve-SafeFile {
    param([string] $RelativePath, [string] $Root)
    Require-String $RelativePath '^(?![A-Za-z]:)(?![\\/])[^\x00]+$' 'PATH' | Out-Null
    if ($RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or $RelativePath -match '(^|[\\/])\.$') { Reject 'PATH' }
    $full = [IO.Path]::GetFullPath((Join-Path -Path $Root -ChildPath $RelativePath))
    $rootWithSeparator = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $full -PathType Leaf)) { Reject 'PATH' }
    $current = $full
    while ($true) {
        $item = Get-Item -LiteralPath $current
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Reject 'PATH' }
        if ([string]::Equals($current.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent -or
            (-not [string]::Equals($parent.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -and
             -not $parent.FullName.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase))) { Reject 'PATH' }
        $current = $parent.FullName
    }
    return $full
}

function Verify-HashEntry {
    param([object] $Entry, [string] $Root, [string] $Code, [long] $MaxBytes = 268435456, [DateTimeOffset] $FreshnessStart = [DateTimeOffset]::MinValue, [DateTimeOffset] $VerificationNow = [DateTimeOffset]::MinValue)
    if ($VerificationNow -eq [DateTimeOffset]::MinValue) { $VerificationNow = [DateTimeOffset]::UtcNow }
    if ($null -eq $Entry.PSObject.Properties['path'] -or $null -eq $Entry.PSObject.Properties['sha256']) { Reject 'SCHEMA' }
    $path = Require-String $Entry.path '^(?![A-Za-z]:)(?![\\/])[^\x00]+$' 'PATH'
    if ($path -match ':' -or $path -match '(^|[\\/])[^\\/]*[\. ]([\\/]|$)' -or $path -match '(^|[\\/])[^\\/]*[\. ]$') { Reject 'PATH' }
    $expected = Require-String $Entry.sha256 '^[0-9a-fA-F]{64}$' 'HASH'
    $full = Resolve-SafeFile $path $Root
    try {
        # FileShare.Read denies concurrent write/delete while this single open
        # handle is hashed.  The verifier is fail-closed if the host cannot
        # provide this Windows sharing guarantee.
        $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($IsWindows) {
                try {
                    $nativeFinal = [M12FileIdentity]::FinalPath($stream.SafeFileHandle)
                    $fileIdentity = [M12FileIdentity]::Identity($stream.SafeFileHandle)
                    $handleLength = [M12FileIdentity]::Length($stream.SafeFileHandle)
                    $linkCount = [M12FileIdentity]::Links($stream.SafeFileHandle)
                    $lastWrite = [DateTimeOffset]([M12FileIdentity]::LastWriteUtc($stream.SafeFileHandle))
                } catch {
                    # Native handle identity is part of the Windows evidence
                    # boundary for both profiles. Falling back to path identity
                    # would silently accept a multiply-linked file.
                    Reject 'HOST'
                }
                if ($linkCount -ne 1) { Reject 'FILE' }
            } elseif ($Profile -eq 'Release') {
                Reject 'HOST'
            } else {
                $nativeFinal = $full
                $fileIdentity = $full.ToUpperInvariant()
                $handleLength = $stream.Length
                $lastWrite = [DateTimeOffset][IO.File]::GetLastWriteTimeUtc($full)
            }
            $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            $nativeFinal = $nativeFinal.TrimStart('\\?\')
            if (-not $nativeFinal.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { Reject 'PATH' }
            $lowerBound = $VerificationNow.AddHours(-24)
            if ($FreshnessStart -ne [DateTimeOffset]::MinValue -and $FreshnessStart.AddHours(-24) -gt $lowerBound) { $lowerBound = $FreshnessStart.AddHours(-24) }
            $upperBound = $VerificationNow.AddMinutes(5)
            if ($FreshnessStart -ne [DateTimeOffset]::MinValue -and $FreshnessStart.AddMinutes(5) -lt $upperBound) { $upperBound = $FreshnessStart.AddMinutes(5) }
            if ($handleLength -lt 1 -or $handleLength -gt $MaxBytes -or $handleLength -ne $stream.Length -or $lastWrite -lt $lowerBound -or $lastWrite -gt $upperBound) { Reject 'FILE' }
            $buffer = [IO.MemoryStream]::new(); $stream.CopyTo($buffer); $bytes = $buffer.ToArray(); $actual = ([Security.Cryptography.SHA256]::HashData($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''; $buffer.Dispose()
        } finally { $stream.Dispose() }
    } catch { Reject 'FILE' }
    if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) { Reject $Code }
    return [pscustomobject]@{ Canonical = [IO.Path]::GetFullPath($full).ToUpperInvariant(); Identity = $fileIdentity; Bytes = $bytes }
}

function Read-SafeAsset {
    param([string] $Path, [string] $Root, [long] $MaxBytes = 16777216)
    if ([IO.Path]::IsPathRooted($Path)) {
        $full = [IO.Path]::GetFullPath($Path)
        $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { Reject 'PATH' }
        $current = $full
        while ($true) {
            $item = Get-Item -LiteralPath $current -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Reject 'PATH' }
            if ([string]::Equals($current.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { break }
            $parent = [IO.Directory]::GetParent($current); if ($null -eq $parent) { Reject 'PATH' }; $current = $parent.FullName
        }
    } else { $full = Resolve-SafeFile $Path $Root }
    try {
        $item = Get-Item -LiteralPath $full -ErrorAction Stop
        if ($item.Length -lt 1 -or $item.Length -gt $MaxBytes) { Reject 'FILE' }
        $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $buffer = [IO.MemoryStream]::new(); $stream.CopyTo($buffer); $bytes = $buffer.ToArray(); $buffer.Dispose() } finally { $stream.Dispose() }
    } catch { Reject 'FILE' }
    return [pscustomobject]@{ Bytes = $bytes; Hash = (([Security.Cryptography.SHA256]::HashData($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''); Canonical = [IO.Path]::GetFullPath($full).ToUpperInvariant() }
}

function Verify-EvidenceFile {
    param([byte[]] $Bytes, [string] $Format, [string] $CaseId, [long] $Executions, [long] $Skipped, [long] $NotRun, [long] $Failed, [string] $RunId, [string] $Commit, [string] $Identity)
    if ($Format -eq 'json') {
        try { $content = [Text.Encoding]::UTF8.GetString($Bytes); $value = $content | ConvertFrom-Json -DateKind String } catch { Reject 'EVIDENCE' }
        Require-Shape $value 'laneResult' @('caseId', 'status', 'executions', 'skipped', 'notRun', 'failed', 'runId', 'commitSha', 'environmentId') @('caseId', 'status', 'executions', 'skipped', 'notRun', 'failed', 'runId', 'commitSha', 'environmentId')
        if ($value.caseId -cne $CaseId -or $value.status -cne 'passed' -or
            (Require-String $value.runId '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' 'EVIDENCE') -cne $RunId -or
            -not [string]::Equals((Require-String $value.commitSha '^[0-9a-fA-F]{40}$' 'EVIDENCE'), $Commit, [StringComparison]::OrdinalIgnoreCase) -or
            (Require-String $value.environmentId '^[a-z][a-z0-9-]+$' 'EVIDENCE') -cne $Identity -or
            (Require-Integer $value.executions 1 'EVIDENCE') -ne $Executions -or
            (Require-Integer $value.skipped 0 'EVIDENCE') -ne $Skipped -or
            (Require-Integer $value.notRun 0 'EVIDENCE') -ne $NotRun -or
            (Require-Integer $value.failed 0 'EVIDENCE') -ne $Failed) { Reject 'EVIDENCE' }
        return
    }
    try { [xml] $xml = [Text.Encoding]::UTF8.GetString($Bytes) } catch { Reject 'EVIDENCE' }
    function Verify-XmlBinding([System.Xml.XmlDocument] $Document) {
        $root = $Document.DocumentElement
        if ($null -eq $root -or $root.GetAttribute('sqlobserverRunId') -cne $RunId -or $root.GetAttribute('sqlobserverCommitSha') -cne $Commit -or $root.GetAttribute('sqlobserverEnvironmentId') -cne $Identity) { Reject 'EVIDENCE' }
    }
    if ($Format -eq 'trx') {
        Verify-XmlBinding $xml
        $results = @($xml.SelectNodes('//*[local-name()="UnitTestResult"]'))
        if ($results.Count -lt 1 -or @($results | Where-Object { $_.testName -cne $CaseId -or $_.outcome -cne 'Passed' }).Count -ne 0) { Reject 'EVIDENCE' }
        $failedActual = 0
        $skippedActual = 0
        $notRunActual = 0
        if ($results.Count -ne $Executions -or $failedActual -ne $Failed -or $skippedActual -ne $Skipped -or $notRunActual -ne $NotRun) { Reject 'EVIDENCE' }
        return
    }
    if ($Format -eq 'junit') {
        Verify-XmlBinding $xml
        $results = @($xml.SelectNodes('//*[local-name()="testcase"]'))
        if ($results.Count -lt 1 -or @($results | Where-Object { $_.name -cne $CaseId }).Count -ne 0) { Reject 'EVIDENCE' }
        $failedActual = @($results | Where-Object { $null -ne $_.SelectSingleNode('./*[local-name()="failure" or local-name()="error"]') }).Count
        $skippedActual = @($results | Where-Object { $null -ne $_.SelectSingleNode('./*[local-name()="skipped"]') }).Count
        $suite = $xml.SelectSingleNode('//*[local-name()="testsuite"]')
        if ($null -eq $suite -or $suite.GetAttribute('tests') -ne [string]$results.Count -or $suite.GetAttribute('failures') -ne [string]$failedActual -or $suite.GetAttribute('errors') -ne '0' -or $suite.GetAttribute('skipped') -ne [string]$skippedActual) { Reject 'EVIDENCE' }
        if ($results.Count -ne $Executions -or $failedActual -ne $Failed -or $skippedActual -ne $Skipped -or $NotRun -ne 0) { Reject 'EVIDENCE' }
        return
    }
    Reject 'EVIDENCE'
}

function Read-JsonWithSchema {
    param([string] $JsonPath, [string] $SchemaPath, [string] $SchemaCode, [string] $ExpectedJsonHash, [string] $ExpectedSchemaHash, [string] $Root)
    $schemaFile = Read-SafeAsset $SchemaPath $Root
    $jsonFile = Read-SafeAsset $JsonPath $Root
    if ($schemaFile.Hash -cne $ExpectedSchemaHash.ToLowerInvariant() -or (-not [string]::IsNullOrWhiteSpace($ExpectedJsonHash) -and $jsonFile.Hash -cne $ExpectedJsonHash.ToLowerInvariant())) { Reject 'SCHEMA' }
    $schemaJson = [Text.Encoding]::UTF8.GetString($schemaFile.Bytes)
    try { $schemaObject = $schemaJson | ConvertFrom-Json -DateKind String } catch { Reject 'SCHEMA' }
    if ($schemaObject.'$schema' -cne 'https://json-schema.org/draft/2020-12/schema') { Reject 'SCHEMA' }
    $json = [Text.Encoding]::UTF8.GetString($jsonFile.Bytes)
    try { $value = $json | ConvertFrom-Json -DateKind String } catch { Reject 'SCHEMA' }
    try { $valid = Test-Json -Json $json -Schema $schemaJson -ErrorAction Stop } catch { Reject 'SCHEMA' }
    if (-not $valid) { Reject $SchemaCode }
    return [pscustomobject]@{ Raw = $json; Value = $value }
}

try {
    if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [version]'7.5.0') { Reject 'HOST' }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    } else {
        $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    }
    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { Reject 'PATH' }
    $matrixPath = Join-Path $RepositoryRoot 'release/certification/m12-certification-matrix.v1.json'
    $matrixSchemaPath = Join-Path $RepositoryRoot 'release/certification/m12-certification-matrix.v1.schema.json'
    $manifestSchemaPath = Join-Path $RepositoryRoot 'release/certification/m12-certification-manifest.v1.schema.json'
    $pinPath = Join-Path $RepositoryRoot 'release/certification/m12-certification-assets.sha256'
    $pinFile = Read-SafeAsset 'release/certification/m12-certification-assets.sha256' $RepositoryRoot 65536
    $pins = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $pinLines = [IO.File]::ReadAllLines($pinPath)
    foreach ($line in $pinLines) {
        if ($line -notmatch '^([0-9a-f]{64})  ([^\\/]+)$') { Reject 'SCHEMA' }
        $pinName = $Matches[2]
        if ($pins.ContainsKey($pinName)) { Reject 'SCHEMA' }
        $pins.Add($pinName, $Matches[1])
    }
    $expectedPinNames = @('m12-certification-matrix.v1.json', 'm12-certification-matrix.v1.schema.json', 'm12-certification-manifest.v1.schema.json')
    if ($pinLines.Count -ne 3 -or $pins.Count -ne 3 -or
        (@($pins.Keys | Sort-Object) -join '|') -cne (@($expectedPinNames | Sort-Object) -join '|')) { Reject 'SCHEMA' }
    foreach ($assetName in $ApprovedAssetHashes.Keys) {
        if ($ApprovedAssetHashes[$assetName] -notmatch '^[0-9a-fA-F]{64}$' -or $pins[$assetName] -cne $ApprovedAssetHashes[$assetName].ToLowerInvariant()) { Reject 'SCHEMA' }
    }
    $matrixBundle = Read-JsonWithSchema $matrixPath $matrixSchemaPath 'MATRIX' $ApprovedAssetHashes['m12-certification-matrix.v1.json'] $ApprovedAssetHashes['m12-certification-matrix.v1.schema.json'] $RepositoryRoot
    $matrix = $matrixBundle.Value
    Require-Shape $matrix 'matrix' @('$schema', 'schemaVersion', 'matrixId', 'version', 'environmentIdentities', 'profiles', 'lanes') @('$schema', 'schemaVersion', 'matrixId', 'version', 'environmentIdentities', 'profiles', 'lanes')
    if ($matrix.'$schema' -cne 'm12-certification-matrix.v1.schema.json' -or $matrix.schemaVersion -ne 1 -or $matrix.matrixId -cne 'sqlobserver-m12') { Reject 'MATRIX' }
    $environmentIdentities = @(Require-Array $matrix.environmentIdentities 'MATRIX' | ForEach-Object { Require-String $_ '^[a-z][a-z0-9-]+$' 'MATRIX' })
    Ensure-Unique $environmentIdentities 'MATRIX'
    if ($matrix.version -cne '1.2.0') { Reject 'MATRIX' }
    Require-Shape $matrix.profiles 'profiles' @('Local', 'Release') @('Local', 'Release')
    $profileDefinition = $matrix.profiles.$Profile
    Require-Shape $profileDefinition 'profile' @('releaseEvidence', 'requiredLanes', 'omittedExternalLanes') @('releaseEvidence', 'requiredLanes', 'omittedExternalLanes')
    $releaseEvidence = Require-Boolean $profileDefinition.releaseEvidence 'MATRIX'
    if (($Profile -eq 'Release') -ne $releaseEvidence) { Reject 'MATRIX' }

    $matrixLanes = @{}
    $matrixCaseToLane = @{}
    $matrixCaseRequirements = @{}
    foreach ($lane in (Require-Array $matrix.lanes 'MATRIX')) {
        Require-Shape $lane 'lane' @('laneId', 'producerId', 'implementationStatus', 'cases') @('laneId', 'producerId', 'implementationStatus', 'cases')
        $laneId = Require-String $lane.laneId '^[a-z][a-z0-9-]+$' 'MATRIX'
        $laneProducer = Require-String $lane.producerId '^[a-z][a-z0-9-]+$' 'MATRIX'
        $laneStatus = Require-String $lane.implementationStatus '^(implemented|pending)$' 'MATRIX'
        if ($ApprovedLaneIds -cnotcontains $laneId) { Reject 'MATRIX' }
        if ($matrixLanes.ContainsKey($laneId)) { Reject 'MATRIX' }
        $caseIds = @()
        foreach ($matrixCase in (Require-Array $lane.cases 'MATRIX')) {
            Require-Shape $matrixCase 'matrixCase' @('caseId', 'producerId', 'implementationStatus', 'artifactKinds', 'environment') @('caseId', 'producerId', 'implementationStatus', 'artifactKinds', 'environment')
            $caseId = Require-String $matrixCase.caseId '^m12-[a-z0-9-]+$' 'MATRIX'
            $caseProducer = Require-String $matrixCase.producerId '^[a-z][a-z0-9-]+$' 'MATRIX'
            $caseStatus = Require-String $matrixCase.implementationStatus '^(implemented|pending)$' 'MATRIX'
            $caseArtifactKinds = @(Require-Array $matrixCase.artifactKinds 'MATRIX' | ForEach-Object { Require-String $_ '^[a-z][a-z0-9-]+$' 'MATRIX' })
            Ensure-Unique $caseArtifactKinds 'MATRIX'
            if ($ApprovedCaseIds -cnotcontains $caseId -or $caseProducer -cne $laneProducer -or $caseStatus -cne $laneStatus) { Reject 'MATRIX' }
            Require-Shape $matrixCase.environment 'matrixEnvironment' @('identities', 'requiredFacts', 'factPredicates') @('identities', 'requiredFacts', 'factPredicates')
            $caseIdentities = @(Require-Array $matrixCase.environment.identities 'MATRIX' | ForEach-Object { Require-String $_ '^[a-z][a-z0-9-]+$' 'MATRIX' })
            $caseFacts = @(Require-Array $matrixCase.environment.requiredFacts 'MATRIX' | ForEach-Object { Require-String $_ '^[a-z][a-zA-Z0-9]+$' 'MATRIX' })
            Require-Shape $matrixCase.environment.factPredicates 'factPredicates' @() @($matrixCase.environment.factPredicates.PSObject.Properties | ForEach-Object { $_.Name })
            if (@($matrixCase.environment.factPredicates.PSObject.Properties).Count -lt 1) { Reject 'MATRIX' }
            Ensure-Unique $caseIdentities 'MATRIX'; Ensure-Unique $caseFacts 'MATRIX'
            if ($caseIdentities.Count -lt 1 -or $caseFacts.Count -lt 1 -or $caseArtifactKinds.Count -lt 1 -or @($caseIdentities | Where-Object { $environmentIdentities -cnotcontains $_ }).Count -ne 0) { Reject 'MATRIX' }
            foreach ($predicate in $matrixCase.environment.factPredicates.PSObject.Properties) { if ($caseFacts -cnotcontains [string]$predicate.Name) { Reject 'MATRIX' } }
            if ($matrixCaseRequirements.ContainsKey($caseId)) { Reject 'MATRIX' }
            $caseIds += $caseId
            $matrixCaseRequirements[$caseId] = [pscustomobject]@{ LaneId = $laneId; Identities = $caseIdentities; RequiredFacts = $caseFacts; Predicates = $matrixCase.environment.factPredicates; Status = $caseStatus; ProducerId = $caseProducer; ArtifactKinds = $caseArtifactKinds }
        }
        Ensure-Unique $caseIds 'MATRIX'
        $matrixLanes[$laneId] = $caseIds
        foreach ($caseId in $caseIds) {
            if ($matrixCaseToLane.ContainsKey($caseId)) { Reject 'MATRIX' }
            $matrixCaseToLane[$caseId] = $laneId
        }
    }
    $requiredLanes = @(Require-Array $profileDefinition.requiredLanes 'MATRIX' | ForEach-Object { Require-String $_ '^[a-z][a-z0-9-]+$' 'MATRIX' })
    $omittedLanes = @(Require-Array $profileDefinition.omittedExternalLanes 'MATRIX' | ForEach-Object { Require-String $_ '^[a-z][a-z0-9-]+$' 'MATRIX' })
    if ($requiredLanes.Count -lt 1) { Reject 'MATRIX' }
    Ensure-Unique $requiredLanes 'MATRIX'; Ensure-Unique $omittedLanes 'MATRIX'
    foreach ($laneId in $requiredLanes + $omittedLanes) { if (-not $matrixLanes.ContainsKey($laneId)) { Reject 'MATRIX' } }
    if (@($requiredLanes | Where-Object { $omittedLanes -contains $_ }).Count -ne 0) { Reject 'MATRIX' }
    $profilePartition = @($requiredLanes + $omittedLanes)
    Ensure-Unique $profilePartition 'MATRIX'
    if ($profilePartition.Count -ne $matrixLanes.Count) { Reject 'MATRIX' }
    if ($matrixLanes.Count -ne $ApprovedLaneIds.Count -or ((@($matrixLanes.Keys | Sort-Object) -join '|') -cne (@($ApprovedLaneIds | Sort-Object) -join '|')) -or $matrixCaseToLane.Count -ne $ApprovedCaseIds.Count -or ((@($matrixCaseToLane.Keys | Sort-Object) -join '|') -cne (@($ApprovedCaseIds | Sort-Object) -join '|'))) { Reject 'MATRIX' }

    if ($MatrixOnly) {
        [ordered]@{ valid = $true; policy = $PolicyId; schemaVersion = $PolicySchemaVersion; matrix = 'sqlobserver-m12'; profiles = @('Local', 'Release'); lanes = $matrixLanes.Count; cases = $matrixCaseToLane.Count } | ConvertTo-Json -Compress
        exit 0
    }
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { Reject 'INPUT' }
    $manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) { Reject 'INPUT' }
    $manifestBundle = Read-JsonWithSchema $manifestFullPath $manifestSchemaPath 'SCHEMA' '' $ApprovedAssetHashes['m12-certification-manifest.v1.schema.json'] $RepositoryRoot
    $manifest = $manifestBundle.Value

    Require-Shape $manifest 'manifest' @('$schema', 'schemaVersion', 'matrixId', 'profile', 'commitSha', 'generatedAtUtc', 'runId', 'environments', 'artifacts', 'lanes', 'result') @('$schema', 'schemaVersion', 'matrixId', 'profile', 'commitSha', 'generatedAtUtc', 'runId', 'environments', 'products', 'artifacts', 'lanes', 'result')
    if ($manifest.'$schema' -cne 'm12-certification-manifest.v1.schema.json' -or $manifest.schemaVersion -ne 1 -or $manifest.matrixId -cne 'sqlobserver-m12' -or $manifest.profile -cne $Profile) { Reject 'SCHEMA' }
    $commit = Require-String $manifest.commitSha '^[0-9a-fA-F]{40}$' 'COMMIT'
    $currentHead = (& git -C $RepositoryRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $currentHead -notmatch '^[0-9a-fA-F]{40}$') { Reject 'COMMIT' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or -not [string]::Equals($ExpectedCommitSha.Trim(), $currentHead.Trim(), [StringComparison]::OrdinalIgnoreCase))) { Reject 'COMMIT' }
    if (-not [string]::Equals($commit, $currentHead.Trim(), [StringComparison]::OrdinalIgnoreCase)) { Reject 'COMMIT' }
    $verificationNow = [DateTimeOffset]::UtcNow
    $generatedAt = Require-UtcTimestamp $manifest.generatedAtUtc
    if ($generatedAt -gt $verificationNow.AddMinutes(5) -or $generatedAt -lt $verificationNow.AddHours(-24)) { Reject 'TIME' }
    $runId = Require-String $manifest.runId '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' 'RUN'

    $manifestEnvironments = @{}
    foreach ($environment in (Require-Array $manifest.environments 'ENV')) {
        Require-Shape $environment 'environment' @('environmentId', 'identity', 'os', 'architecture', 'facts') @('environmentId', 'identity', 'os', 'architecture', 'facts')
        $environmentId = Require-String $environment.environmentId '^[a-z][a-z0-9-]+$' 'ENV'
        $identity = Require-String $environment.identity '^[a-z][a-z0-9-]+$' 'ENV'
        if ($manifestEnvironments.ContainsKey($environmentId) -or $environmentIdentities -cnotcontains $identity) { Reject 'ENV' }
        if ($Profile -eq 'Local' -and $identity -cne 'local-windows') { Reject 'ENV' }
        if ($Profile -eq 'Release' -and $identity -notmatch '^release-windows-server-(2022|2025)$') { Reject 'ENV' }
        $osName = Require-String $environment.os '^Windows' 'ENV'
        if (($identity -eq 'local-windows' -and $osName -cne 'Windows 11') -or ($identity -eq 'release-windows-server-2022' -and $osName -cne 'Windows Server 2022') -or ($identity -eq 'release-windows-server-2025' -and $osName -cne 'Windows Server 2025')) { Reject 'ENV' }
        if ((Require-String $environment.architecture '^x64$' 'ENV') -ne 'x64') { Reject 'ENV' }
        Require-Shape $environment.facts 'facts' @() @($environment.facts.PSObject.Properties | ForEach-Object { $_.Name })
        if ($null -eq $environment.facts.os -or $environment.facts.os.GetType().FullName -ne 'System.String' -or $environment.facts.os -cne $osName -or $null -eq $environment.facts.architecture -or $environment.facts.architecture.GetType().FullName -ne 'System.String' -or $environment.facts.architecture -cne 'x64') { Reject 'ENV' }
        $factNames = @($environment.facts.PSObject.Properties | ForEach-Object { [string]$_.Name })
        if ($factNames -contains 'windowsServerVersion' -and ($environment.facts.windowsServerVersion -isnot [string] -or $environment.facts.windowsServerVersion -notmatch '^20[2-9][0-9]$')) { Reject 'ENV' }
        if ($factNames -contains 'postgresqlVersion' -and ($environment.facts.postgresqlVersion -isnot [string] -or $environment.facts.postgresqlVersion -notmatch '^[0-9]+(?:\.[0-9]+)?$')) { Reject 'ENV' }
        if ($factNames -contains 'postgresqlPatch' -and ($environment.facts.postgresqlPatch -isnot [string] -or $environment.facts.postgresqlPatch -notmatch '^(minimum|current)$')) { Reject 'ENV' }
        if ($factNames -contains 'sqlServerVersion' -and ($environment.facts.sqlServerVersion -isnot [int] -and $environment.facts.sqlServerVersion -isnot [long] -or [int]$environment.facts.sqlServerVersion -notin @(15, 16, 17))) { Reject 'ENV' }
        $manifestEnvironments[$environmentId] = [pscustomobject]@{ Value = $environment; Identity = $identity; FactNames = @($environment.facts.PSObject.Properties | ForEach-Object { [string] $_.Name }) }
    }
    if ($manifestEnvironments.Count -lt 1) { Reject 'ENV' }

    $artifactPaths = @()
    $artifactsById = @{}
    $seenFileIdentities = @{}
    $seenContentHashes = @{}
    $productsById = @{}
    $productPaths = @()
    $manifestProducts = @()
    if ($null -ne $manifest.PSObject.Properties['products']) { $manifestProducts = @(Require-Array $manifest.products 'PRODUCT'); if ($manifestProducts.Count -lt 1) { Reject 'PRODUCT' } }
    foreach ($product in $manifestProducts) {
        Require-Shape $product 'product' @('productId','path','sha256','size','runId','commitSha','environmentId') @('productId','path','sha256','size','runId','commitSha','environmentId')
        $productId = Require-String $product.productId '^[a-z][a-z0-9-]+$' 'PRODUCT'
        $productEnvironmentId = Require-String $product.environmentId '^[a-z][a-z0-9-]+$' 'ENV'
        if ($productsById.ContainsKey($productId) -or -not $manifestEnvironments.ContainsKey($productEnvironmentId) -or
            (Require-String $product.runId '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' 'RUN') -cne $runId -or
            -not [string]::Equals((Require-String $product.commitSha '^[0-9a-fA-F]{40}$' 'COMMIT'), $commit, [StringComparison]::OrdinalIgnoreCase)) { Reject 'PRODUCT' }
        if ($product.path -notmatch '(?i)\.(msi|exe)$') { Reject 'PRODUCT' }
        $productSize = Require-Integer $product.size 1 'PRODUCT'
        $productHash = (Require-String $product.sha256 '^[0-9a-fA-F]{64}$' 'HASH').ToLowerInvariant()
        $productFile = Verify-HashEntry $product $RepositoryRoot 'PRODUCT' 268435456 $generatedAt $verificationNow
        if ($productFile.Bytes.Length -ne $productSize) { Reject 'PRODUCT' }
        if ($seenFileIdentities.ContainsKey($productFile.Identity) -or $seenContentHashes.ContainsKey($productHash)) { Reject 'DUPLICATE' }
        $seenFileIdentities[$productFile.Identity] = $true
        $seenContentHashes[$productHash] = $true
        $productPaths += $productFile.Canonical
        $productsById[$productId] = [pscustomobject]@{ Value = $product; Hash = $productHash }
    }
    Ensure-Unique $productPaths 'DUPLICATE'
    foreach ($artifact in (Require-Array $manifest.artifacts 'SCHEMA')) {
        Require-Shape $artifact 'artifact' @('artifactId','kind','producerId','path','sha256','size','runId','commitSha','environmentId','provenance') @('artifactId','kind','producerId','path','sha256','size','runId','commitSha','environmentId','provenance','productId')
        $artifactEnvironmentId = Require-String $artifact.environmentId '^[a-z][a-z0-9-]+$' 'ENV'
        if (-not $manifestEnvironments.ContainsKey($artifactEnvironmentId) -or
            (Require-String $artifact.runId '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' 'RUN') -cne $runId -or
            -not [string]::Equals((Require-String $artifact.commitSha '^[0-9a-fA-F]{40}$' 'COMMIT'), $commit, [StringComparison]::OrdinalIgnoreCase)) { Reject 'ARTIFACT' }
        # A signed Burn bootstrapper is a shipped release artifact in its own
        # right; lifecycle evidence may therefore point directly at .exe as
        # well as the existing archive/MSI formats. Authenticode/signing
        # evidence remains a separate certification case.
        if ($artifact.path -match '(?i)(^|[\\/])README\.md$' -or $artifact.path -notmatch '(?i)\.(json|txt|trx|xml|zip|msi|exe)$') { Reject 'ARTIFACT' }
        $artifactSize = Require-Integer $artifact.size 1 'ARTIFACT'
        $artifactKind = Require-String $artifact.kind '^[a-z][a-z0-9-]+$' 'ARTIFACT'
        $artifactProducer = Require-String $artifact.producerId '^[a-z][a-z0-9-]+$' 'ARTIFACT'
        $artifactId = Require-String $artifact.artifactId '^[a-z][a-z0-9-]+$' 'ARTIFACT'
        $productId = $null
        if ($null -ne $artifact.PSObject.Properties['productId']) { $productId = Require-String $artifact.productId '^[a-z][a-z0-9-]+$' 'PRODUCT' }
        if ($null -ne $productId -and -not $productsById.ContainsKey($productId)) { Reject 'PRODUCT' }
        Require-Shape $artifact.provenance 'provenanceReference' @('path','sha256') @('path','sha256')
        $artifactHash = (Require-String $artifact.sha256 '^[0-9a-fA-F]{64}$' 'HASH').ToLowerInvariant()
        if ($artifactsById.ContainsKey($artifactId)) { Reject 'ARTIFACT' }
        $artifactFile = Verify-HashEntry $artifact $RepositoryRoot 'ARTIFACT' 268435456 $generatedAt $verificationNow
        if ($artifactFile.Bytes.Length -ne $artifactSize) { Reject 'ARTIFACT' }
        $artifactPaths += $artifactFile.Canonical
        if ($seenFileIdentities.ContainsKey($artifactFile.Identity)) { Reject 'DUPLICATE' }
        $seenFileIdentities[$artifactFile.Identity] = $true
        if ($seenContentHashes.ContainsKey($artifactHash)) { Reject 'DUPLICATE' }
        $seenContentHashes[$artifactHash] = $true

        # Provenance is a physically separate, manifest-bound sidecar.  It is
        # read through the same locked/native canonical boundary as artifacts
        # and evidence, then reconciled field-by-field with this artifact.
        $provenanceHash = (Require-String $artifact.provenance.sha256 '^[0-9a-fA-F]{64}$' 'HASH').ToLowerInvariant()
        $provenanceFile = Verify-HashEntry $artifact.provenance $RepositoryRoot 'PROVENANCE' 16777216 $generatedAt $verificationNow
        if ($seenFileIdentities.ContainsKey($provenanceFile.Identity) -or $seenContentHashes.ContainsKey($provenanceHash)) { Reject 'DUPLICATE' }
        $seenFileIdentities[$provenanceFile.Identity] = $true
        $seenContentHashes[$provenanceHash] = $true
        try { $provenanceJson = [Text.Encoding]::UTF8.GetString($provenanceFile.Bytes) | ConvertFrom-Json -DateKind String } catch { Reject 'PROVENANCE' }
        Require-Shape $provenanceJson 'provenanceSidecar' @('artifactId','kind','producerId','artifactSha256','artifactSize','commitSha','runId','environmentId','createdAtUtc') @('artifactId','kind','producerId','artifactSha256','artifactSize','commitSha','runId','environmentId','createdAtUtc','productId')
        $sidecarArtifactHash = (Require-String $provenanceJson.artifactSha256 '^[0-9a-fA-F]{64}$' 'PROVENANCE').ToLowerInvariant()
        $sidecarArtifactSize = Require-Integer $provenanceJson.artifactSize 1 'PROVENANCE'
        $sidecarRunId = Require-String $provenanceJson.runId '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' 'PROVENANCE'
        $sidecarCommit = Require-String $provenanceJson.commitSha '^[0-9a-fA-F]{40}$' 'PROVENANCE'
        $sidecarEnvironment = Require-String $provenanceJson.environmentId '^[a-z][a-z0-9-]+$' 'PROVENANCE'
        $sidecarProductId = $null
        if ($null -ne $provenanceJson.PSObject.Properties['productId']) { $sidecarProductId = Require-String $provenanceJson.productId '^[a-z][a-z0-9-]+$' 'PROVENANCE' }
        if (($null -eq $productId) -ne ($null -eq $sidecarProductId) -or ($null -ne $productId -and $productId -cne $sidecarProductId)) { Reject 'PROVENANCE' }
        $sidecarAt = Require-UtcTimestamp $provenanceJson.createdAtUtc
        $provenanceLower = $verificationNow.AddHours(-24); if ($generatedAt.AddHours(-24) -gt $provenanceLower) { $provenanceLower = $generatedAt.AddHours(-24) }
        $provenanceUpper = $verificationNow.AddMinutes(5); if ($generatedAt.AddMinutes(5) -lt $provenanceUpper) { $provenanceUpper = $generatedAt.AddMinutes(5) }
        if ($provenanceJson.artifactId -cne $artifactId -or $provenanceJson.kind -cne $artifactKind -or $provenanceJson.producerId -cne $artifactProducer -or $sidecarArtifactHash -cne $artifactHash -or $sidecarArtifactSize -ne $artifactSize -or -not [string]::Equals($sidecarCommit, $commit, [StringComparison]::OrdinalIgnoreCase) -or $sidecarRunId -cne $runId -or $sidecarEnvironment -cne $artifactEnvironmentId) { Reject 'PROVENANCE' }
        if ($sidecarAt -lt $provenanceLower -or $sidecarAt -gt $provenanceUpper) { Reject 'TIME' }
        $artifactsById[$artifactId] = [pscustomobject]@{ Value = $artifact; EnvironmentId = $artifactEnvironmentId; ProductId = $productId }
    }
    Ensure-Unique $artifactPaths 'DUPLICATE'

    $seenLanes = @{}
    $seenCases = @{}
    $usedArtifacts = @{}
    $usedProducts = @{}
    $seenEvidence = @{}
    $seenEvidenceHashes = @{}
    $installerProductSha256 = $null
    $hasPendingCase = $false
    foreach ($lane in (Require-Array $manifest.lanes 'SCHEMA')) {
        Require-Shape $lane 'lane' @('laneId', 'cases') @('laneId', 'cases')
        $laneId = Require-String $lane.laneId '^[a-z][a-z0-9-]+$' 'LANE'
        if ($seenLanes.ContainsKey($laneId) -or -not $matrixLanes.ContainsKey($laneId)) { Reject 'LANE' }
        $seenLanes[$laneId] = $true
        foreach ($case in (Require-Array $lane.cases 'SCHEMA')) {
            Require-Shape $case 'case' @('caseId', 'environmentId', 'status', 'executions', 'skipped', 'notRun', 'failed', 'artifactIds', 'evidence') @('caseId', 'environmentId', 'status', 'executions', 'skipped', 'notRun', 'failed', 'artifactIds', 'evidence')
            $caseId = Require-String $case.caseId '^m12-[a-z0-9-]+$' 'CASE'
            if ($seenCases.ContainsKey($caseId) -or -not $matrixCaseToLane.ContainsKey($caseId) -or $matrixCaseToLane[$caseId] -cne $laneId) { Reject 'CASE' }
            $matrixRequirement = $matrixCaseRequirements[$caseId]
            $caseEnvironmentId = Require-String $case.environmentId '^[a-z][a-z0-9-]+$' 'ENV'
            if (-not $manifestEnvironments.ContainsKey($caseEnvironmentId) -or $matrixRequirement.Identities -cnotcontains $manifestEnvironments[$caseEnvironmentId].Identity) { Reject 'ENV' }
            $caseIdentity = $manifestEnvironments[$caseEnvironmentId].Identity
            $caseArtifactIds = @(Require-Array $case.artifactIds 'ARTIFACT' | ForEach-Object { Require-String $_ '^[a-z][a-z0-9-]+$' 'ARTIFACT' })
            if ($caseArtifactIds.Count -lt 1) { Reject 'ARTIFACT' }
            foreach ($caseArtifactId in $caseArtifactIds) {
                if (-not $artifactsById.ContainsKey($caseArtifactId) -or $artifactsById[$caseArtifactId].EnvironmentId -cne $caseEnvironmentId -or $usedArtifacts.ContainsKey($caseArtifactId)) { Reject 'ARTIFACT' }
                $referencedArtifact = $artifactsById[$caseArtifactId].Value
                if ($referencedArtifact.producerId -cne $matrixRequirement.ProducerId -or $matrixRequirement.ArtifactKinds -cnotcontains $referencedArtifact.kind) { Reject 'ARTIFACT' }
                $referencedProductId = $artifactsById[$caseArtifactId].ProductId
                if ($null -ne $referencedProductId) { $usedProducts[$referencedProductId] = $true }
                if ($Profile -eq 'Release' -and $matrixRequirement.LaneId -eq 'installer') {
                    $caseProductId = $referencedProductId
                    if ($null -eq $caseProductId -or -not $productsById.ContainsKey($caseProductId)) { Reject 'ARTIFACT' }
                    if ($null -eq $installerProductSha256) { $installerProductSha256 = $caseProductId }
                    elseif ($installerProductSha256 -cne $caseProductId) { Reject 'ARTIFACT' }
                }
                $usedArtifacts[$caseArtifactId] = $true
            }
            $caseFactNames = $manifestEnvironments[$caseEnvironmentId].FactNames
            foreach ($requiredFact in $matrixRequirement.RequiredFacts) {
                if ($caseFactNames -cnotcontains $requiredFact -or $null -eq $manifestEnvironments[$caseEnvironmentId].Value.facts.$requiredFact -or
                    ($manifestEnvironments[$caseEnvironmentId].Value.facts.$requiredFact -is [string] -and [string]::IsNullOrWhiteSpace($manifestEnvironments[$caseEnvironmentId].Value.facts.$requiredFact))) { Reject 'ENV' }
            }
            foreach ($predicate in $matrixRequirement.Predicates.PSObject.Properties) {
                if ($caseFactNames -cnotcontains $predicate.Name) { Reject 'ENV' }
                $actualFact = $manifestEnvironments[$caseEnvironmentId].Value.facts.$($predicate.Name)
                $expectedFact = $predicate.Value
                if ($null -eq $actualFact -or $actualFact.GetType().FullName -ne $expectedFact.GetType().FullName -or $actualFact -cne $expectedFact) { Reject 'ENV' }
            }
            $seenCases[$caseId] = $true
            if ($matrixRequirement.Status -cne 'implemented' -or $matrixRequirement.ProducerId -notmatch '^m12-') { $hasPendingCase = $true }
            if ($case.status -cne 'passed') { Reject 'CASE' }
            Require-Integer $case.executions 1 'COUNT' | Out-Null
            foreach ($countName in @('skipped', 'notRun', 'failed')) { if ((Require-Integer $case.$countName 0 'COUNT') -ne 0) { Reject 'COUNT' } }
            foreach ($evidence in (Require-Array $case.evidence 'SCHEMA')) {
                Require-Shape $evidence 'evidence' @('path', 'sha256', 'format', 'runId', 'commitSha', 'environmentId') @('path', 'sha256', 'format', 'runId', 'commitSha', 'environmentId')
                $evidenceFormat = Require-String $evidence.format '^(json|trx|junit)$' 'EVIDENCE'
                if ((Require-String $evidence.runId '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' 'RUN') -cne $runId -or
                    -not [string]::Equals((Require-String $evidence.commitSha '^[0-9a-fA-F]{40}$' 'COMMIT'), $commit, [StringComparison]::OrdinalIgnoreCase) -or
                    (Require-String $evidence.environmentId '^[a-z][a-z0-9-]+$' 'ENV') -cne $caseEnvironmentId) { Reject 'EVIDENCE' }
                $evidenceFile = Verify-HashEntry $evidence $RepositoryRoot 'EVIDENCE' 33554432 $generatedAt $verificationNow
                $evidencePath = $evidenceFile.Canonical
                if ($seenEvidence.ContainsKey($evidencePath)) { Reject 'DUPLICATE' }
                $evidenceHash = (Require-String $evidence.sha256 '^[0-9a-fA-F]{64}$' 'HASH').ToLowerInvariant()
                $seenEvidence[$evidencePath] = $true
                if ($seenFileIdentities.ContainsKey($evidenceFile.Identity)) { Reject 'DUPLICATE' }
                $seenFileIdentities[$evidenceFile.Identity] = $true
                if ($seenEvidenceHashes.ContainsKey($evidenceHash) -or $seenContentHashes.ContainsKey($evidenceHash)) { Reject 'DUPLICATE' }
                $seenEvidenceHashes[$evidenceHash] = $true
                $seenContentHashes[$evidenceHash] = $true
                Verify-EvidenceFile $evidenceFile.Bytes $evidenceFormat $caseId ([long]$case.executions) ([long]$case.skipped) ([long]$case.notRun) ([long]$case.failed) $runId $commit $caseEnvironmentId
            }
        }
    }
    if ($hasPendingCase) { Reject 'CASE' }
    foreach ($laneId in $requiredLanes) {
        if (-not $seenLanes.ContainsKey($laneId)) { Reject 'MISSING' }
        foreach ($caseId in $matrixLanes[$laneId]) { if (-not $seenCases.ContainsKey($caseId)) { Reject 'MISSING' } }
    }
    foreach ($artifactId in $artifactsById.Keys) { if (-not $usedArtifacts.ContainsKey($artifactId)) { Reject 'ARTIFACT' } }
    foreach ($productId in $productsById.Keys) { if (-not $usedProducts.ContainsKey($productId)) { Reject 'PRODUCT' } }

    Require-Shape $manifest.result 'result' @('releaseEvidence', 'missing', 'unavailable', 'skipped', 'notRun', 'failed') @('releaseEvidence', 'missing', 'unavailable', 'skipped', 'notRun', 'failed')
    if ((Require-Boolean $manifest.result.releaseEvidence 'RESULT') -ne $releaseEvidence) { Reject 'RESULT' }
    foreach ($countName in @('missing', 'unavailable', 'skipped', 'notRun', 'failed')) {
        if ((Require-Integer $manifest.result.$countName 0 'RESULT') -ne 0) { Reject 'RESULT' }
    }
    if ($Profile -eq 'Release' -and @($omittedLanes).Count -ne 0) { Reject 'MATRIX' }
    # Keep the release cleanliness gate mandatory, but perform it after the
    # manifest binding checks so malformed external evidence reports its
    # sanitized causal contract code rather than an ambient worktree detail.
    if ($Profile -eq 'Release') {
        $dirty = (& git -C $RepositoryRoot status --porcelain 2>$null)
        if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($dirty -join ''))) { Reject 'COMMIT' }
    }

    $output = [ordered]@{ valid = $true; profile = $Profile; matrix = 'sqlobserver-m12'; releaseEvidence = $releaseEvidence; lanes = $seenLanes.Count; cases = $seenCases.Count; evidenceFiles = $seenEvidence.Count }
    $output | ConvertTo-Json -Compress
    exit 0
}
catch {
    # Never echo exception text: it may disclose paths, command lines, or
    # provider diagnostics.  A short, allow-listed rejection code is safe for
    # automation and lets callers distinguish the causal contract failure.
    $code = 'REJECTED'
    if ($_.Exception.Message -match '^M12-EVIDENCE-([A-Z]+)$') { $code = $Matches[1] }
    [ordered]@{ valid = $false; error = "M12-EVIDENCE-$code" } | ConvertTo-Json -Compress
    exit 1
}
