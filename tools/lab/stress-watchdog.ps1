[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Stress.Common.psm1') -Force
Assert-StressHost
$path=Get-StressRunPath $RunId
$high=0; $bad=0; $reason=$null
try {
    while (-not (Test-Path (Join-Path $path 'finished'))) {
        $manifest=Read-StressJson (Join-Path $path 'manifest.json')
        $controller=Get-Process -Id $manifest.Controller.Id -ErrorAction SilentlyContinue
        $heartbeat=Get-Item (Join-Path $path 'heartbeat') -ErrorAction Stop
        $stopRequested=Test-Path (Join-Path $path 'stop')
        if ($stopRequested -and (Get-Content (Join-Path $path 'stop') -Raw) -eq 'run_complete') { break }
        $reason=Get-StressControlFailure ([DateTime]::UtcNow) (ConvertTo-StressUtc $manifest.DeadlineUtc) $heartbeat.LastWriteTimeUtc (Test-StressProcessIdentity $manifest.Controller $controller) $stopRequested
        if($reason){break}
        $sample=Get-StressSample
        $high=if($sample.CpuPercent -gt 90){$high+1}else{0}
        $bad=if(-not $sample.ProbeOk -or $sample.ProbeMs -gt 5000){$bad+1}else{0}
        [IO.File]::AppendAllText((Join-Path $path 'guardrails.jsonl'),(($sample|ConvertTo-Json -Depth 6 -Compress)+"`n"))
        $reason=Get-StressGuardDecision $sample $high $bad
        if ($reason) { break }
        Start-Sleep -Seconds 5
    }
} catch { $reason='watchdog_failure' }
if ($reason) {
    [IO.File]::WriteAllText((Join-Path $path 'stop'),$reason)
    Write-StressJson (Join-Path $path 'watchdog-result.json') @{ Reason=$reason; Utc=[DateTime]::UtcNow.ToString('o') }
    $manifest=Read-StressJson (Join-Path $path 'manifest.json')
    foreach($worker in $manifest.Workers) { Stop-StressOwnedProcess $worker }
    # The controller normally cleans up. An independently launched cleanup can also
    # recover a crashed controller; the cleanup lock serializes both callers.
    & (Join-Path $PSScriptRoot 'run-stress-test.ps1') -Operation Cleanup -RunId $RunId
}
