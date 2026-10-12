param(
    [Parameter(Mandatory = $true)]
    [string] $SqlInstance
)

$ErrorActionPreference = 'Stop'
$database = 'SqlObserverWaitGroupProbe_' + [guid]::NewGuid().ToString('N')
$probeLogin = 'SqlObserverWaitGroupLogin_' + [guid]::NewGuid().ToString('N')
$databaseCreated = $false
$loginCreated = $false

function Invoke-ProbeSql([string] $databaseName, [string] $statement) {
    $sqlcmdArgs = @('-S', $SqlInstance, '-d', $databaseName, '-E', '-b',
        '-W', '-h', '-1', '-s', '|', '-l', '5', '-t', '20', '-Q', $statement)
    $output = & sqlcmd @sqlcmdArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd failed in $databaseName`: $($output -join [Environment]::NewLine)"
    }
    return @($output)
}

function Read-CaptureModeAt([string[]] $output, [string] $expectedMode) {
    if ($output.Count -lt 1) { throw 'Candidate wait source returned no capture status.' }
    $status = $output[0].Split('|')
    if ($status.Count -ne 2 -or $status[0].Trim() -ne $expectedMode) {
        throw "Candidate wait source returned an unexpected capture status: $($output -join '; ')"
    }
    $observed = [datetimeoffset]::Parse($status[1].Trim())
    if ($observed.Offset -ne [timespan]::Zero) {
        throw 'Candidate wait source capture status was not observed in UTC.'
    }
    return $observed
}

try {
    $major = Invoke-ProbeSql 'master' "SET NOCOUNT ON; SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))"
    if (@($major | Where-Object { $_ -match '^\s*16\s*$' }).Count -ne 1) {
        throw 'The local Query Store wait-group probe requires SQL Server 2022 (major 16).'
    }
    Invoke-ProbeSql 'master' "CREATE DATABASE [$database]" | Out-Null
    $databaseCreated = $true
    Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET QUERY_STORE = ON; ALTER DATABASE [$database] SET QUERY_STORE (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL, INTERVAL_LENGTH_MINUTES = 1440, WAIT_STATS_CAPTURE_MODE = ON)" | Out-Null
    Invoke-ProbeSql $database 'CREATE TABLE dbo.wait_probe ([value] int NOT NULL); INSERT INTO dbo.wait_probe VALUES (1),(2),(3)' | Out-Null
    Start-Sleep -Seconds 2

    $lockJob = Start-ThreadJob -ArgumentList $SqlInstance,$database -ScriptBlock {
        param($instance,$db)
        & sqlcmd -S $instance -d $db -E -b -l 5 -t 20 -Q "BEGIN TRAN; SELECT COUNT(*) FROM dbo.wait_probe WITH (TABLOCKX); WAITFOR DELAY '00:00:06'; COMMIT TRAN" 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'The disposable lock holder failed.' }
    }
    try {
        Start-Sleep -Seconds 2
        Invoke-ProbeSql $database 'SELECT SUM([value]) AS wait_probe_sum FROM dbo.wait_probe WITH (READCOMMITTEDLOCK) WHERE [value] > 0' | Out-Null
        $null = Wait-Job $lockJob -Timeout 20
        if ($lockJob.State -ne 'Completed') { throw "Disposable lock holder did not complete: $($lockJob.State)" }
        Receive-Job $lockJob | Out-Null
    }
    finally { Remove-Job $lockJob -Force -ErrorAction SilentlyContinue }
    Invoke-ProbeSql $database 'EXEC sys.sp_query_store_flush_db' | Out-Null

    $planOutput = Invoke-ProbeSql $database "SET NOCOUNT ON; SELECT TOP (1) p.plan_id, q.query_text_id FROM sys.query_store_query_text AS qt JOIN sys.query_store_query AS q ON q.query_text_id=qt.query_text_id JOIN sys.query_store_plan AS p ON p.query_id=q.query_id WHERE CHARINDEX(N'wait_probe_sum',qt.query_sql_text)>0 AND CHARINDEX(N'FROM sys.query_store_query_text',qt.query_sql_text)=0 ORDER BY p.plan_id"
    $planRows = @($planOutput | Where-Object { $_ -match '^\s*\d+\|\d+\s*$' })
    if ($planRows.Count -ne 1) { throw 'Query Store did not capture the blocked fixture plan and text.' }
    $planColumns = $planRows[0].Split('|')
    $planId = [long]::Parse($planColumns[0].Trim())
    $textId = [long]::Parse($planColumns[1].Trim())

    $directSql = "SET NOCOUNT ON; SELECT ws.runtime_stats_interval_id, ws.execution_type, CONVERT(int,ws.wait_category), CONVERT(bigint,SUM(CONVERT(decimal(38,0),ws.total_query_wait_time_ms))) FROM sys.query_store_wait_stats AS ws WHERE ws.plan_id=$planId AND ws.wait_category=3 GROUP BY ws.runtime_stats_interval_id,ws.execution_type,ws.wait_category HAVING SUM(CONVERT(decimal(38,0),ws.total_query_wait_time_ms))>0"
    $directRows = @(Invoke-ProbeSql $database $directSql | Where-Object { $_ -match '^\s*\d+\|0\|3\|[1-9]\d*\s*$' })
    if ($directRows.Count -ne 1) {
        throw "Fixture did not record one regular lock-wait group: $($directRows -join '; ')"
    }
    $directFields = $directRows[0].Split('|')

    $runtimeSql = Get-Content (Join-Path $PSScriptRoot '../../collectors/query-store-groups/runtime.sqlserver16-windows.v1.sql') -Raw
    $waitSql = Get-Content (Join-Path $PSScriptRoot '../../collectors/query-store-groups/waits.sqlserver16-windows.v1.sql') -Raw
    $sourceParameters = "DECLARE @probe_rows int=2001, @window_start datetimeoffset(7)=DATEADD(minute,-5,SYSUTCDATETIME()), @window_end datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');"
    $runtimeRows = @(Invoke-ProbeSql $database "$sourceParameters $runtimeSql" | Where-Object {
        $columns = $_.Split('|')
        $columns.Count -eq 20 -and $columns[4].Trim() -eq [string]$planId -and
            $columns[19].Trim() -eq [string]$textId -and $columns[9].Trim() -eq '0'
    })
    if ($runtimeRows.Count -ne 1) { throw 'Candidate runtime source did not return the blocked fixture plan.' }
    $runtimeFields = $runtimeRows[0].Split('|')
    $waitOutput = @(Invoke-ProbeSql $database "$sourceParameters $waitSql")
    $waitObservedAt = Read-CaptureModeAt $waitOutput 'ON'
    $waitRows = @($waitOutput | Where-Object {
        $columns = $_.Split('|')
        $columns.Count -eq 14 -and $columns[4].Trim() -eq [string]$planId -and
            $columns[10].Trim() -eq '3' -and $columns[9].Trim() -eq '0'
    })
    if ($waitRows.Count -ne 1) { throw 'Candidate wait source did not return one complete lock-wait group.' }
    $waitFields = $waitRows[0].Split('|')
    if ($waitFields[6].Trim() -ne $directFields[0].Trim() -or
        $waitFields[9].Trim() -ne $directFields[1].Trim() -or
        $waitFields[10].Trim() -ne $directFields[2].Trim() -or
        $waitFields[11].Trim() -ne $directFields[3].Trim()) {
        throw "Candidate wait group did not reconcile with direct Query Store counters: direct $($directRows[0]); candidate $($waitRows[0])"
    }
    foreach ($index in 0..9) {
        if ($waitFields[$index].Trim() -ne $runtimeFields[$index].Trim()) {
            throw "Candidate wait and runtime identities disagree at column $index."
        }
    }
    if ($waitFields[13].Trim() -ne $runtimeFields[19].Trim() -or
        [guid]::Parse($waitFields[1].Trim()) -eq [guid]::Empty -or
        [datetimeoffset]::Parse($waitFields[12].Trim()) -ne $waitObservedAt) {
        throw 'Candidate wait group has invalid query text, database or UTC observation identity.'
    }

    $probePassword = [guid]::NewGuid().ToString('N') + 'aA1!'
    Invoke-ProbeSql 'master' "CREATE LOGIN [$probeLogin] WITH PASSWORD = '$probePassword', CHECK_POLICY = OFF" | Out-Null
    $loginCreated = $true
    Invoke-ProbeSql 'master' "GRANT VIEW ANY DATABASE TO [$probeLogin]" | Out-Null
    Invoke-ProbeSql $database "CREATE USER [$probeLogin] FOR LOGIN [$probeLogin]; GRANT CONNECT TO [$probeLogin]; GRANT VIEW DATABASE PERFORMANCE STATE TO [$probeLogin]" | Out-Null
    $permissionState = Invoke-ProbeSql $database "SET NOCOUNT ON; EXECUTE AS LOGIN = N'$probeLogin'; SELECT IS_SRVROLEMEMBER(N'sysadmin'), HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DATABASE PERFORMANCE STATE'); REVERT"
    if (-not ($permissionState | Where-Object { $_ -match '^\s*0\|1\s*$' })) {
        throw 'Disposable Query Store login did not have the expected non-sysadmin read permission.'
    }
    $leastPrivilegeOutput = @(Invoke-ProbeSql $database "EXECUTE AS LOGIN = N'$probeLogin'; $sourceParameters $waitSql REVERT")
    $null = Read-CaptureModeAt $leastPrivilegeOutput 'ON'
    $leastPrivilegeRows = @($leastPrivilegeOutput | Where-Object {
        $columns = $_.Split('|')
        $columns.Count -eq 14 -and $columns[4].Trim() -eq [string]$planId -and
            $columns[10].Trim() -eq '3' -and $columns[9].Trim() -eq '0'
    })
    if ($leastPrivilegeRows.Count -ne 1) {
        throw 'Least-privilege login could not read the candidate wait group.'
    }
    $leastPrivilegeFields = $leastPrivilegeRows[0].Split('|')
    foreach ($index in @(0..11) + @(13)) {
        if ($leastPrivilegeFields[$index].Trim() -ne $waitFields[$index].Trim()) {
            throw "Least-privilege wait group changed at column $index."
        }
    }
    Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET QUERY_STORE (WAIT_STATS_CAPTURE_MODE = OFF)" | Out-Null
    $disabledOutput = @(Invoke-ProbeSql $database "$sourceParameters $waitSql")
    $null = Read-CaptureModeAt $disabledOutput 'OFF'
    if (@($disabledOutput | Where-Object { $_.Split('|').Count -eq 14 }).Count -ne 0) {
        throw 'Candidate wait source did not distinguish disabled capture from an empty enabled interval.'
    }
    Write-Output "SQL Server 2022 Query Store wait group matched runtime identity and direct lock wait $($waitFields[11].Trim()) ms for plan $planId; the non-sysadmin login returned the same group, and disabled capture was reported explicitly."
}
finally {
    try {
        if ($databaseCreated) {
            Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]" | Out-Null
            Write-Output 'Disposable Query Store wait database removed.'
        }
    }
    finally {
        if ($loginCreated) {
            Invoke-ProbeSql 'master' "DROP LOGIN [$probeLogin]" | Out-Null
            Write-Output 'Disposable Query Store wait login removed.'
        }
    }
}
