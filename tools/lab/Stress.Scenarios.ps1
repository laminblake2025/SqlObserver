# Dot-sourced by the controller. All administrative writes use the product API;
# direct repository access is read-only evidence, never simulated collector data.
function Set-Phase([string]$Name) { $script:manifest.Phase=$Name; Save-Manifest }
function Assert-Evidence([bool]$Condition,[string]$Reason) { if(-not $Condition){throw "STRESS: $Reason"} }
function Confirm-QueryCollectionCoverage($Coverage, $DatabaseChoices) {
    foreach($name in @('SqlObserverLabSales','SqlObserverLabWarehouse')) {
        $choice=@($DatabaseChoices|Where-Object name -eq $name)
        Assert-Evidence ($choice.Count -eq 1) 'Workload database is absent from the inventory.'
        Assert-Evidence (@($Coverage|Where-Object {$_.database_id -eq $choice[0].id -and $_.observations -gt 0}).Count -gt 0) 'Query collection did not observe both workload databases.'
    }
}
function Get-RunMetricSamples {
    @(Repository "SELECT sample_id,observed_at,metric_value FROM telemetry.raw_metric_sample WHERE instance_id='$($settings.Target)' AND metric_key='engine.user_connections' AND observed_at>='$($script:manifest.StartedUtc)' ORDER BY observed_at LIMIT 512")
}
function Get-RuleState {
    $rows=@(Repository "SELECT state,revision,alert_id,alert_episode_id,last_value,last_observed_at,delivery_suppressed FROM alerting.rule_state WHERE instance_id='$($settings.Target)' AND rule_id='$($script:manifest.Rule.ruleId)'")
    if($rows.Count){return $rows[0]}; return $null
}
function Read-LiveEvidence([string]$Phase) {
    $tag=[Uri]::EscapeDataString("SqlObserver.Stress.$($script:manifest.RunId)")
    $page=Invoke-StressApi "/activity/live?application=$tag&includeIdle=true"
    # List responses contain identifiers/measurements but never captured SQL.
    Write-StressJson (Join-Path $script:runPath "$Phase-live.json") $page
    return $page
}
function Confirm-Delivery([string]$AlertId,[int]$Kind) {
    $delivery=Wait-Condition {
        $rows=@(Repository "SELECT o.delivery_id,o.alert_id,o.event_kind,o.created_at,o.completed_at,a.outcome,a.attempted_at FROM alerting.delivery_outbox o LEFT JOIN alerting.delivery_attempt a USING(delivery_id) WHERE o.destination_id='$($script:manifest.Destination.destinationId)' AND o.alert_id='$AlertId' AND o.event_kind=$Kind ORDER BY a.attempt")
        if(@($rows|Where-Object outcome -eq 'succeeded').Count){return ,$rows}
    } 30 'Local delivery did not succeed within 30 seconds.'
    $succeeded=@($delivery|Where-Object outcome -eq 'succeeded')
    Assert-Evidence ($succeeded.Count -eq 1) 'Duplicate successful delivery.'
    $latency=((ConvertTo-StressUtc $succeeded[0].attempted_at)-(ConvertTo-StressUtc $succeeded[0].created_at)).TotalSeconds
    Assert-Evidence ($latency -le 30) 'Delivery exceeded thirty seconds from the committed event.'
    $eventPattern=if($Kind -eq 3){'"event"\s*:\s*"acknowledged"'}else{'"event"\s*:\s*'+$Kind+'\b'}
    $event=@(Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='SqlObserver Alerts';StartTime=[DateTime]$script:manifest.StartedUtc} -ErrorAction SilentlyContinue | Where-Object {$_.Message.Contains($AlertId) -and $_.Message -match $eventPattern})
    Assert-Evidence ($event.Count -eq 1) 'Expected exactly one corresponding Windows event.'
    Add-Result "delivery-$Kind-$AlertId" 'passed' @{ Delivery=$delivery; EventRecordIds=@($event|ForEach-Object RecordId) }
}
function Alert-Episode([string]$Name,[bool]$Acknowledge,[bool]$Suppressed) {
    Set-Phase $Name
    $prior=Get-RuleState
    $priorId=if($prior){$prior.alert_id}else{$null}
    $counter=Invoke-StressSql "SELECT MAX(cntr_value) n FROM sys.dm_os_performance_counters WHERE RTRIM(counter_name)='User Connections' AND RTRIM(object_name) LIKE '%:General Statistics'"
    Assert-Evidence ($counter.Rows.Count -eq 1 -and $counter.Rows[0].n -isnot [DBNull] -and $null -ne $counter.Rows[0].n) 'Connection baseline counter unavailable.'
    $current=[int]$counter.Rows[0].n
    # The measurement connection is closed before workers start.
    $current=[Math]::Max(0,$current-1)
    $threshold=$script:manifest.Rule.threshold
    if($current -gt $threshold-2){throw 'STRESS_INCONCLUSIVE: Baseline drift prevents deterministic connection alert recovery.'}
    $connections=[int][Math]::Ceiling($threshold+4-$current)
    Assert-Evidence ($connections -ge 1 -and $connections -le 32) 'Connection budget cannot safely cross alert threshold.'
    $crossing=[DateTime]::UtcNow
    $workers=Start-Workers Idle $connections ($script:engineInterval*3+150)
    try {
        $fired=Wait-Condition { $state=Get-RuleState; if($state -and $state.state -eq 3 -and $state.alert_id -ne $priorId){return $state} } ($script:engineInterval*2+45) 'Alert did not fire within the collection-derived deadline.'
        $history=@(Repository "SELECT from_state,to_state,observed_at,alert_id,delivery_suppressed FROM alerting.state_history WHERE instance_id='$($settings.Target)' AND rule_id='$($script:manifest.Rule.ruleId)' AND observed_at>='$($crossing.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'))' ORDER BY history_id")
        Assert-Evidence (@($history|Where-Object to_state -eq 2).Count -gt 0) 'Pending confirmation was not recorded.'
        Assert-Evidence ([bool]$fired.delivery_suppressed -eq $Suppressed) 'Maintenance suppression mismatch.'
        $active=Invoke-StressApi '/alerts/active'
        Assert-Evidence (@($active.items|Where-Object alertId -eq $fired.alert_id).Count -eq 1) 'Fired alert missing from application API.'
        if(-not $Suppressed){Confirm-Delivery $fired.alert_id 1}
        if($Acknowledge){
            $ackState=Get-RuleState
            Assert-Evidence ($ackState.alert_id -eq $fired.alert_id -and $ackState.alert_episode_id -eq $fired.alert_episode_id) 'Alert episode changed before acknowledgement.'
            Admin "/alerts/$($fired.alert_id)/acknowledge" @{operationId=[guid]::NewGuid().ToString('D');expectedRevision=$ackState.revision;expectedEpisodeId=$ackState.alert_episode_id}
            $ack=Get-RuleState; Assert-Evidence ($ack.state -eq 4) 'Acknowledgement did not persist.'
            Confirm-Delivery $fired.alert_id 3
        }
        Hold 30
        if($Suppressed){
            $delivered=@(Repository "SELECT delivery_id FROM alerting.delivery_outbox WHERE alert_id='$($fired.alert_id)' AND completed_at IS NOT NULL")
            Assert-Evidence ($delivered.Count -eq 0) 'Maintenance unexpectedly delivered a notification.'
        } else {
            $duplicates=@(Repository "SELECT event_kind,count(*) n FROM alerting.delivery_outbox WHERE destination_id='$($script:manifest.Destination.destinationId)' AND alert_id='$($fired.alert_id)' GROUP BY event_kind HAVING count(*)>1")
            Assert-Evidence ($duplicates.Count -eq 0) 'Stable alert generated duplicate notification intents.'
        }
    } finally { Stop-Workers $workers }
    $released=[DateTime]::UtcNow
    $resolved=Wait-Condition { $state=Get-RuleState; if($state -and $state.state -in @(1,5) -and (ConvertTo-StressUtc $state.last_observed_at) -gt $released){return $state} } ($script:engineInterval+45) 'Alert did not recover within its deadline.'
    if(-not $Suppressed){Confirm-Delivery $fired.alert_id 2}
    Add-Result $Name 'passed' @{ AlertId=$fired.alert_id; OpenedConnections=$connections; CrossingUtc=$crossing.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'); FiredObservation=$fired.last_observed_at; ResolvedObservation=$resolved.last_observed_at; History=$history; Suppressed=$Suppressed }
}
function Invoke-ControlledStressScenarios {
    $script:engineInterval=[int](@($script:manifest.Preflight.Schedules|Where-Object collector_id -eq 'engine.core')[0].seconds)
    $relevant=@($script:manifest.Preflight.Schedules|Where-Object {$_.enabled -and $_.collector_id -in @('engine.core','activity.requests','activity.sessions','blocking.current','queries.performance','deadlocks.system-health')})
    $slowest=[int](($relevant|Measure-Object seconds -Maximum).Maximum)
    if($script:engineInterval -le 0 -or $slowest -gt 300){throw 'Configured collection cadence cannot fit the bounded profile; schedules were not changed.'}
    $baseline=Wait-Condition { $samples=Get-RunMetricSamples; if($samples.Count -ge 3){return ,$samples} } ($script:engineInterval*3+45) 'Three baseline engine samples were not collected.'
    $maximum=[double](($baseline|Measure-Object metric_value -Maximum).Maximum)
    Add-Result baseline passed @{ Samples=$baseline; MaximumConnections=$maximum; EngineInterval=$script:engineInterval; RelevantInterval=$slowest }
    if($script:manifest.Profile -ne 'Alerts') {
    if($script:manifest.Profile -eq 'Full') {
    foreach($count in @(2,4,8,16)){
        Set-Phase "ramp-$count"
        $duration=[Math]::Max(120,2*$slowest)
        $workers=Start-Workers Ramp $count ($duration+30)
        try {
            Hold 25
            $page=Read-LiveEvidence "ramp-$count"
            # SQL statements are sampled: wait for both databases instead of assuming
            # every short batch appears in the first ten-second observation.
            $page=Wait-Condition {
                $candidate=Read-LiveEvidence "ramp-$count"
                if(@($candidate.rows|Select-Object -ExpandProperty databaseName -Unique).Count -eq 2){return $candidate}
            } 60 'Both workload databases were not observed.'
            foreach($database in @('SqlObserverLabSales','SqlObserverLabWarehouse')){
                $choice=@($page.databases|Where-Object name -eq $database)[0]
                $filtered=Invoke-StressApi "/activity/live?snapshot=$($page.snapshotId)&databaseId=$($choice.id)&includeIdle=true&application=$([Uri]::EscapeDataString('SqlObserver.Stress.'+$script:manifest.RunId))"
                Assert-Evidence (@($filtered.rows|Where-Object databaseName -ne $database).Count -eq 0 -and $filtered.rows.Count -gt 0) 'Database filter mismatch.'
            }
            if($count -eq 2){
                $queryRow=@($page.rows|Where-Object {$_.queryId -and $_.requestId -ne $null})|Select-Object -First 1
                if($queryRow){
                    $query=Invoke-StressApi "/activity/live/query?snapshot=$($page.snapshotId)&identity=$($queryRow.identity)"
                    Assert-Evidence ($query.state -in @('available','truncated') -and $query.text.Length -gt 0) 'Protected query details unavailable.'
                    Add-Result protected-query passed @{SnapshotId=$page.snapshotId;Identity=$queryRow.identity;State=$query.state;ObservedUtc=$page.observedUtc}
                    $query=$null
                } else { Add-Result protected-query inconclusive @{Reason='No active statement was sampled during this check.'} }
            }
            Hold $duration
            foreach($worker in $workers){
                $process=Get-Process -Id $worker.Id -ErrorAction SilentlyContinue
                # Workers have a duration bound; a completed worker must provide its
                # own successful outcome rather than silently reducing the load.
                if(-not(Test-StressProcessIdentity $worker $process)){
                    $outcomePath=Join-Path $script:runPath "worker-$($worker.Number).json"
                    Assert-Evidence (Test-Path $outcomePath) 'Ramp worker disappeared without an outcome.'
                    $outcome=Read-StressJson $outcomePath
                    Assert-Evidence ($outcome.Status -eq 'completed' -and $outcome.Batches -gt 0) 'Ramp worker failed to execute workload.'
                }
            }
        } finally { Stop-Workers $workers }
        Add-Result "ramp-$count" passed @{Workers=$count;MinimumDurationSeconds=$duration;SnapshotId=$page.snapshotId}
    }
    } else {
        Add-Result load-ramp inconclusive @{Reason='Functional profile omits the load ramp; it does not establish sixteen-worker load acceptance.'}
    }
    Set-Phase blocking
    $duration=[Math]::Max(120,2*$slowest)
    $blocker=Start-Workers Blocker 1 ($duration+40); Hold 2
    $waiters=Start-Workers Waiter 2 ($duration+35)
    try {
        $blocked=Wait-Condition { $page=Read-LiveEvidence 'blocking'; if(@($page.rows|Where-Object {$_.blocker -gt 0}).Count){return $page} } 45 'No live blocker/waiter evidence.'
        Hold $duration
        $from=[Uri]::EscapeDataString($script:manifest.StartedUtc); $to=[Uri]::EscapeDataString([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'))
        $legacy=Invoke-StressApi "/activity/blocking/history?limit=100&fromUtc=$from&toUtc=$to"
        $ownedSessions=@($blocked.rows|ForEach-Object sessionId)
        Assert-Evidence (@($legacy.items|Where-Object {$_.edge.blockedSessionId -in $ownedSessions -and $_.edge.blockerSessionId -in $ownedSessions}).Count -gt 0) 'Blocking history lacks the owned blocker/waiter relationship.'
        Write-StressJson (Join-Path $script:runPath 'blocking-history.json') $legacy
        Add-Result blocking passed @{SnapshotId=$blocked.snapshotId;LiveBlockedRows=@($blocked.rows|Where-Object {$_.blocker -gt 0}).Count}
    } finally { Stop-Workers @($blocker+$waiters) }
    Set-Phase deadlock
    $deadlockStart=[DateTime]::UtcNow
    $a=Start-Workers DeadlockA 1 30; $b=Start-Workers DeadlockB 1 30
    Hold 15
    $outcomes=@($a+$b|ForEach-Object {Read-StressJson (Join-Path $script:runPath "worker-$($_.Number).json")})
    Assert-Evidence (@($outcomes|Where-Object ExpectedDeadlock).Count -eq 1) 'Expected deadlock victim was not recorded.'
    $deadlocks=Wait-Condition {
        $from=[Uri]::EscapeDataString($deadlockStart.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')); $to=[Uri]::EscapeDataString([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'))
        $events=Invoke-StressApi "/deadlocks?fromUtc=$from&toUtc=$to"
        if($events.items.Count){return $events}
    } ($slowest*2+45) 'Deadlock was not collected.'
    Write-StressJson (Join-Path $script:runPath 'deadlocks.json') $deadlocks
    $ownedDeadlockSessions=@($outcomes|ForEach-Object SessionId)
    $matched=$false
    foreach($item in $deadlocks.items){
        $detail=Invoke-StressApi "/deadlocks/$($item.eventId)"
        if(@($detail.participants|Where-Object {$_.sessionId -in $ownedDeadlockSessions}).Count -eq 2 -and @($detail.participants|Where-Object isVictim).Count -eq 1){$matched=$true;break}
    }
    Assert-Evidence $matched 'Deadlock details lack the owned participants and victim.'
    Add-Result deadlock passed @{WorkerOutcomes=$outcomes;EventIds=@($deadlocks.items|ForEach-Object eventId)}

    Set-Phase viewer-comparison
    foreach($viewers in @(1,5)){
        $start=[DateTime]::UtcNow
        $workers=Start-Workers Viewer $viewers 125
        Hold 140
        $outcomes=@($workers|ForEach-Object{Read-StressJson (Join-Path $script:runPath "worker-$($_.Number).json")})
        $samples=@($outcomes|ForEach-Object Samples)
        $p95=Get-StressPercentile @($samples|ForEach-Object Milliseconds)
        $cadence=@(Repository "SELECT observed_at FROM live_activity.snapshot WHERE target_id='$($settings.Target)' AND observed_at>='$($start.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'))' ORDER BY observed_at")
        Assert-Evidence ($p95 -lt 2000 -and @($samples|Where-Object {-not $_.Ok}).Count -eq 0) 'Viewer responsiveness failed.'
        Assert-Evidence ($cadence.Count -ge 11 -and $cadence.Count -le 16) 'Viewer test collection cadence outside ten-second bounds.'
        Add-Result "viewers-$viewers" passed @{P95Ms=$p95;Requests=$samples.Count;CollectionTimes=$cadence;DurationSeconds=140}
    }

    }
    Set-Phase alert-setup
    if(-not [Diagnostics.EventLog]::SourceExists('SqlObserver Alerts')) { New-EventLog -LogName Application -Source 'SqlObserver Alerts' }
    # The product's bounded helper uses eventcreate, which requires a custom
    # source rather than the .NET source registration created by New-EventLog.
    $sourceKey='HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\SqlObserver Alerts'
    New-ItemProperty -LiteralPath $sourceKey -Name CustomSource -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $sourceKey -Name EventMessageFile -Value '%SystemRoot%\System32\EventCreate.exe' -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $sourceKey -Name TypesSupported -Value 7 -PropertyType DWord -Force | Out-Null
    $script:manifest.Rule=@{ruleId=[guid]::NewGuid().ToString('D');name="stress.$($script:manifest.RunId)";kind='metric_threshold';metricId='engine.user_connections';comparison='greater_than';threshold=$maximum+4;hysteresis=2;confirmationCount=2;confirmationSeconds=$script:engineInterval*3;evaluationSeconds=15;enabled=$true;operationId=[guid]::NewGuid().ToString('D')}
    $script:manifest.Destination=@{destinationId=[guid]::NewGuid().ToString('D');kind='windows-event-log';configurationReference='SqlObserver Alerts';enabled=$true;operationId=[guid]::NewGuid().ToString('D')}
    Save-Manifest
    Admin '/alerts/rules' $script:manifest.Rule
    Admin '/alerts/destinations' $script:manifest.Destination
    $approval=@{}; foreach($key in $script:manifest.Destination.Keys){$approval[$key]=$script:manifest.Destination[$key]}
    $approval.expectedRevision=1; $approval.operationId=[guid]::NewGuid().ToString('D')
    Admin "/alerts/destinations/$($approval.destinationId)/approve" $approval
    Wait-Condition { $state=Get-RuleState; if($state -and $state.state -in @(1,5)){return $state} } ($script:engineInterval+45) 'Rule did not establish a normal baseline.' | Out-Null
    Alert-Episode 'alert-lifecycle' $true $false
    Alert-Episode 'alert-new-episode' $false $false
    $script:manifest.Maintenance=@{windowId=[guid]::NewGuid().ToString('D');startsAtUtc=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ');endsAtUtc=[DateTime]::UtcNow.AddSeconds(4*$script:engineInterval+240).ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ');reason="stress.$($script:manifest.RunId)";operationId=[guid]::NewGuid().ToString('D')}
    Save-Manifest; Admin '/alerts/maintenance' $script:manifest.Maintenance
    Alert-Episode 'maintenance-suppression' $false $true

    Set-Phase recovery
    Hold ($slowest+15)
    Confirm-StressRecovery
}
function Confirm-StressRecovery {
    $evidenceEnd=if($script:manifest.PSObject.Properties['CompletedUtc'] -and $script:manifest.CompletedUtc){(ConvertTo-StressUtc $script:manifest.CompletedUtc).ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')}else{[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ')}
    $page=Read-LiveEvidence recovery
    Assert-Evidence ($page.rows.Count -eq 0) 'Owned SQL sessions remain after recovery.'
    $from=[Uri]::EscapeDataString($script:manifest.StartedUtc);$to=[Uri]::EscapeDataString($evidenceEnd)
    $history=Invoke-StressApi "/activity/live/history?from=$from&to=$to"
    Assert-Evidence (@($history).Count -gt 2) 'Historical minute snapshots missing.'
    Write-StressJson (Join-Path $script:runPath 'minute-history.json') $history
    $historical=Invoke-StressApi "/activity/live?snapshot=$($history[0].id)&includeIdle=true"
    Assert-Evidence ($historical.snapshotId -eq $history[0].id) 'Exact historical snapshot could not be retrieved.'
    if($script:manifest.Profile -ne 'Alerts') {
    $top=Invoke-StressApi "/query-performance/top?metric=cpu&limit=200&fromUtc=$from&toUtc=$to"
    $databaseChoices=Invoke-StressApi '/activity/live'
    # Rankings are bounded and their metric intervals can begin before this run.
    # Verify collection-time coverage separately; never relabel old cumulative
    # measurements as resource usage generated exclusively by this workload.
    # Select query-collector runs first. Probing observation RLS for every unrelated
    # engine/activity run makes this reporting read depend on retained history.
    # The lateral boundary retains exact counts and the existing ownership checks.
    $coverage=@(Repository "WITH scoped_runs AS MATERIALIZED (SELECT run_id FROM telemetry.collection_run WHERE collector_id='queries.performance' AND instance_id='$($settings.Target)' AND started_at>='$($script:manifest.StartedUtc)' AND started_at<='$evidenceEnd') SELECT o.database_id,o.source,o.semantics,count(*) observations,min(o.interval_start) earliest_interval_start,max(o.interval_end) latest_interval_end,max(o.observed_at) latest_observed_at FROM scoped_runs r CROSS JOIN LATERAL (SELECT scoped.* FROM events.query_performance_observation scoped WHERE scoped.collection_run_id=r.run_id OFFSET 0) o GROUP BY o.database_id,o.source,o.semantics")
    Confirm-QueryCollectionCoverage $coverage $databaseChoices.databases
    Write-StressJson (Join-Path $script:runPath 'query-collection-coverage.json') $coverage
    Write-StressJson (Join-Path $script:runPath 'query-performance.json') $top
    }
    $routes=@('/health/','/health/databases','/query-performance/status',"/query-performance/top?metric=cpu&fromUtc=$from&toUtc=$to","/api/v1/overview?targetId=$($settings.Target)&fromUtc=$from&toUtc=$to",'/activity/sessions','/alerts/active')
    foreach($route in $routes){$response=Invoke-StressApi $route; Add-Result "api-$($route.Split('?')[0])" passed @{Reachable=$true}}
    $samples=@(Get-Content (Join-Path $script:runPath 'guardrails.jsonl')|ForEach-Object{$_|ConvertFrom-Json})
    $p95=Get-StressPercentile @($samples|ForEach-Object ProbeMs)
    $stale=@($samples|Where-Object {$null -eq $_.LiveAgeSeconds -or $_.LiveAgeSeconds -gt 30})
    Assert-Evidence ($p95 -lt 2000 -and $stale.Count -eq 0) 'Live latency or freshness acceptance failed.'
    $collectorEvidence=@(Repository "WITH observed AS (SELECT r.collector_id,r.started_at,o.completed_at,o.outcome,o.duration_ms,lag(r.started_at) OVER(PARTITION BY r.collector_id ORDER BY r.started_at) prior_start,lag(o.completed_at) OVER(PARTITION BY r.collector_id ORDER BY r.started_at) prior_completion FROM telemetry.collection_run r JOIN telemetry.collection_run_outcome o USING(run_id) WHERE r.instance_id='$($settings.Target)' AND r.started_at>='$($script:manifest.StartedUtc)' AND r.started_at<='$evidenceEnd') SELECT s.collector_id,extract(epoch FROM s.collection_interval) configured_seconds,s.last_outcome,extract(epoch FROM clock_timestamp()-s.last_completed_at) current_age_seconds,count(o.started_at) samples,avg(extract(epoch FROM o.started_at-o.prior_start)) mean_interval_seconds,max(extract(epoch FROM o.started_at-o.prior_start)) maximum_interval_seconds,max(o.duration_ms) maximum_duration_ms,count(*) FILTER(WHERE o.prior_completion>o.started_at) overlap_count,count(*) FILTER(WHERE o.outcome NOT IN ('succeeded','partial','unsupported')) failures FROM control.collector_schedule s LEFT JOIN observed o USING(collector_id) WHERE s.instance_id='$($settings.Target)' AND s.enabled GROUP BY s.collector_id,s.collection_interval,s.last_outcome,s.last_completed_at ORDER BY s.collector_id")
    Write-StressJson (Join-Path $script:runPath 'collector-cadence.json') $collectorEvidence
    Assert-Evidence (@($collectorEvidence|Where-Object overlap_count -gt 0).Count -eq 0) 'Collector executions overlapped.'
    Add-Result collector-cadence passed @{Collectors=$collectorEvidence}
    $metrics=@(Repository "SELECT metric_key,count(*) samples,min(observed_at) first_observed_at,max(observed_at) last_observed_at,min(metric_value) minimum_observed_value,max(metric_value) maximum_observed_value FROM telemetry.raw_metric_sample WHERE instance_id='$($settings.Target)' AND observed_at>='$($script:manifest.StartedUtc)' AND observed_at<='$evidenceEnd' GROUP BY metric_key ORDER BY metric_key LIMIT 256")
    $waits=@(Repository "SELECT wait_type,count(*) samples,min(observed_at) first_observed_at,max(observed_at) last_observed_at,min(wait_time_ms) minimum_cumulative_wait_ms,max(wait_time_ms) maximum_cumulative_wait_ms FROM telemetry.server_wait_snapshot WHERE instance_id='$($settings.Target)' AND observed_at>='$($script:manifest.StartedUtc)' AND observed_at<='$evidenceEnd' GROUP BY wait_type ORDER BY maximum_cumulative_wait_ms DESC LIMIT 100")
    Write-StressJson (Join-Path $script:runPath 'resource-monitoring.json') @{Metrics=$metrics;Waits=$waits;Semantics='Observed ranges retain source metric semantics. Wait counters are cumulative; no workload-only totals or restart-unsafe deltas are inferred.'}
    $bytes=(Repository 'SELECT pg_database_size(current_database()) bytes')[0].bytes
    $recovered=Get-StressSample
    Assert-Evidence ($null -eq (Get-StressGuardDecision $recovered 0 0) -and $recovered.CpuPercent -le [Math]::Max(30,$script:manifest.Preflight.Sample.CpuPercent+20)) 'Resources did not settle after recovery.'
    foreach($name in @('SqlObserverServer','SqlObserverCollector')){Assert-Evidence ((Get-Service $name).Status -eq 'Running') 'Observer service stopped during the run.'}
    Add-Result recovery passed @{P95LiveMs=$p95;StaleSamples=$stale.Count;RepositoryBytes=$bytes;RepositoryGrowthBytes=$bytes-$script:manifest.Preflight.RepositoryBytes;MinuteSnapshots=@($history).Count;ResourceSample=$recovered}
    Add-Result browser-verification inconclusive @{Reason='Pause/resume, retained details, and page rendering require browser evidence from this run; API checks alone do not establish these behaviors.'}
}
