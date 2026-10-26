[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.5') { throw 'Use PowerShell 7.5 or newer.' }
if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw 'Build the lab package on a Windows x64 host.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$originalLocation = Get-Location
Set-Location -LiteralPath $repositoryRoot
function Invoke-BuildCommand([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Lab package build step failed: $Executable" }
}
function Copy-PayloadFile([string] $Source, [string] $RelativeDestination) {
    $sourcePath = Join-Path $repositoryRoot $Source
    $destination = Join-Path $packageRoot $RelativeDestination
    if ((Get-Item -LiteralPath $sourcePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload source reparse point rejected.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($sourcePath, $destination, $false)
}
try {
    $sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'A committed Git source is required.' }
    $sourceTree = (& git rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceTree -cnotmatch '^[0-9a-f]{40}$') { throw 'Git source tree is unavailable.' }
    if (@(& git status --porcelain=v1 --untracked-files=all).Count -ne 0) { throw 'Commit source changes before building a versioned lab package.' }
    $sdk = (Get-Content -LiteralPath global.json -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -cne $sdk) { throw 'The lab build requires the pinned .NET SDK.' }
    $webPolicy = Get-Content -LiteralPath web/package.json -Raw | ConvertFrom-Json
    if ($webPolicy.packageManager -cnotmatch '^pnpm@([0-9]+\.[0-9]+\.[0-9]+)$') { throw 'The lab build requires an exact pnpm version policy.' }
    $expectedPnpm = $Matches[1]
    $actualPnpm = (& pnpm --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualPnpm -cne $expectedPnpm) { throw 'The lab build requires the pinned pnpm version.' }
    $expectedNode = ([string]$webPolicy.engines.node).TrimStart('>', '=')
    $actualNode = (& node --version).Trim().TrimStart('v')
    if ($LASTEXITCODE -ne 0 -or $actualNode -cne $expectedNode) { throw 'The lab build requires the pinned Node.js version.' }
    $runName = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $outputRoot = Join-Path $repositoryRoot "artifacts/lab-packages/$($sourceCommit.Substring(0,8))/$runName"
    $packageRoot = Join-Path $outputRoot 'package'
    if (Test-Path -LiteralPath $outputRoot) { throw 'Lab package output must be a fresh directory.' }
    [void][IO.Directory]::CreateDirectory($packageRoot)
    Invoke-BuildCommand dotnet @('restore', 'SqlObserver.slnx', '--locked-mode', '--configfile', 'NuGet.Config')
    Invoke-BuildCommand pnpm @('--dir', 'web', 'install', '--frozen-lockfile')
    Invoke-BuildCommand pnpm @('--dir', 'web', 'run', 'build')
    $projects = [ordered]@{
        Server = 'src/SqlObserver.Server/SqlObserver.Server.csproj'
        Collector = 'src/SqlObserver.Collector/SqlObserver.Collector.csproj'
        MigrationHost = 'src/SqlObserver.Cli/SqlObserver.Cli.csproj'
    }
    foreach ($component in $projects.Keys) {
        Invoke-BuildCommand dotnet @('publish', $projects[$component], '--configuration', 'Release', '--no-restore',
            '--self-contained', 'false', '-p:UseAppHost=true', '-p:AppHostRuntimeIdentifier=win-x64',
            "-p:SourceRevisionId=$sourceCommit", '--output', (Join-Path $packageRoot $component))
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'web/dist') -Destination (Join-Path $packageRoot 'Web') -Recurse
    $copyFiles = [ordered]@{
        'docs/deployment/lab-test-package.md' = 'README.md'
        'docs/deployment/lab-single-host.md' = 'Docs/deployment/lab-single-host.md'
        'docs/deployment/sqlserver-version-compatibility.md' = 'Docs/deployment/sqlserver-version-compatibility.md'
        'docs/deployment/sqlserver-certificate-trust.md' = 'Docs/deployment/sqlserver-certificate-trust.md'
        'docs/architecture/support-matrix.md' = 'Docs/architecture/support-matrix.md'
        'docs/adr/ADR-0017-deployment-identities-secrets-and-transport.md' = 'Docs/adr/ADR-0017-deployment-identities-secrets-and-transport.md'
        'web/.artifacts/web-asset-manifest.v1.json' = 'Docs/web-asset-manifest.v1.json'
        'database/migrations/checksums.sha256' = 'Docs/repository-migrations.sha256'
        'release/lab/templates/server.example.json' = 'ConfigTemplates/server.example.json'
        'release/lab/templates/collector.example.json' = 'ConfigTemplates/collector.example.json'
        'tools/test-lab-package.ps1' = 'Tools/test-lab-package.ps1'
        'tools/generate-permissions.ps1' = 'Tools/generate-permissions.ps1'
        'tools/lab/provision-live-activity-key.ps1' = 'Tools/provision-live-activity-key.ps1'
    }
    foreach ($source in $copyFiles.Keys) { Copy-PayloadFile $source $copyFiles[$source] }
    $readmePath = Join-Path $packageRoot 'README.md'
    $readme = [IO.File]::ReadAllText($readmePath).Replace('(lab-single-host.md)', '(Docs/deployment/lab-single-host.md)').Replace('(sqlserver-version-compatibility.md)', '(Docs/deployment/sqlserver-version-compatibility.md)')
    [IO.File]::WriteAllText($readmePath, $readme, [Text.UTF8Encoding]::new($false))
    # apphost target verification does not execute either runtime service.
    foreach ($component in $projects.Keys) {
        $name = if ($component -eq 'MigrationHost') { 'SqlObserver.Cli' } else { "SqlObserver.$component" }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $packageRoot "$component/$name.exe"))
        if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw 'Published apphost is not a Windows executable.' }
        $offset = [BitConverter]::ToInt32($bytes, 60)
        if ($offset -lt 0 -or $offset + 6 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes, $offset + 4) -ne 0x8664) {
            throw 'Published apphost is not Windows x64.'
        }
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path=[IO.Path]::GetRelativePath($packageRoot,$_.FullName).Replace('\','/'); bytes=$_.Length;
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{ schemaVersion=1; kind='lab-test'; sourceCommit=$sourceCommit; sourceTree=$sourceTree;
        builtAtUtc=[DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'"); configuration='Release'; runtimeIdentifier='win-x64';
        selfContained=$false; productionSupported=$false; nativeQualification='pending'; files=$payloadFiles }
    $manifestPath = Join-Path $packageRoot 'package-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
    $checksumLines = @("$((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())  package-manifest.json") + @($payloadFiles | ForEach-Object { "$($_.sha256)  $($_.path)" })
    [IO.File]::WriteAllText((Join-Path $packageRoot 'checksums.sha256'), (($checksumLines | Sort-Object) -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'test-lab-package.ps1') -PackageDirectory $packageRoot
    Invoke-BuildCommand (Join-Path $packageRoot 'MigrationHost/SqlObserver.Cli.exe') @('--help')
    if ($sourceCommit -cne (& git rev-parse HEAD).Trim() -or @(& git status --porcelain=v1 --untracked-files=all).Count -ne 0) {
        throw 'Source changed during package build; refusing publication.'
    }
    $archivePath = Join-Path $outputRoot "SqlObserver-lab-win-x64-$($sourceCommit.Substring(0,8)).zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot 'test-lab-package.ps1') -ArchivePath $archivePath -ExpectedArchiveSha256 $archiveSha256
    [IO.File]::WriteAllText("$archivePath.sha256", "$archiveSha256  $([IO.Path]::GetFileName($archivePath))`n", [Text.UTF8Encoding]::new($false))
    $receipt = [ordered]@{ kind='lab-package-build'; status='verified'; sourceCommit=$sourceCommit; sourceTree=$sourceTree;
        archive=[IO.Path]::GetFileName($archivePath); sha256=$archiveSha256; bytes=(Get-Item -LiteralPath $archivePath).Length;
        payloadFiles=$payloadFiles.Count; runtimeIdentifier='win-x64'; selfContained=$false; dotnetSdk=$actualSdk;
        nativeQualification='pending'; installed=$false; productionSupported=$false; completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    [IO.File]::WriteAllText((Join-Path $outputRoot 'build-receipt.json'), ($receipt | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Verified lab package: $archivePath"
    Write-Host "SHA-256: $archiveSha256"
}
finally { Set-Location -LiteralPath $originalLocation.Path }
