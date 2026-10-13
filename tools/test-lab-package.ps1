[CmdletBinding(DefaultParameterSetName = 'Directory')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Directory')][string] $PackageDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Archive')][string] $ArchivePath,
    [Parameter(Mandatory, ParameterSetName = 'Archive')][ValidatePattern('^[0-9a-fA-F]{64}$')][string] $ExpectedArchiveSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.5') { throw 'Package verification requires PowerShell 7.5 or newer.' }
$maximumFiles = 2048
$maximumFileBytes = 64MB
$maximumTotalBytes = 512MB
$manifestName = 'package-manifest.json'
$checksumName = 'checksums.sha256'
$files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$archive = $null
$archiveStream = $null
$readingArchive = $PSCmdlet.ParameterSetName -eq 'Archive'
function Assert-PackagePath([string] $Name) {
    if ($Name.Length -gt 240 -or $Name -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or
        @($Name.Split('/') | Where-Object { $_ -in @('.', '..') -or $_.EndsWith('.') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -ne 0) {
        throw 'Unsafe package member path.'
    }
    if ($Name -match '(?i)(^|/)(\.env(?:\..*)?|appsettings(?:\..*)?\.local\.json)$|\.(dpapi|pfx|p12|pem|key|user)$') {
        throw 'Personal configuration or secret material is not permitted in the package.'
    }
}
function Assert-OrdinaryPath([string] $Path) {
    $cursor = Get-Item -LiteralPath $Path -Force
    while ($null -ne $cursor) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package reparse points are not permitted.' }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
}
function Open-PackageMember([string] $Name) {
    if (!$files.ContainsKey($Name)) { throw "Package member is missing: $Name" }
    $item = $files[$Name]
    if ($readingArchive) { return $item.entry.Open() }
    return [IO.File]::Open($item.path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
}
function Get-MemberDigest([string] $Name) {
    $stream = Open-PackageMember $Name
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new(65536)
        $length = 0L
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $length += $read
            if ($length -gt $maximumFileBytes -or $length -gt $files[$Name].bytes) { throw 'Package member exceeds its declared bound.' }
            $hash.AppendData($buffer, 0, $read)
        }
        if ($length -ne $files[$Name].bytes) { throw 'Package member length changed.' }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Read-MemberText([string] $Name) {
    if ($files[$Name].bytes -gt 1MB) { throw 'Package metadata is too large.' }
    $stream = Open-PackageMember $Name
    $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $false)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Assert-Keys($Object, [string[]] $Expected, [string] $Label) {
    $actualKeys = @($Object.PSObject.Properties.Name | Sort-Object) -join '|'
    $expectedKeys = @($Expected | Sort-Object) -join '|'
    if ($null -eq $Object -or $actualKeys -cne $expectedKeys) {
        throw "Package $Label shape is invalid."
    }
}
try {
    if ($readingArchive) {
        $fullArchivePath = [IO.Path]::GetFullPath($ArchivePath)
        Assert-OrdinaryPath $fullArchivePath
        if ((Get-Item -LiteralPath $fullArchivePath).Length -gt $maximumTotalBytes) { throw 'Package archive is too large.' }
        $archiveStream = [IO.File]::Open($fullArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $actualArchiveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archiveStream)).ToLowerInvariant()
        if ($actualArchiveHash -cne $ExpectedArchiveSha256.ToLowerInvariant()) { throw 'Package archive checksum mismatch.' }
        $archiveStream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { throw 'Package archive must contain only file entries.' }
            Assert-PackagePath $entry.FullName
            if (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) { throw 'Archive symbolic links are not permitted.' }
            if (!$files.TryAdd($entry.FullName, @{ entry = $entry; bytes = $entry.Length })) { throw 'Duplicate package member.' }
        }
    }
    else {
        $root = [IO.Path]::GetFullPath($PackageDirectory)
        Assert-OrdinaryPath $root
        foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package reparse points are not permitted.' }
            if ($item.PSIsContainer) { continue }
            $name = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
            Assert-PackagePath $name
            if (!$files.TryAdd($name, @{ path = $item.FullName; bytes = $item.Length })) { throw 'Duplicate package member.' }
        }
    }
    if ($files.Count -lt 3 -or $files.Count -gt $maximumFiles) { throw 'Package file count is invalid.' }
    $totalBytes = 0L
    foreach ($item in $files.Values) {
        if ($item.bytes -le 0 -or $item.bytes -gt $maximumFileBytes) { throw 'Package member size is invalid.' }
        $totalBytes += $item.bytes
    }
    if ($totalBytes -gt $maximumTotalBytes) { throw 'Package payload is too large.' }
    foreach ($metadata in @($manifestName, $checksumName)) { if (!$files.ContainsKey($metadata)) { throw 'Package metadata is missing.' } }
    $manifestText = Read-MemberText $manifestName
    $manifest = $manifestText | ConvertFrom-Json -Depth 12 -DateKind String
    Assert-Keys $manifest @('schemaVersion','kind','sourceCommit','sourceTree','builtAtUtc','configuration','runtimeIdentifier','selfContained','productionSupported','nativeQualification','files') 'manifest'
    if ($manifest.schemaVersion -isnot [long] -and $manifest.schemaVersion -isnot [int] -or $manifest.schemaVersion -ne 1 -or
        $manifest.kind -cne 'lab-test' -or $manifest.configuration -cne 'Release' -or
        $manifest.runtimeIdentifier -cne 'win-x64' -or $manifest.selfContained -isnot [bool] -or $manifest.selfContained -ne $false -or
        $manifest.productionSupported -isnot [bool] -or $manifest.productionSupported -ne $false -or $manifest.nativeQualification -cne 'pending' -or
        $manifest.sourceCommit -isnot [string] -or $manifest.sourceTree -isnot [string] -or
        $manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $manifest.sourceTree -cnotmatch '^[0-9a-f]{40}$' -or
        $manifest.builtAtUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$') { throw 'Package is not the bounded lab build contract.' }
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $checksumLines = [Collections.Generic.List[string]]::new()
    $checksumLines.Add("$(Get-MemberDigest $manifestName)  $manifestName")
    foreach ($member in $manifest.files) {
        Assert-Keys $member @('path','bytes','sha256') 'file entry'
        Assert-PackagePath $member.path
        if ($member.bytes -isnot [long] -and $member.bytes -isnot [int] -or $member.bytes -le 0 -or
            $member.path -in @($manifestName, $checksumName) -or !$listed.Add($member.path) -or
            $member.sha256 -cnotmatch '^[0-9a-f]{64}$' -or !$files.ContainsKey($member.path) -or
            $member.bytes -ne $files[$member.path].bytes -or (Get-MemberDigest $member.path) -cne $member.sha256) {
            throw 'Package payload inventory or checksum mismatch.'
        }
        $checksumLines.Add("$($member.sha256)  $($member.path)")
    }
    if ($listed.Count -ne $files.Count - 2) { throw 'Package contains unlisted files.' }
    if ((Read-MemberText $checksumName) -cne (($checksumLines | Sort-Object) -join "`n") + "`n") { throw 'Package checksum manifest mismatch.' }
    $required = @('README.md','Server/SqlObserver.Server.exe','Server/SqlObserver.Server.dll','Server/SqlObserver.Server.runtimeconfig.json',
        'Collector/SqlObserver.Collector.exe','Collector/SqlObserver.Collector.dll','Collector/SqlObserver.Collector.runtimeconfig.json',
        'MigrationHost/SqlObserver.Cli.exe','MigrationHost/SqlObserver.Cli.dll','MigrationHost/SqlObserver.Cli.runtimeconfig.json',
        'Web/index.html','Docs/web-asset-manifest.v1.json','ConfigTemplates/server.example.json','ConfigTemplates/collector.example.json',
        'Tools/test-lab-package.ps1','Tools/generate-permissions.ps1','Tools/provision-live-activity-key.ps1')
    foreach ($name in $required) { if (!$listed.Contains($name)) { throw "Required package member is missing: $name" } }
    foreach ($name in @('Server/SqlObserver.Server.exe','Collector/SqlObserver.Collector.exe','MigrationHost/SqlObserver.Cli.exe')) {
        $stream = Open-PackageMember $name
        $reader = [IO.BinaryReader]::new($stream)
        try {
            $header = $reader.ReadBytes(64)
            if ($header.Length -ne 64 -or [BitConverter]::ToUInt16($header,0) -ne 0x5A4D) { throw 'Package apphost is not Windows x64.' }
            $offset = [BitConverter]::ToInt32($header,60)
            if ($offset -lt 0 -or $offset + 6 -gt $files[$name].bytes) { throw 'Package apphost header is invalid.' }
            # ZIP streams cannot seek; reopen and consume the bounded prefix below.
        }
        finally { $reader.Dispose() }
        $stream = Open-PackageMember $name
        $reader = [IO.BinaryReader]::new($stream)
        try {
            [void]$reader.ReadBytes($offset)
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Package apphost is not Windows x64.' }
        }
        finally { $reader.Dispose() }
    }
    foreach ($name in @('Server/SqlObserver.Server.runtimeconfig.json','Collector/SqlObserver.Collector.runtimeconfig.json','MigrationHost/SqlObserver.Cli.runtimeconfig.json')) {
        $runtime = (Read-MemberText $name | ConvertFrom-Json -Depth 10).runtimeOptions
        if ($runtime.tfm -cne 'net10.0' -or $null -ne $runtime.PSObject.Properties['includedFrameworks']) { throw 'Package runtime configuration is invalid.' }
        $frameworks = if ($null -ne $runtime.PSObject.Properties['frameworks']) { @($runtime.frameworks) } elseif ($null -ne $runtime.PSObject.Properties['framework']) { @($runtime.framework) } else { @() }
        $expectedFrameworks = if ($name.StartsWith('Server/', [StringComparison]::Ordinal)) { @('Microsoft.NETCore.App','Microsoft.AspNetCore.App') } else { @('Microsoft.NETCore.App') }
        if ((@($frameworks.name | Sort-Object) -join '|') -cne (@($expectedFrameworks | Sort-Object) -join '|') -or @($frameworks | Where-Object { $_.version -notmatch '^10\.0\.\d+$' }).Count -gt 0) {
            throw 'Package framework prerequisites are invalid.'
        }
    }
    [pscustomobject]@{ status='verified'; kind='lab-test'; sourceCommit=$manifest.sourceCommit; fileCount=$files.Count; bytes=$totalBytes; productionSupported=$false } | ConvertTo-Json
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
}
