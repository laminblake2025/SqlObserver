[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Stress.Common.psm1') -Force
$count=0
function Check([bool]$Condition,[string]$Name){if(-not $Condition){throw "FAILED: $Name"};$script:count++}
foreach($file in @('Stress.Common.psm1','Stress.Scenarios.ps1','run-stress-test.ps1','stress-worker.ps1','stress-watchdog.ps1','provision-stress-credential.ps1','verify-stress-recovery.ps1')){
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) "Syntax $file"
}
if($env:COMPUTERNAME -ne 'WIN-QNGOV5GDM24'){
    $rejected=$false;try{Assert-StressHost}catch{$rejected=$true};Check $rejected 'Wrong host rejected'
}
$sample=@{AvailableMemoryBytes=4GB;TotalMemoryBytes=8GB;Disks=@(@{FreeBytes=20GB;TotalBytes=64GB})}
Check ($null -eq (Get-StressGuardDecision $sample 0 0)) 'Healthy sample admitted'
$sample.TotalMemoryBytes=16GB
Check ($null -eq (Get-StressGuardDecision $sample 0 0)) 'Sixteen GiB threshold avoids Int32 overload overflow'
$sample.AvailableMemoryBytes=[long][Math]::Ceiling(16GB*.15)
Check ($null -eq (Get-StressGuardDecision $sample 0 0)) 'Rounded-up fifteen-percent threshold admitted'
$sample.AvailableMemoryBytes--
Check ((Get-StressGuardDecision $sample 0 0) -eq 'memory_limit') 'One byte below sixteen GiB threshold rejected'
$sample.TotalMemoryBytes=8GB
$sample.AvailableMemoryBytes=1GB
Check ((Get-StressGuardDecision $sample 0 0) -eq 'memory_limit') 'Percentage memory bound'
$sample.TotalMemoryBytes=2GB;$sample.AvailableMemoryBytes=900MB
Check ((Get-StressGuardDecision $sample 0 0) -eq 'memory_limit') 'Absolute memory bound'
$sample.AvailableMemoryBytes=4GB;$sample.TotalMemoryBytes=8GB;$sample.Disks[0].FreeBytes=9GB
Check ((Get-StressGuardDecision $sample 0 0) -eq 'disk_limit') 'Percentage disk bound'
$sample.Disks[0].TotalBytes=20GB;$sample.Disks[0].FreeBytes=4GB
Check ((Get-StressGuardDecision $sample 0 0) -eq 'disk_limit') 'Absolute disk bound'
$sample.Disks[0].FreeBytes=10GB
Check ($null -eq (Get-StressGuardDecision $sample 5 2)) 'Brief CPU/probe spike tolerated'
Check ((Get-StressGuardDecision $sample 6 0) -eq 'sustained_cpu_limit') 'Sustained CPU stops load'
Check ((Get-StressGuardDecision $sample 0 3) -eq 'observer_probe_limit') 'Three bad probes stop load'
$actual=[pscustomobject]@{Id=42;StartTime=[DateTime]::UtcNow;Path='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'}
$expected=@{Id=42;StartedUtc=$actual.StartTime.ToUniversalTime().ToString('o');Path=$actual.Path}
Check (Test-StressProcessIdentity $expected $actual) 'Owned process matches'
$expected.StartedUtc=$actual.StartTime.AddSeconds(-1).ToUniversalTime().ToString('o')
Check (-not(Test-StressProcessIdentity $expected $actual)) 'Reused PID rejected'
$expected.StartedUtc=$actual.StartTime.ToUniversalTime().ToString('o');$expected.Path='C:\other.exe'
Check (-not(Test-StressProcessIdentity $expected $actual)) 'Different executable rejected'
Check (-not(Test-StressProcessIdentity $expected $null)) 'Exited process safe'
foreach($invalid in @('..\outside','../outside','bad','00000000000000000000000000000000\child')){
    $rejected=$false;try{Get-StressRunPath $invalid|Out-Null}catch{$rejected=$true};Check $rejected 'Run path traversal rejected'
}
Check ((Get-StressPercentile @(1..100)) -eq 95) 'Nearest rank P95'
Check ($null -eq (Get-StressPercentile @())) 'Empty percentile unavailable'
Check ((ConvertTo-StressUtc '2026-09-08T07:26:00-07:00').ToString('HH:mm') -eq '14:26') 'Offset repository timestamps normalize to UTC'
Check ((ConvertTo-StressUtc '2026-09-08T14:26:00Z').Kind -eq [DateTimeKind]::Utc) 'UTC deadlines retain UTC kind'
$now=[DateTime]::UtcNow
Check ($null -eq (Get-StressControlFailure $now $now.AddSeconds(1) $now $true $false)) 'Active controller admitted'
Check ((Get-StressControlFailure $now $now $now $true $false) -eq 'overall_deadline') 'Exact deadline stops work'
Check ((Get-StressControlFailure $now $now.AddMinutes(1) $now.AddSeconds(-46) $true $false) -eq 'controller_stalled') 'Stalled controller stops work'
Check ((Get-StressControlFailure $now $now.AddMinutes(1) $now $false $false) -eq 'controller_exited') 'Controller crash stops work'
Check ((Get-StressControlFailure $now $now.AddMinutes(1) $now $true $true) -eq 'operator_stop') 'Operator stop is honored'
$child=$null
try {
    $executable=(Get-Process -Id $PID).Path
    $child=Start-Process $executable -ArgumentList @('-NoProfile','-Command','"Start-Sleep -Seconds 30"') -WindowStyle Hidden -PassThru
    $record=@{Id=$child.Id;StartedUtc=$child.StartTime.ToUniversalTime().ToString('o');Path=$child.Path}
    $wrong=@{Id=$record.Id;StartedUtc=$child.StartTime.AddSeconds(-1).ToUniversalTime().ToString('o');Path=$record.Path}
    Stop-StressOwnedProcess $wrong
    Check (-not $child.HasExited) 'Cancellation refuses a reused process identity'
    Stop-StressOwnedProcess $record
    Check ($child.WaitForExit(5000)) 'Cancellation terminates an owned worker'
    Stop-StressOwnedProcess $record
    Check $true 'Repeated cancellation is harmless'
} finally {if($child){if(-not $child.HasExited){$child.Kill()};$child.Dispose()}}
# Atomic journal replacement round-trip and exclusive run lock are filesystem behavior,
# not assertions that merely repeat the script text.
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('stress-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $path=Join-Path $temporary 'manifest.json'
    Write-StressJson $path @{RunId='first';Workers=@()};Write-StressJson $path @{RunId='second';Workers=@(1,2)}
    $read=Read-StressJson $path;Check ($read.RunId -eq 'second' -and $read.Workers.Count -eq 2) 'Atomic manifest replacement'
    $lock=[IO.File]::Open((Join-Path $temporary 'lock'),'OpenOrCreate','ReadWrite','None')
    try{$rejected=$false;try{$other=[IO.File]::Open((Join-Path $temporary 'lock'),'OpenOrCreate','ReadWrite','None');$other.Dispose()}catch{$rejected=$true};Check $rejected 'Concurrent controller lock rejected'}finally{$lock.Dispose()}

    # Exercise the real recovery function against bounded fake adapters. No lab
    # credentials or SQL endpoint are available to these adapters.
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'run-stress-test.ps1'),[ref]$tokens,[ref]$errors)
    $recordFunction=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Process-Record'},$true)
    . ([scriptblock]::Create($recordFunction.Extent.Text))
    $script:identityLookups=0;$script:identityStart=[DateTime]::UtcNow
    function Get-Process { param($Id,$ErrorAction)
        $script:identityLookups++
        [pscustomobject]@{Id=$Id;StartTime=$script:identityStart;Path=if($script:identityLookups -ge 3){'C:\owned.exe'}else{$null}}
    }
    try {
        $record=Process-Record ([pscustomobject]@{Id=42;StartTime=$script:identityStart})
        Check ($record.Path -eq 'C:\owned.exe' -and $script:identityLookups -eq 3) 'Startup identity waits for the executable path'
    } finally { Remove-Item Function:\Get-Process }
    $scenarioAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Stress.Scenarios.ps1'),[ref]$tokens,[ref]$errors)
    foreach($functionName in @('Assert-Evidence','Confirm-QueryCollectionCoverage')) {
        $definition=$scenarioAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName},$true)
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $choices=@(@{id=5;name='SqlObserverLabSales'},@{id=6;name='SqlObserverLabWarehouse'})
    Confirm-QueryCollectionCoverage @(@{database_id=5;observations=12},@{database_id=6;observations=8}) $choices
    Check $true 'Collection coverage does not require both databases on a bounded ranking page'
    $missingRejected=$false
    try { Confirm-QueryCollectionCoverage @(@{database_id=5;observations=12}) $choices } catch { $missingRejected=$true }
    Check $missingRejected 'Missing query collection evidence is rejected'
    $scheduleAssignment=$scenarioAst.Find({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$relevant'},$true)
    $script:manifest=@{Preflight=@{Schedules=@(
        [pscustomobject]@{collector_id='engine.core';enabled=$true;seconds=30},
        [pscustomobject]@{collector_id='queries.performance';enabled=$true;seconds=300},
        [pscustomobject]@{collector_id='backups.status';enabled=$true;seconds=900},
        [pscustomobject]@{collector_id='activity.requests';enabled=$false;seconds=600})}}
    . ([scriptblock]::Create($scheduleAssignment.Extent.Text))
    Check ($relevant.Count -eq 2 -and ($relevant|Measure-Object seconds -Maximum).Maximum -eq 300) 'Ramp duration includes query performance but excludes unrelated/disabled schedules'
    $cleanupFunction=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Cleanup-Run'},$true)
    . ([scriptblock]::Create($cleanupFunction.Extent.Text))
    $script:runPath=$temporary
    $settings=Get-StressSettings
    $script:fakeEnabled=$true;$script:fakeCancelled=$false;$script:adminCalls=0;$script:stopCalls=0;$script:failAdmin=$true
    function Repository([string]$Query){
        if($Query -match 'maintenance_window'){return [pscustomobject]@{revision=3;cancelled_at=if($script:fakeCancelled){'done'}else{$null}}}
        return [pscustomobject]@{revision=7;enabled=$script:fakeEnabled}
    }
    function Invoke-StressApi([string]$Path,[string]$Method,$Body){
        if($script:failAdmin){throw 'Simulated transient administration failure'}
        $script:adminCalls++
        if($Path -like '*/cancel'){$script:fakeCancelled=$true}else{$script:fakeEnabled=$false}
    }
    function Stop-StressOwnedProcess($Expected){$script:stopCalls++}
    function Invoke-StressSql([string]$Query){return [pscustomobject]@{Rows=@([pscustomobject]@{n=0})}}
    $ownedRule=[guid]::NewGuid().ToString('D');$ownedDestination=[guid]::NewGuid().ToString('D');$ownedWindow=[guid]::NewGuid().ToString('D')
    Write-StressJson $path @{RunId='00000000000000000000000000000001';RepositoryCredentialPath='unused';Workers=@(@{Id=42});Rule=@{ruleId=$ownedRule;enabled=$true};Destination=@{destinationId=$ownedDestination};Maintenance=@{windowId=$ownedWindow}}
    Cleanup-Run
    Check (-not(Read-StressJson (Join-Path $temporary 'cleanup-result.json')).Passed) 'Cleanup failure is retained for retry'
    $script:failAdmin=$false
    Cleanup-Run
    Check ((Read-StressJson (Join-Path $temporary 'cleanup-result.json')).Passed) 'Cleanup retry succeeds'
    $calls=$script:adminCalls
    Cleanup-Run
    Check ($script:adminCalls -eq $calls) 'Successful cleanup retry makes no repeated mutations'
    Check ($script:stopCalls -eq 3) 'Each recovery pass checks owned worker processes'
    Write-StressJson $path @{RunId='00000000000000000000000000000001';Status='inconclusive';StartedUtc='2026-09-08T12:00:00Z';CompletedUtc='2026-09-08T12:01:00Z';Workers=@(@{Number=1;Scenario='Ramp'});Results=@(@{Name='baseline-drift';Status='inconclusive';Evidence=@{Reason='Baseline drift'}})}
    $guard=@(
        @{Utc='2026-09-08T12:00:01Z';SnapshotId='a';LiveAgeSeconds=1;ProbeMs=10;ProbeOk=$true;CpuPercent=20;AvailableMemoryBytes=2GB},
        @{Utc='2026-09-08T12:00:16Z';SnapshotId='b';LiveAgeSeconds=6;ProbeMs=100;ProbeOk=$false;CpuPercent=30;AvailableMemoryBytes=1GB})
    [IO.File]::WriteAllLines((Join-Path $temporary 'guardrails.jsonl'),@($guard|ForEach-Object{$_|ConvertTo-Json -Compress}))
    Write-StressReport $temporary
    $report=Read-StressJson (Join-Path $temporary 'results.json')
    Check ($report.P95LiveMs -eq 100 -and $report.FailedProbes -eq 1 -and $report.MaximumObservedCollectionGapSeconds -eq 10) 'Report derives latency, failures and collection gaps from observations'
    Check ($report.Status -eq 'inconclusive' -and (Get-Content (Join-Path $temporary 'report.md') -Raw).Contains('baseline-drift')) 'Report preserves inconclusive evidence instead of claiming a pass'
    Check ($report.MissingFinalWorkerRecords -eq 1 -and $report.Workers[0].Status -eq 'no_final_worker_record' -and $null -eq $report.Workers[0].Batches) 'Report retains cancelled workers without inventing completed work'
} finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "Passed $count stress harness checks. No SQL workload or lab mutation was performed."
