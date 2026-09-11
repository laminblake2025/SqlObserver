[CmdletBinding()]
param(
    [ValidateSet('Preflight','Run','Status','Stop','Cleanup')][string]$Operation='Preflight',
    [ValidateSet('Full','Functional','Alerts')][string]$Profile='Full',
    [string]$RunId,
    [string]$RepositoryCredentialPath='C:\SqlObserverLab\ProtectedStress\repository.dpapi.json'
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Stress.Common.psm1') -Force
Assert-StressHost
$settings=Get-StressSettings
$shell='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
function Repository([string]$Query) { Invoke-StressRepository $Query $RepositoryCredentialPath }
function Save-Manifest { Write-StressJson (Join-Path $script:runPath 'manifest.json') $script:manifest }
function Add-Result([string]$Name,[string]$Status,$Evidence) {
    $script:manifest.Results+=@{ Name=$Name; Status=$Status; Utc=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); Evidence=$Evidence }; Save-Manifest
}
function Assert-RunActive {
    [IO.File]::WriteAllText((Join-Path $script:runPath 'heartbeat'),[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'))
    if (Test-Path (Join-Path $script:runPath 'stop')) { throw 'Stress run stopped by operator or watchdog.' }
    if ([DateTime]::UtcNow -ge (ConvertTo-StressUtc $script:manifest.DeadlineUtc)) { throw 'Stress run exceeded overall deadline.' }
    if ($script:manifest.Watchdog) {
        $process=Get-Process -Id $script:manifest.Watchdog.Id -ErrorAction SilentlyContinue
        if (-not (Test-StressProcessIdentity $script:manifest.Watchdog $process)) { throw 'Independent watchdog is unavailable.' }
    }
}
function Wait-Condition([scriptblock]$Condition,[int]$Seconds,[string]$Failure) {
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Assert-RunActive
        $value=& $Condition
        if ($value) { return $value }
        Start-Sleep -Seconds 5
    } while([DateTime]::UtcNow -lt $until)
    throw "STRESS: $Failure"
}
function Hold([int]$Seconds) {
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    while([DateTime]::UtcNow -lt $until) { Assert-RunActive; Start-Sleep -Seconds 5 }
}
function Process-Record($Process) {
    $started=$Process.StartTime.ToUniversalTime().ToString('o')
    for($attempt=0;$attempt -lt 20;$attempt++){
        $current=Get-Process -Id $Process.Id -ErrorAction Stop
        if($current.StartTime.ToUniversalTime().ToString('o') -ne $started){throw 'Process identity changed during startup.'}
        if($current.Path){return @{ Id=$current.Id; StartedUtc=$started; Path=$current.Path }}
        Start-Sleep -Milliseconds 100
    }
    throw 'Process executable identity was unavailable after startup.'
}
function Start-Workers([string]$Scenario,[int]$Count,[int]$Seconds) {
    if ($Count -lt 1 -or $Count -gt 32) { throw 'Worker limit exceeded.' }
    $created=@()
    for($i=0;$i -lt $Count;$i++) {
        Assert-RunActive
        $number=$script:manifest.Workers.Count+1
        $database=if($Scenario -in @('Blocker','Waiter','DeadlockA','DeadlockB') -or $i%2 -eq 0){'SqlObserverLabSales'}else{'SqlObserverLabWarehouse'}
        $args=@('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'stress-worker.ps1')+'"'),'-RunId',$script:manifest.RunId,'-Scenario',$Scenario,'-Database',$database,'-Seconds',$Seconds,'-Worker',$number)
        $process=Start-Process $shell -ArgumentList $args -WindowStyle Hidden -PassThru
        $record=Process-Record $process; $record.Number=$number; $record.Scenario=$Scenario
        $script:manifest.Workers+=,$record; Save-Manifest; $created+=,$record
    }
    return ,$created
}
function Stop-Workers($Workers) { foreach($worker in $Workers) { Stop-StressOwnedProcess $worker } }
function Admin([string]$Path,$Body) {
    if (-not $Body.operationId) { $Body.operationId=[guid]::NewGuid().ToString('D') }
    # Persist identity/body before sending so uncertain outcomes remain reviewable.
    Write-StressJson (Join-Path $script:runPath ('admin-'+$Body.operationId+'.json')) @{ Path=$Path; Body=$Body }
    Invoke-StressApi $Path 'POST' $Body | Out-Null
}
function Preflight {
    $sample=Get-StressSample
    $failures=@()
    $guard=Get-StressGuardDecision $sample 0 0
    if($guard){$failures+=$guard}
    if(-not $sample.ProbeOk){$failures+='observer_unavailable'}
    if($null -eq $sample.LiveAgeSeconds -or $sample.LiveAgeSeconds -gt 30){$failures+='live_evidence_stale'}
    foreach($name in @('SqlObserverServer','SqlObserverCollector')) {
        if((Get-Service $name).Status -ne 'Running'){$failures+="service_unavailable:$name"}
    }
    $sql=Invoke-StressSql "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) host_name, (SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id=@@SPID) encrypted, (SELECT COUNT(*) FROM sys.databases WHERE name IN ('SqlObserverLabSales','SqlObserverLabWarehouse') AND state=0) databases;"
    if($sql.Rows[0].host_name -ne $settings.HostName -or $sql.Rows[0].encrypted -ne 'TRUE' -or $sql.Rows[0].databases -ne 2){$failures+='sql_fixture_or_tls'}
    $target=Invoke-StressApi ''
    $schedules=@(); $bytes=$null
    if(-not(Test-Path -LiteralPath $RepositoryCredentialPath)){$failures+='protected_repository_credential_missing'}
    else {
        $schedules=@(Repository "SELECT collector_id,extract(epoch FROM collection_interval)::int seconds,enabled,last_outcome,last_completed_at FROM control.collector_schedule WHERE instance_id='$($settings.Target)' ORDER BY collector_id")
        # Availability groups are unsupported on this single-instance lab by design.
        if(@($schedules|Where-Object {$_.enabled -and $_.collector_id -ne 'availability-groups.health' -and ($_.last_outcome -notin @('succeeded','partial') -or -not $_.last_completed_at -or ([DateTime]::UtcNow-(ConvertTo-StressUtc $_.last_completed_at)).TotalSeconds -gt 2*$_.seconds)}).Count){$failures+='collector_not_current'}
        $bytes=(Repository 'SELECT pg_database_size(current_database()) bytes')[0].bytes
        if(@(Repository "SELECT destination_id FROM alerting.destination WHERE instance_id='$($settings.Target)' AND enabled AND approved").Count){$failures+='existing_delivery_destination_enabled'}
        if(@(Repository "SELECT window_id FROM alerting.maintenance_window WHERE instance_id='$($settings.Target)' AND cancelled_at IS NULL AND ends_at>clock_timestamp()").Count){$failures+='existing_maintenance_window'}
    }
    @{ Passed=$failures.Count -eq 0; Failures=$failures; Sample=$sample; Schedules=$schedules; RepositoryBytes=$bytes; Target=$settings.Target; Utc=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ') }
}
function Cleanup-Run {
    $lockPath=Join-Path $script:runPath 'cleanup.lock'; $lock=$null
    try {
        try { $lock=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None') } catch { return }
        $m=Read-StressJson (Join-Path $script:runPath 'manifest.json')
        $RepositoryCredentialPath=$m.RepositoryCredentialPath
        $errors=@()
        foreach($worker in $m.Workers) { try { Stop-StressOwnedProcess $worker } catch { $errors+='worker_stop' } }
        # Read current revisions, but only mutate exact identities created by this run.
        foreach($kind in @('Rule','Destination','Maintenance')) {
            $identity=$m.$kind
            if(-not $identity){continue}
            try {
                switch($kind){
                    'Rule' {
                        $row=@(Repository "SELECT revision,enabled FROM alerting.rule WHERE instance_id='$($settings.Target)' AND rule_id='$($identity.ruleId)'")
                        if($row.Count -and $row[0].enabled){$body=@{}; $identity.psobject.Properties|ForEach-Object{$body[$_.Name]=$_.Value}; $body.expectedRevision=$row[0].revision; $body.operationId=[guid]::NewGuid().ToString('D'); $body.enabled=$false; Invoke-StressApi "/alerts/rules/$($identity.ruleId)/disable" POST $body|Out-Null}
                    }
                    'Destination' {
                        $row=@(Repository "SELECT revision,enabled FROM alerting.destination WHERE instance_id='$($settings.Target)' AND destination_id='$($identity.destinationId)'")
                        if($row.Count -and $row[0].enabled){$body=@{destinationId=$identity.destinationId;kind='windows-event-log';configurationReference='SqlObserver Alerts';enabled=$false;expectedRevision=$row[0].revision;operationId=[guid]::NewGuid().ToString('D')}; Invoke-StressApi "/alerts/destinations/$($identity.destinationId)/disable" POST $body|Out-Null}
                    }
                    'Maintenance' {
                        $row=@(Repository "SELECT revision,cancelled_at FROM alerting.maintenance_window WHERE instance_id='$($settings.Target)' AND window_id='$($identity.windowId)'")
                        if($row.Count -and -not $row[0].cancelled_at){Invoke-StressApi "/alerts/maintenance/$($identity.windowId)/cancel" POST @{expectedRevision=$row[0].revision;operationId=[guid]::NewGuid().ToString('D')}|Out-Null}
                    }
                }
            } catch { $errors+="cleanup_$kind" }
        }
        $until=[DateTime]::UtcNow.AddSeconds(35)
        do {
            $remaining=Invoke-StressSql "SELECT COUNT(*) n FROM sys.dm_exec_sessions WHERE program_name LIKE 'SqlObserver.Stress.$($m.RunId).%'"
            if($remaining.Rows[0].n -eq 0){break}
            Start-Sleep -Seconds 1
        } while([DateTime]::UtcNow -lt $until)
        if($remaining.Rows[0].n -ne 0){$errors+='remaining_sql_sessions'}
        Write-StressJson (Join-Path $script:runPath 'cleanup-result.json') @{ Passed=$errors.Count -eq 0; Errors=$errors; RemainingSessions=[int]$remaining.Rows[0].n; Utc=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ') }
    } finally { if($lock){$lock.Dispose()} }
}

if($Operation -eq 'Preflight'){Preflight|ConvertTo-Json -Depth 12; return}
if($Operation -ne 'Run'){
    $script:runPath=Get-StressRunPath $RunId
    $script:manifest=Read-StressJson (Join-Path $script:runPath 'manifest.json')
    $RepositoryCredentialPath=$script:manifest.RepositoryCredentialPath
    if($Operation -eq 'Status'){ $script:manifest|ConvertTo-Json -Depth 24; return }
    [IO.File]::WriteAllText((Join-Path $script:runPath 'stop'),'operator_stop')
    Cleanup-Run
    return
}

[void][IO.Directory]::CreateDirectory($settings.Root)
$runLock=$null
try { $runLock=[IO.File]::Open((Join-Path $settings.Root 'active.lock'),'OpenOrCreate','ReadWrite','None') }
catch { throw 'Another stress controller is running.' }
try {
    # An abandoned run must be explicitly cleaned before admitting another run.
    foreach($directory in Get-ChildItem $settings.Root -Directory){
        if(Test-Path (Join-Path $directory.FullName 'manifest.json')){
            $cleanup=Join-Path $directory.FullName 'cleanup-result.json'
            if(-not(Test-Path $cleanup) -or -not(Read-StressJson $cleanup).Passed){throw "Cleanup required for run $($directory.Name)."}
        }
    }
    $preflight=Preflight
    if(-not $preflight.Passed){$preflight|ConvertTo-Json -Depth 12; throw 'Stress preflight failed; no workload started.'}
    $RunId=[guid]::NewGuid().ToString('N'); $script:runPath=Get-StressRunPath $RunId
    [void][IO.Directory]::CreateDirectory($script:runPath)
    $script:manifest=@{ RunId=$RunId; Profile=$Profile; StartedUtc=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); DeadlineUtc=[DateTime]::UtcNow.AddMinutes(90).ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); Status='running'; Phase='baseline'; Controller=(Process-Record (Get-Process -Id $PID)); Watchdog=$null; Workers=@(); Results=@(); Rule=$null; Destination=$null; Maintenance=$null; RepositoryCredentialPath=$RepositoryCredentialPath; Preflight=$preflight }
    Save-Manifest; Assert-RunActive
    $watchdog=Start-Process $shell -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'stress-watchdog.ps1')+'"'),'-RunId',$RunId) -WindowStyle Hidden -PassThru
    $script:manifest.Watchdog=Process-Record $watchdog; Save-Manifest
    Write-Output "Stress run $RunId started. Evidence: $script:runPath"
    try {
        . (Join-Path $PSScriptRoot 'Stress.Scenarios.ps1')
        Invoke-ControlledStressScenarios
        $script:manifest.Status=if(@($script:manifest.Results|Where-Object Status -ne 'passed').Count){'inconclusive'}else{'passed'}
    } catch {
        $inconclusive=$_.Exception.Message.StartsWith('STRESS_INCONCLUSIVE: ')
        $script:manifest.Status=if($inconclusive){'inconclusive'}else{'failed'}
        $reason=if($inconclusive){$_.Exception.Message.Substring('STRESS_INCONCLUSIVE: '.Length)}elseif($_.Exception.Message.StartsWith('STRESS: ')){$_.Exception.Message.Substring(8)}else{'Scenario failed or exceeded a bound; inspect recorded evidence and watchdog result.'}
        Add-Result $script:manifest.Phase $script:manifest.Status @{ ErrorType=$_.Exception.GetType().Name; Reason=$reason }
    } finally {
        [IO.File]::WriteAllText((Join-Path $script:runPath 'stop'),'run_complete')
        Cleanup-Run
        $cleanup=Read-StressJson (Join-Path $script:runPath 'cleanup-result.json')
        if(-not $cleanup.Passed){$script:manifest.Status='failed'}
        $script:manifest.CompletedUtc=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); Save-Manifest
        [IO.File]::WriteAllText((Join-Path $script:runPath 'finished'),$script:manifest.Status)
        Write-StressReport $script:runPath
        $script:manifest|ConvertTo-Json -Depth 24
    }
} finally { if($runLock){$runLock.Dispose()} }
