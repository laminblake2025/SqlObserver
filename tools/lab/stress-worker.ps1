[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId,
    [Parameter(Mandatory)][ValidateSet('Ramp','Idle','Blocker','Waiter','DeadlockA','DeadlockB','Viewer')][string]$Scenario,
    [ValidateSet('SqlObserverLabSales','SqlObserverLabWarehouse')][string]$Database='SqlObserverLabSales',
    [ValidateRange(1,5400)][int]$Seconds=120,
    [ValidateRange(1,4096)][int]$Worker=1
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Stress.Common.psm1') -Force
Assert-StressHost
$runPath=Get-StressRunPath $RunId
$manifest=Read-StressJson (Join-Path $runPath 'manifest.json')
if ($manifest.RunId -ne $RunId) { throw 'Worker run mismatch.' }
$stop=Join-Path $runPath 'stop'
if(Test-Path $stop){exit 0}
$deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
$globalDeadline=ConvertTo-StressUtc $manifest.DeadlineUtc
if ($deadline -gt $globalDeadline) { $deadline=$globalDeadline }
$tag="SqlObserver.Stress.$RunId.$Scenario.$Worker"
$outcome=@{ Worker=$Worker; Scenario=$Scenario; Database=$Database; StartedUtc=[DateTime]::UtcNow.ToString('o'); Batches=0; ExpectedDeadlock=$false; Status='running' }
$connection=$null
try {
    if ($Scenario -eq 'Viewer') {
        # Record Windows authentication/connection startup separately from the
        # steady polling measurements; do not mix one cold handshake into a
        # short polling-window P95. The warmup request is still non-overlapping.
        $warmup=[Diagnostics.Stopwatch]::StartNew()
        $null=Invoke-StressApi '/activity/live'
        $warmup.Stop(); $outcome.ColdStartMs=$warmup.Elapsed.TotalMilliseconds
        $samples=@()
        while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path $stop)) {
            if (([DateTime]::UtcNow-(Get-Item (Join-Path $runPath 'heartbeat')).LastWriteTimeUtc).TotalSeconds -gt 45) { throw 'Controller heartbeat expired.' }
            $watch=[Diagnostics.Stopwatch]::StartNew(); $ok=$false
            try { $page=Invoke-StressApi '/activity/live'; $ok=$true } catch { }
            $watch.Stop(); $samples+=@{ Utc=[DateTime]::UtcNow.ToString('o'); Milliseconds=$watch.Elapsed.TotalMilliseconds; Ok=$ok; SnapshotId=if($ok){$page.snapshotId}else{$null} }
            # Settlement-based polling: no overlapping request or queued timer ticks.
            Start-Sleep -Seconds 10
        }
        $outcome.Samples=$samples
    } else {
        $builder=New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source']='tcp:WIN-QNGOV5GDM24,1433'; $builder['Initial Catalog']=$Database; $builder['Integrated Security']=$true
        $builder['Encrypt']=$true; $builder['TrustServerCertificate']=$false; $builder['Application Name']=$tag; $builder['Pooling']=$false; $builder['Connect Timeout']=5
        $connection=New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString); $connection.Open()
        $identityCommand=$connection.CreateCommand(); $identityCommand.CommandText='SELECT @@SPID'; $identityCommand.CommandTimeout=5
        try { $outcome.SessionId=[int]$identityCommand.ExecuteScalar() } finally { $identityCommand.Dispose() }
        while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path $stop)) {
            if (([DateTime]::UtcNow-(Get-Item (Join-Path $runPath 'heartbeat')).LastWriteTimeUtc).TotalSeconds -gt 45) { throw 'Controller heartbeat expired.' }
            if ($Scenario -eq 'Idle') { Start-Sleep -Seconds 1; continue }
            $command=$connection.CreateCommand(); $command.CommandTimeout=35
            $query=switch ($Scenario) {
                'Ramp' {
                    if ($Database -eq 'SqlObserverLabSales') {
                        'DECLARE @n bigint; SELECT @n=SUM(CONVERT(bigint,l.Quantity)*o.CustomerId) FROM dbo.OrderLines l JOIN dbo.Orders o ON o.OrderId=l.OrderId OPTION(MAXDOP 1); BEGIN TRAN; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=1; ROLLBACK; SELECT TOP(2000) OrderId,CustomerId INTO #stress FROM dbo.Orders ORDER BY OrderDate DESC OPTION(MAXDOP 1); DROP TABLE #stress;'
                    } else {
                        'DECLARE @n bigint; SELECT @n=SUM(CONVERT(bigint,Quantity)*ProductId) FROM dbo.FactInventory OPTION(MAXDOP 1); BEGIN TRAN; UPDATE dbo.FactInventory SET Quantity=Quantity+1 WHERE Id=1; ROLLBACK; SELECT TOP(2000) Id,Description INTO #stress FROM dbo.FactInventory ORDER BY Description DESC OPTION(MAXDOP 1); DROP TABLE #stress;'
                    }
                }
                'Blocker' { "BEGIN TRAN; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=2; WAITFOR DELAY '00:00:20'; ROLLBACK;" }
                'Waiter' { 'BEGIN TRAN; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=2; ROLLBACK;' }
                'DeadlockA' { "SET DEADLOCK_PRIORITY LOW; BEGIN TRAN; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=3; WAITFOR DELAY '00:00:05'; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=4; ROLLBACK;" }
                'DeadlockB' { "BEGIN TRAN; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=4; WAITFOR DELAY '00:00:05'; UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=3; ROLLBACK;" }
            }
            $command.CommandText="SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 25000; $query"
            try { [void]$command.ExecuteNonQuery(); $outcome.Batches++ }
            catch [Data.SqlClient.SqlException] {
                if ($Scenario -like 'Deadlock*' -and $_.Exception.Number -eq 1205) { $outcome.ExpectedDeadlock=$true }
                elseif ($Scenario -eq 'Waiter' -and $_.Exception.Number -eq 1222) { }
                else { throw }
            } finally { $command.Dispose() }
            if ($Scenario -like 'Deadlock*') { break }
            Start-Sleep -Milliseconds 100
        }
    }
    $outcome.Status='completed'
} catch { $outcome.Status='failed'; $outcome.ErrorType=$_.Exception.GetType().Name }
finally {
    if ($connection) { $connection.Dispose() }
    $outcome.CompletedUtc=[DateTime]::UtcNow.ToString('o')
    Write-StressJson (Join-Path $runPath "worker-$Worker.json") $outcome
}
if ($outcome.Status -eq 'failed') { exit 1 }
