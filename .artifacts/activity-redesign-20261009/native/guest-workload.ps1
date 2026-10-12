param([ValidatePattern('^[0-9a-f]{32}$')][string]$RunId,[string]$BlockerSql,[string]$BlockedSql)
$ErrorActionPreference='Stop'
if ($env:COMPUTERNAME -cne 'WIN-QNGOV5GDM24') { throw 'Guest identity mismatch.' }
$connectionString='Server=tcp:WIN-QNGOV5GDM24,1433;Database=SqlObserverLabSales;Integrated Security=true;Encrypt=true;TrustServerCertificate=false;Connect Timeout=5;Application Name=SQL Observer Activity qualification '+$RunId
$connection=New-Object Data.SqlClient.SqlConnection($connectionString)
$report=[ordered]@{runId=$RunId;host=$env:COMPUTERNAME;startedUtc=[DateTime]::UtcNow.ToString('o');tls='validated';boundedSeconds=150;serviceChanges=$false;before=$null;after=$null;ownedSessionsRemaining=$null;workers=@();rollbackVerified=$false}
$jobs=@()
try {
    $connection.Open();$command=$connection.CreateCommand();$command.CommandTimeout=5
    $command.CommandText='SELECT Value FROM dbo.LockScenario WHERE Id=1';$report.before=[string]$command.ExecuteScalar()
    if ([string]::IsNullOrEmpty($report.before)) { throw 'Expected bounded lab row is missing.' }
    $command.Dispose()
    $worker={param($connectionString,$sql,$kind)
        $connection=New-Object Data.SqlClient.SqlConnection($connectionString+' '+$kind);$command=$null
        try {$connection.Open();$command=$connection.CreateCommand();$command.CommandTimeout=140;$command.CommandText=$sql;[void]$command.ExecuteNonQuery();[pscustomobject]@{kind=$kind;state='completed';rolledBack=$true}}
        catch {[pscustomobject]@{kind=$kind;state='failed';category=$_.Exception.GetType().FullName;rolledBack=$false}}
        finally {if($command){$command.Dispose()};$connection.Dispose()}
    }
    $jobs+=Start-Job -ScriptBlock $worker -ArgumentList $connectionString,($BlockerSql -replace '(?m)^GO\s*$',''),'blocker'
    $jobs+=Start-Job -ScriptBlock $worker -ArgumentList $connectionString,($BlockedSql -replace '(?m)^GO\s*$',''),'blocked'
    $deadline=[DateTime]::UtcNow.AddSeconds(150)
    while (@($jobs | Where-Object State -in @('Running','NotStarted')).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {Start-Sleep -Seconds 1}
    foreach($job in $jobs){if($job.State -in @('Running','NotStarted')){Stop-Job -Job $job};$report.workers+=@(Receive-Job -Job $job)}
} finally {
    foreach($job in $jobs){Stop-Job -Job $job -ErrorAction SilentlyContinue;Remove-Job -Job $job -ErrorAction SilentlyContinue}
    if($connection.State -eq 'Open'){
        $check=$connection.CreateCommand();$check.CommandTimeout=10
        $check.CommandText='SET LOCK_TIMEOUT 5000; SELECT Value FROM dbo.LockScenario WHERE Id=1';$report.after=[string]$check.ExecuteScalar()
        $check.CommandText='SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name IN (@blocker,@blocked)'
        [void]$check.Parameters.AddWithValue('@blocker','SQL Observer Activity qualification '+$RunId+' blocker');[void]$check.Parameters.AddWithValue('@blocked','SQL Observer Activity qualification '+$RunId+' blocked')
        $report.ownedSessionsRemaining=[int]$check.ExecuteScalar();$check.Dispose()
        $report.rollbackVerified=$report.before -ceq $report.after -and $report.ownedSessionsRemaining -eq 0
    }
    $connection.Dispose()
}
$report.finishedUtc=[DateTime]::UtcNow.ToString('o');$report | ConvertTo-Json -Depth 8 -Compress
