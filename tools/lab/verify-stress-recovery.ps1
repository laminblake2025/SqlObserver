[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RunId,
    [string]$RepositoryCredentialPath='C:\SqlObserverLab\ProtectedStress\repository.dpapi.json'
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Stress.Common.psm1') -Force
Assert-StressHost
$settings=Get-StressSettings
$originalPath=Get-StressRunPath $RunId
$script:manifest=Read-StressJson (Join-Path $originalPath 'manifest.json')
if($script:manifest.Status -eq 'running'){throw 'Recovery verification requires a completed controller run.'}
if((ConvertTo-StressUtc $script:manifest.StartedUtc) -lt [DateTime]::UtcNow.AddHours(-24)){throw 'Live historical evidence has expired; repeat the workload instead.'}
$cleanup=Read-StressJson (Join-Path $originalPath 'cleanup-result.json')
if(-not $cleanup.Passed){throw 'Complete run cleanup before verifying recovery.'}
foreach($worker in $script:manifest.Workers){
    if(Test-StressProcessIdentity $worker (Get-Process -Id $worker.Id -ErrorAction SilentlyContinue)){throw 'An owned worker is still running.'}
}
# Preserve the original manifest and report, including the original failure.
# Every invocation receives a separate evidence directory and makes no admin writes.
$script:runPath=Join-Path $originalPath ('recovery-verification-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
[void][IO.Directory]::CreateDirectory($script:runPath)
Copy-Item -LiteralPath (Join-Path $originalPath 'guardrails.jsonl') -Destination (Join-Path $script:runPath 'guardrails.jsonl')
$script:verificationResults=@()
function Repository([string]$Query){Invoke-StressRepository $Query $RepositoryCredentialPath}
function Add-Result([string]$Name,[string]$Status,$Evidence){
    $script:verificationResults+=@{Name=$Name;Status=$Status;Evidence=$Evidence;Utc=[DateTime]::UtcNow.ToString('o')}
}
. (Join-Path $PSScriptRoot 'Stress.Scenarios.ps1')
$failure=$null
try { Confirm-StressRecovery }
catch { $failure=$_.Exception.Message; Add-Result recovery-verification failed @{Reason=$failure} }
$status=if($failure){'failed'}elseif(@($script:verificationResults|Where-Object Status -ne 'passed').Count){'inconclusive'}else{'passed'}
Write-StressJson (Join-Path $script:runPath 'results.json') @{
    RunId=$RunId;OriginalStatus=$script:manifest.Status;Status=$status
    VerifiedUtc=[DateTime]::UtcNow.ToString('o');Results=$script:verificationResults
}
Write-Output "Recovery verification: $status. Evidence: $script:runPath"
if($failure){throw $failure}
