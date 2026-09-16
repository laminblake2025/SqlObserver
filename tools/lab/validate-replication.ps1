[CmdletBinding()]
param([ValidateRange(60,240)][int]$PauseSeconds=150)
$ErrorActionPreference='Stop'
if($env:COMPUTERNAME -ne 'WIN-QNGOV5GDM24'){throw 'Restricted to the disposable lab VM.'}
$sqlcmd='C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'
$runPath=Join-Path 'C:\SqlObserverLab\Workloads' ('replication-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
function Sql([string]$Query){
 $output=& $sqlcmd -S tcp:WIN-QNGOV5GDM24,1433 -E -N -b -l 10 -t 30 -W -w 300 -h -1 -Q ('SET NOCOUNT ON; '+$Query)
 if($LASTEXITCODE -ne 0){throw 'Lab SQL validation failed.'}
 return ($output -join "`n").Trim()
}
$job=Sql "SELECT CONVERT(varchar(36),CONVERT(uniqueidentifier,job_id)) FROM SqlObserverLabDistribution.dbo.MSdistribution_agents WHERE publisher_db=N'SqlObserverLabPublisher' AND publication=N'SqlObserverLabOrders' AND subscriber_db=N'SqlObserverLabSubscriber';"
if($job -notmatch '^[a-fA-F0-9-]{36}$'){throw 'Expected exactly one fixture distribution agent.'}
$before=[int](Sql 'SELECT COUNT(*) FROM SqlObserverLabSubscriber.dbo.OrderEvents;')
$expected=$before+1000
$started=[DateTime]::UtcNow
function Evidence([string]$Phase){
 $sql=Sql 'SELECT (SELECT COUNT(*) FROM SqlObserverLabPublisher.dbo.OrderEvents) publisher_rows,(SELECT COUNT(*) FROM SqlObserverLabSubscriber.dbo.OrderEvents) subscriber_rows,(SELECT SUM(UndelivCmdsInDistDB) FROM SqlObserverLabDistribution.dbo.MSdistribution_status) pending_commands;'
 $from=[Uri]::EscapeDataString($started.AddMinutes(-1).ToString('o'));$to=[Uri]::EscapeDataString([DateTime]::UtcNow.ToString('o'))
 $uri="https://win-qngov5gdm24:5443/api/v1/observation-targets/dfb2b72c-4ad4-4765-9523-528c798482f0/analytics/replication/status?limit=200&fromUtc=$from&toUtc=$to"
 $api=Invoke-RestMethod $uri -UseDefaultCredentials
 $result=[pscustomobject]@{Phase=$Phase;Utc=[DateTime]::UtcNow.ToString('o');Sql=$sql;Api=$api}
 $result|ConvertTo-Json -Depth 12|Set-Content (Join-Path $runPath ($Phase+'.json'))
 [pscustomobject]@{Phase=$Phase;RunPath=$runPath;Sql=$sql;Latest=($api.items|Sort-Object observedAtUtc|Select-Object -Last 1)}|ConvertTo-Json -Depth 5
}
Evidence 'before'
try{
 Sql "EXEC msdb.dbo.sp_stop_job @job_id=N'$job';" | Out-Null
 Start-Sleep -Seconds 3
 Sql "USE SqlObserverLabPublisher; DECLARE @base bigint=(SELECT MAX(EventId) FROM dbo.OrderEvents); INSERT dbo.OrderEvents SELECT TOP(1000) @base+ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),'Paid',39.95,SYSUTCDATETIME() FROM sys.all_objects;" | Out-Null
 [pscustomobject]@{Phase='paused';RunPath=$runPath;ExpectedSubscriberRows=$expected;PauseSeconds=$PauseSeconds}|ConvertTo-Json
 $until=[DateTime]::UtcNow.AddSeconds($PauseSeconds)
 while([DateTime]::UtcNow -lt $until){Start-Sleep -Seconds 10}
 Evidence 'backlog'
 $backlog=(Get-Content (Join-Path $runPath 'backlog.json') -Raw|ConvertFrom-Json).Api.items|Sort-Object observedAtUtc|Select-Object -Last 1
 if($backlog.pendingCommands -ne 1000 -or $backlog.synchronizationState -ne 'disabled' -or $null -ne $backlog.latencySeconds){throw 'Monitoring did not report the stopped agent and its 1000-command backlog correctly.'}
 if([int](Sql 'SELECT COUNT(*) FROM SqlObserverLabSubscriber.dbo.OrderEvents;') -ne $before){throw 'Subscriber changed while distribution was stopped.'}
}finally{
 Sql "EXEC msdb.dbo.sp_start_job @job_id=N'$job';" | Out-Null
}
$deadline=[DateTime]::UtcNow.AddMinutes(3)
while([int](Sql 'SELECT COUNT(*) FROM SqlObserverLabSubscriber.dbo.OrderEvents;') -ne $expected){
 if([DateTime]::UtcNow -ge $deadline){throw 'Subscriber did not converge within three minutes.'}
 Start-Sleep -Seconds 5
}
# Allow at least two scheduled monitoring observations after delivery resumes.
for($i=0;$i -lt 13;$i++){Start-Sleep -Seconds 10}
Evidence 'recovered'
$recovered=(Get-Content (Join-Path $runPath 'recovered.json') -Raw|ConvertFrom-Json).Api.items|Sort-Object observedAtUtc|Select-Object -Last 1
if($recovered.pendingCommands -ne 0 -or $recovered.synchronizationState -ne 'healthy'){throw 'Monitoring did not report healthy recovery with an empty queue.'}
$difference=Sql 'SELECT COUNT(*) FROM (SELECT * FROM SqlObserverLabPublisher.dbo.OrderEvents EXCEPT SELECT * FROM SqlObserverLabSubscriber.dbo.OrderEvents) missing;'
if($difference -ne '0'){throw 'Subscriber data differs from publisher.'}
[pscustomobject]@{Phase='passed';Rows=$expected;MissingRows=0;RunPath=$runPath}|ConvertTo-Json
