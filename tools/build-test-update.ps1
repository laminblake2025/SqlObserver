#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string] $InstallerBuildDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = [IO.Path]::GetFullPath($InstallerBuildDirectory)
$receipt = Get-Content -LiteralPath (Join-Path $build 'build-receipt.json') -Raw | ConvertFrom-Json
if ($receipt.kind -cne 'unsigned-test-installer' -or !$receipt.automatedPolicyTests.Equals('passed')) { throw 'A verified test installer build is required.' }
$source = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $receipt.installerSourceCommit -cne $source -or @(& git -C $repo status --porcelain=v1 --untracked-files=all).Count -ne 0) { throw 'Build a patch from the clean committed installer source.' }
$stage = Join-Path $build 'staging'
& (Join-Path $repo 'tools/test-lab-package.ps1') -PackageDirectory (Join-Path $stage 'Application')
if (!$?) { throw 'Application verification failed.' }
& (Join-Path $stage 'Setup/SqlObserver.TestSetup.exe') --verify
if ($LASTEXITCODE -ne 0) { throw 'Helper verification failed.' }
$run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $repo "artifacts/test-patches/$run"
$package = Join-Path $output 'package'
$payload = Join-Path $package 'Payload'
[void][IO.Directory]::CreateDirectory($payload)
Copy-Item -LiteralPath (Join-Path $stage 'Application'), (Join-Path $stage 'Setup') -Destination $payload -Recurse
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path=[IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/'); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    Kind='sqlobserver-test-certificate-trust-patch'; SchemaVersion=1; SourceCommit=$source
    BaseApplicationManifestSha256='032e9681d20ed28e28b231bb3404e30226ae342501b528d3448f594fd49599f7'
    BaseSetupDllSha256='ea959a11dfa5e5c392ab4204b414ecdb84f730514d2590cc3ad23dd461a2006f'
    MigrationCatalogSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'Application/Docs/repository-migrations.sha256') -Algorithm SHA256).Hash.ToLowerInvariant()
    Target=[ordered]@{ TargetId='6140bc24-d03d-4b67-b42e-a2863d8a29c5'; Host='sdlc-qa-db03.pentechho.com'; TcpPort=1433 }
    Files=$files
}
$manifestPath = Join-Path $package 'patch-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$template = [IO.File]::ReadAllText((Join-Path $repo 'installer/test/Apply-SqlObserverTestPatch.ps1'))
if ([regex]::Matches($template, '__PATCH_MANIFEST_SHA256__').Count -ne 1) { throw 'Unexpected patch template.' }
[IO.File]::WriteAllText((Join-Path $package 'Apply-SqlObserverTestPatch.ps1'), $template.Replace('__PATCH_MANIFEST_SHA256__', $manifestHash), [Text.UTF8Encoding]::new($false))
$instructions = @'
SQL Observer test patch — SQL-QA-DB03 certificate trust

Extract this entire ZIP to a local directory. Close any SQL Observer setup console.
Open PowerShell 7 as Administrator, change to the extracted directory, and run:

  .\Apply-SqlObserverTestPatch.ps1

Defaults: C:\Program Files\SqlObserver Test and
C:\ProgramData\SqlObserverTestIdentityFix. Use -InstallRoot/-DataRoot only if
your existing installation uses different paths. -ValidateOnly checks preflight.
No setup wizard or migration is run. Do not uninstall the working installation.

The patch enables TrustServerCertificate only for target
6140bc24-d03d-4b67-b42e-a2863d8a29c5 at sdlc-qa-db03.pentechho.com:1433.
Encryption remains mandatory. Certificate identity validation is skipped for
this target. Other SQL targets, dashboard HTTPS, and PostgreSQL TLS are unchanged.

After success: open SQL-QA-DB03 target settings, Request rediscovery, then
refresh/retry health access. Discovery can still report SQL login/permission
failures independently of certificate trust.

Backups and a result receipt remain under the installed PatchBackups directory,
restricted to Administrators and SYSTEM. Keep them until the deployment passes.
On failure the patch attempts to restore original files/configuration/service
states and reports any rollback error. Do not share populated configuration
backups: they contain repository credentials. Database data and protected keys
are never modified by this patch.

This unsigned test patch has automated payload/configuration checks. Its native
service replacement/rollback lifecycle remains to be exercised on the test host.
'@
[IO.File]::WriteAllText((Join-Path $package 'INSTALL-PATCH.txt'), $instructions, [Text.UTF8Encoding]::new($false))
$archive = Join-Path $output "SqlObserver-TestPatch-$($source.Substring(0, 8)).zip"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'), "$sha  $([IO.Path]::GetFileName($archive))`n")
[IO.File]::WriteAllText((Join-Path $output 'build-receipt.json'), ([ordered]@{ Kind='sqlobserver-test-patch'; SourceCommit=$source; Archive=[IO.Path]::GetFileName($archive); Sha256=$sha; Files=$files.Count; ManifestSha256=$manifestHash; NativeLifecycle='pending' } | ConvertTo-Json))
Write-Host "Test patch: $archive"
Write-Host "SHA-256: $sha"
