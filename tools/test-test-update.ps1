#requires -Version 7.5
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$template = Join-Path $repo 'installer/test/Apply-SqlObserverTestPatch.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($template, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Patch template has PowerShell parse errors.' }
$functions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
foreach ($name in @('Set-TargetTrust', 'Assert-Child', 'Assert-Tree', 'Move-InstallDirectory', 'Write-Configuration')) {
    $definition = @($functions | Where-Object Name -eq $name)
    if ($definition.Count -ne 1) { throw 'Expected patch function is missing.' }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
function Assert([bool] $Condition, [string] $Failure) { if (!$Condition) { throw $Failure } }
$target = @{ TargetId='6140bc24-d03d-4b67-b42e-a2863d8a29c5'; Host='sdlc-qa-db03.pentechho.com'; TcpPort=1433 }
$other = @{ TargetId='11111111-1111-1111-1111-111111111111'; Host='other.example.test'; TcpPort=1444 }
foreach ($initial in @(@{SqlObserver=@{}}, @{SqlObserver=@{SqlServerCertificateTrustOverrides=@()}})) {
    $updated = Set-TargetTrust $initial $target
    Assert ($updated.SqlObserver.SqlServerCertificateTrustOverrides.Count -eq 1) 'An absent or empty trust array was not configured.'
}
$configuration = '{"ConnectionStrings":{"SqlObserverRepository":"synthetic-secret"},"SqlObserver":{"IdentityFingerprintKey":"preserve-key","SqlServerCertificateTrustOverrides":[]},"Other":{"Flag":true}}' | ConvertFrom-Json -AsHashtable
$configuration.SqlObserver.SqlServerCertificateTrustOverrides = @($other)
$result = Set-TargetTrust $configuration $target
Assert ($result.ConnectionStrings.SqlObserverRepository -ceq 'synthetic-secret') 'Repository credential changed.'
Assert ($result.SqlObserver.IdentityFingerprintKey -ceq 'preserve-key' -and $result.Other.Flag) 'Unrelated settings changed.'
Assert ($result.SqlObserver.SqlServerCertificateTrustOverrides.Count -eq 2) 'Existing exception was lost.'
$result = Set-TargetTrust $result $target
Assert ($result.SqlObserver.SqlServerCertificateTrustOverrides.Count -eq 2) 'Repeated configuration duplicated the exception.'
Assert ($result.SqlObserver.SqlServerCertificateTrustOverrides[1].Host -ceq $target.Host) 'Target endpoint changed.'

$fixture = Join-Path $repo ('artifacts/test-installer/patch-tests-' + [Guid]::NewGuid().ToString('N'))
$InstallRoot = Join-Path $fixture 'Install'
$DataRoot = Join-Path $fixture 'Data'
$backup = Join-Path $InstallRoot 'Backups/fixture'
$stage = Join-Path $InstallRoot 'Stage'
foreach ($directory in @($backup, $stage, (Join-Path $DataRoot 'Server'), (Join-Path $DataRoot 'Collector'))) { [void][IO.Directory]::CreateDirectory($directory) }
$escaped = $false
try { $null = Assert-Child (Join-Path $InstallRoot '../Outside') $InstallRoot } catch { $escaped = $true }
Assert $escaped 'Path containment accepted an escaped target.'
$originals = @{}; $configurations = @{}
foreach ($component in @('Application', 'Setup')) {
    foreach ($root in @($InstallRoot, $stage)) { [void][IO.Directory]::CreateDirectory((Join-Path $root $component)) }
    [IO.File]::WriteAllText((Join-Path $InstallRoot "$component/marker.txt"), 'old')
    [IO.File]::WriteAllText((Join-Path $stage "$component/marker.txt"), 'new')
}
foreach ($component in @('Server', 'Collector')) {
    $path = Join-Path $DataRoot "$component/appsettings.json"
    [IO.File]::WriteAllText($path, '{"original":true}')
    $originals[$component] = [IO.File]::ReadAllBytes($path)
    $configurations[$component] = [Text.Encoding]::UTF8.GetBytes('{"updated":true}')
}
$moved = [Collections.Generic.List[string]]::new()
$configChanged = [Collections.Generic.List[string]]::new()
$states = @{ SqlObserverTestServer='Running'; SqlObserverTestCollector='Running' }
$manifest = @{ Files=@() }
$script:stopCalls = 0; $script:restoreCalls = 0
function Stop-TestServices { $script:stopCalls++ }
function Restore-ServiceStates($States) { $script:restoreCalls++ }
# Fail after both directory replacements and both configuration writes. This
# exercises the actual patch's catch/rollback block without touching SCM.
function Assert-Payload([string] $Root, $Files) { throw 'Injected verification failure.' }
$operation = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] -and $_.Extent.Text.Contains("'replace-application'") })
Assert ($operation.Count -eq 1) 'Patch application block was not found.'
$failed = $false
try { . ([scriptblock]::Create($operation[0].Extent.Text)) } catch { $failed = $true }
Assert $failed 'Injected application failure did not propagate.'
foreach ($component in @('Application', 'Setup')) {
    Assert ([IO.File]::ReadAllText((Join-Path $InstallRoot "$component/marker.txt")) -ceq 'old') 'Rollback did not restore old binaries.'
}
foreach ($component in @('Server', 'Collector')) {
    Assert ([IO.File]::ReadAllText((Join-Path $DataRoot "$component/appsettings.json")) -ceq '{"original":true}') 'Rollback did not restore configuration.'
}
$receipt = Get-Content -LiteralPath (Join-Path $backup 'patch-result.json') -Raw | ConvertFrom-Json
Assert ($receipt.Status -ceq 'failed' -and $receipt.RollbackErrors.Count -eq 0) 'Rollback reported a failure.'
Assert ($script:stopCalls -eq 2 -and $script:restoreCalls -eq 1) 'Rollback service ordering was not exercised.'
Write-Host 'Patch tests passed: scope/config preservation, idempotence, containment, atomic configuration replacement, and injected rollback.'
