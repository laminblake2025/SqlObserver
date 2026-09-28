param(
    [Parameter(Mandatory = $true)]
    [string] $SqlInstance
)

$ErrorActionPreference = 'Stop'
$database = 'SqlObserverQueryStoreProbe_' + [guid]::NewGuid().ToString('N')
$probeLogin = 'SqlObserverQueryStoreLogin_' + [guid]::NewGuid().ToString('N')
$created = $false
$loginCreated = $false

function Invoke-ProbeSql([string] $databaseName, [string] $statement, [switch] $Wide) {
    $sqlcmdArgs = @('-S', $SqlInstance, '-d', $databaseName, '-E', '-b',
        '-W', '-h', '-1', '-s', '|', '-l', '5', '-t', '20', '-Q', $statement)
    if ($Wide) { $sqlcmdArgs += @('-w', '65535') }
    $output = & sqlcmd @sqlcmdArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed in $databaseName`: $($output -join [Environment]::NewLine)" }
    return @($output)
}

function Read-WorkloadRuntimeGroup([string] $databaseName, [string] $statement,
    [long] $workloadPlanId, [long] $workloadTextId) {
    $rows = Invoke-ProbeSql $databaseName $statement
    $matched = @($rows | Where-Object {
        $columns = $_.Split('|')
        $columns.Count -eq 20 -and $columns[4].Trim() -eq [string]$workloadPlanId -and
            $columns[9].Trim() -eq '0' -and $columns[19].Trim() -eq [string]$workloadTextId
    })
    if ($matched.Count -ne 1) {
        throw "Candidate Query Store runtime source did not return exactly one workload group: $($rows -join '; ')"
    }
    $fields = $matched[0].Split('|')
    if ([guid]::Parse($fields[1].Trim()) -eq [guid]::Empty -or
        [long]::Parse($fields[6].Trim()) -le 0 -or
        [datetimeoffset]::Parse($fields[5].Trim()).Offset -ne [timespan]::Zero -or
        [datetimeoffset]::Parse($fields[7].Trim()).Offset -ne [timespan]::Zero -or
        [datetimeoffset]::Parse($fields[8].Trim()).Offset -ne [timespan]::Zero -or
        [datetimeoffset]::Parse($fields[16].Trim()).Offset -ne [timespan]::Zero -or
        [datetimeoffset]::Parse($fields[17].Trim()).Offset -ne [timespan]::Zero) {
        throw 'Candidate Query Store runtime source returned an invalid incarnation or UTC identity.'
    }
    return $fields
}

try {
    $major = Invoke-ProbeSql 'master' "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))"
    if (@($major | Where-Object { $_ -match '^\s*16\s*$' }).Count -ne 1) {
        throw 'The local Query Store text probe requires a SQL Server 2022 (major 16) instance.'
    }
    Invoke-ProbeSql 'master' "CREATE DATABASE [$database]" | Out-Null
    $created = $true
    Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET QUERY_STORE = ON; ALTER DATABASE [$database] SET QUERY_STORE (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL, INTERVAL_LENGTH_MINUTES = 1440, WAIT_STATS_CAPTURE_MODE = ON)" | Out-Null
    Invoke-ProbeSql $database 'CREATE TABLE dbo.capture_probe ([value] int NOT NULL); INSERT INTO dbo.capture_probe VALUES (1),(2),(3)' | Out-Null
    Start-Sleep -Seconds 2
    for ($index = 0; $index -lt 3; $index++) {
        Invoke-ProbeSql $database 'SELECT SUM([value]) AS probe_sum FROM [dbo].[capture_probe] WHERE [value] > 0' | Out-Null
    }
    Invoke-ProbeSql $database 'EXEC sys.sp_query_store_flush_db' | Out-Null

    $idOutput = Invoke-ProbeSql $database "SELECT TOP (1) query_text_id FROM sys.query_store_query_text WHERE CHARINDEX(N'FROM [dbo].[capture_probe]', query_sql_text) > 0 AND CHARINDEX(N'FROM sys.query_store_query_text', query_sql_text) = 0 ORDER BY query_text_id"
    $textId = [long]::Parse(($idOutput | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1).Trim())

    $metadataSql = Get-Content (Join-Path $PSScriptRoot '../../collectors/sql/queries.performance.sqlserver16-windows.v1.sql') -Raw
    $metadata = Invoke-ProbeSql $database "DECLARE @probe_rows int = 2001, @window_start datetime2(7) = DATEADD(minute,-5,SYSUTCDATETIME()), @window_end datetime2(7) = SYSUTCDATETIME(); $metadataSql"
    $workloadRows = @($metadata | Where-Object { $_ -match "\|$textId\s*$" })
    if ($workloadRows.Count -ne 1) {
        throw "Pinned Query Store metadata did not return workload query_text_id $textId. Output: $($metadata -join [Environment]::NewLine)"
    }
    $fields = $workloadRows[0].Split('|')
    if ($fields.Count -ne 18) { throw 'Pinned Query Store metadata changed its bounded row shape.' }
    $intervalStart = [datetimeoffset]::Parse($fields[11], [Globalization.CultureInfo]::InvariantCulture)
    $intervalEnd = [datetimeoffset]::Parse($fields[12], [Globalization.CultureInfo]::InvariantCulture)
    $observedAt = [datetimeoffset]::Parse($fields[13], [Globalization.CultureInfo]::InvariantCulture)
    if ($intervalStart -ge $intervalEnd -or $intervalEnd -gt $observedAt) {
        throw "Pinned Query Store interval is not a completed, increasing observation: $intervalStart / $intervalEnd / $observedAt"
    }
    $planId = [long]::Parse($fields[17], [Globalization.CultureInfo]::InvariantCulture)

    $parameters = for ($index = 0; $index -lt 32; $index++) {
        $value = if ($index -eq 0) { $textId } else { 0 }
        "DECLARE @text_id_$index bigint = $value;"
    }
    $lookupSql = Get-Content (Join-Path $PSScriptRoot '../../collectors/sql/queries.performance.text.sqlserver16-windows.v1.sql') -Raw
    $lookup = Invoke-ProbeSql $database "${parameters} $lookupSql"
    if (-not ($lookup | Where-Object { $_ -match "^\s*$textId\|.*probe_sum" })) {
        throw "Pinned Query Store text lookup did not return the workload text for ID $textId."
    }

    $planParameters = for ($index = 0; $index -lt 4; $index++) {
        $value = if ($index -eq 0) { $planId } else { 0 }
        "DECLARE @plan_id_$index bigint = $value;"
    }
    $planSql = Get-Content (Join-Path $PSScriptRoot '../../collectors/sql/queries.performance.plan.sqlserver16-windows.v1.sql') -Raw
    $planLookup = Invoke-ProbeSql $database "${planParameters} $planSql" -Wide
    if (-not ($planLookup | Where-Object { $_ -match "^\s*$planId\|.*<ShowPlanXML" })) {
        throw "Pinned Query Store plan lookup did not return Showplan XML for plan ID $planId."
    }
    # A reset can refill beyond the previous count before the next collector read.
    # Test whether the first execution time distinguishes that new counter epoch.
    $runtimeSql = "SELECT rs.runtime_stats_interval_id, CONVERT(varchar(33),MIN(rs.first_execution_time),126), CONVERT(varchar(33),MAX(rs.last_execution_time),126), SUM(CONVERT(bigint,rs.count_executions)) FROM sys.query_store_runtime_stats AS rs WHERE rs.plan_id=$planId AND rs.execution_type=0 GROUP BY rs.runtime_stats_interval_id ORDER BY rs.runtime_stats_interval_id DESC"
    $beforeRuntimeRow = @(Invoke-ProbeSql $database $runtimeSql | Where-Object { $_ -match '^\s*\d+\|' } | Select-Object -First 1)
    if ($beforeRuntimeRow.Count -ne 1) { throw "Query Store has no runtime row for workload plan $planId before reset." }
    $beforeRuntime = $beforeRuntimeRow[0].Split('|')
    if ($beforeRuntime.Count -ne 4) { throw 'Query Store runtime row changed its probe shape.' }
    $beforeCount = [long]::Parse($beforeRuntime[3].Trim(), [Globalization.CultureInfo]::InvariantCulture)
    if ($beforeCount -lt 1 -or $beforeCount -gt 20) { throw "Workload plan had an unexpected pre-reset count: $beforeCount" }
    $runtimeGroupSql = Get-Content (Join-Path $PSScriptRoot 'query-store-runtime-groups.sql') -Raw
    $sourceStatement = "DECLARE @probe_rows int=2001, @window_start datetimeoffset(7)=DATEADD(minute,-5,SYSUTCDATETIME()), @window_end datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'); $runtimeGroupSql"
    $beforeGroup = @(Read-WorkloadRuntimeGroup $database $sourceStatement $planId $textId)
    if ($beforeGroup[6].Trim() -ne $beforeRuntime[0].Trim() -or
        [long]::Parse($beforeGroup[12].Trim()) -ne $beforeCount -or
        [datetimeoffset]::Parse($beforeGroup[16].Trim()) -ne [datetimeoffset]::Parse($beforeRuntime[1].Trim()) -or
        [datetimeoffset]::Parse($beforeGroup[17].Trim()) -ne [datetimeoffset]::Parse($beforeRuntime[2].Trim())) {
        throw "Candidate Query Store runtime group did not reconcile with the direct source counter: direct $($beforeRuntime -join '|'); candidate $($beforeGroup -join '|')"
    }
    Invoke-ProbeSql $database 'SELECT SUM([value]) AS probe_sum FROM [dbo].[capture_probe] WHERE [value] > 0' | Out-Null
    $mixedRuntimeRow = @(Invoke-ProbeSql $database $runtimeSql | Where-Object { $_ -match '^\s*\d+\|' } | Select-Object -First 1)
    if ($mixedRuntimeRow.Count -ne 1) { throw 'Query Store lost the workload plan after its unflushed execution.' }
    $mixedRuntime = $mixedRuntimeRow[0].Split('|')
    $sourceRowCount = Invoke-ProbeSql $database "SELECT COUNT(*) FROM sys.query_store_runtime_stats WHERE plan_id=$planId AND runtime_stats_interval_id=$($beforeRuntime[0].Trim()) AND execution_type=0"
    $rawRows = [long]::Parse(($sourceRowCount | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1).Trim())
    $mixedGroup = @(Read-WorkloadRuntimeGroup $database $sourceStatement $planId $textId)
    if ($rawRows -lt 1 -or $mixedRuntime[0].Trim() -ne $beforeRuntime[0].Trim() -or
        [long]::Parse($mixedRuntime[3].Trim()) -ne $beforeCount + 1 -or
        [long]::Parse($mixedGroup[12].Trim()) -ne [long]::Parse($mixedRuntime[3].Trim())) {
        throw "An unflushed Query Store increment did not reconcile: raw rows $rawRows; previous $($beforeRuntime -join '|'); previous candidate $($beforeGroup -join '|'); direct $($mixedRuntime -join '|'); candidate $($mixedGroup -join '|')"
    }
    $ordinaryFirstAdvanced = [datetimeoffset]::Parse($mixedGroup[16].Trim()) -gt
        [datetimeoffset]::Parse($beforeRuntime[2].Trim())
    $ordinaryFirstChanged = [datetimeoffset]::Parse($mixedGroup[16].Trim()) -ne
        [datetimeoffset]::Parse($beforeGroup[16].Trim())
    $beforeRuntime = $mixedRuntime
    $beforeCount = [long]::Parse($mixedRuntime[3].Trim())
    $beforeGroup = $mixedGroup
    Invoke-ProbeSql $database 'EXEC sys.sp_query_store_flush_db' | Out-Null
    $flushedRuntimeRow = @(Invoke-ProbeSql $database $runtimeSql | Where-Object { $_ -match '^\s*\d+\|' } | Select-Object -First 1)
    if ($flushedRuntimeRow.Count -ne 1) { throw "Query Store lost workload plan $planId after a quiet flush." }
    $flushedRuntime = $flushedRuntimeRow[0].Split('|')
    if ($flushedRuntime[0].Trim() -ne $beforeRuntime[0].Trim() -or
        $flushedRuntime[1].Trim() -ne $beforeRuntime[1].Trim() -or
        $flushedRuntime[3].Trim() -ne $beforeRuntime[3].Trim()) {
        throw "A quiet Query Store flush changed the candidate epoch or counter: before $($beforeRuntime -join '|'); after $($flushedRuntime -join '|')"
    }
    Invoke-ProbeSql $database "EXEC sys.sp_query_store_reset_exec_stats @plan_id=$planId" | Out-Null
    Start-Sleep -Seconds 1
    $afterRuntime = $null
    $afterCount = 0L
    for ($batch = 0; $batch -lt 3 -and $afterCount -le $beforeCount; $batch++) {
        for ($index = 0; $index -lt ([Math]::Max($beforeCount + 8, 12)); $index++) {
            Invoke-ProbeSql $database 'SELECT SUM([value]) AS probe_sum FROM [dbo].[capture_probe] WHERE [value] > 0' | Out-Null
        }
        Invoke-ProbeSql $database 'EXEC sys.sp_query_store_flush_db' | Out-Null
        $afterRuntimeRow = @(Invoke-ProbeSql $database $runtimeSql | Where-Object { $_ -match '^\s*\d+\|' } | Select-Object -First 1)
        if ($afterRuntimeRow.Count -ne 1) { throw "Query Store has no runtime row for workload plan $planId after reset." }
        $afterRuntime = $afterRuntimeRow[0].Split('|')
        $afterCount = [long]::Parse($afterRuntime[3].Trim(), [Globalization.CultureInfo]::InvariantCulture)
    }
    $beforeLast = [datetimeoffset]::Parse($beforeRuntime[2].Trim(), [Globalization.CultureInfo]::InvariantCulture)
    $afterFirst = [datetimeoffset]::Parse($afterRuntime[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
    if ($afterRuntime[0].Trim() -ne $beforeRuntime[0].Trim() -or
        $afterCount -le $beforeCount -or $afterFirst -le $beforeLast) {
        throw "Query Store reset did not expose a new epoch after count refill: before $($beforeRuntime -join '|'); after $($afterRuntime -join '|')"
    }
    $afterGroup = @(Read-WorkloadRuntimeGroup $database $sourceStatement $planId $textId)
    if ($afterGroup[1].Trim() -ne $beforeGroup[1].Trim() -or
        $afterGroup[6].Trim() -ne $beforeGroup[6].Trim() -or
        [long]::Parse($afterGroup[12].Trim()) -ne $afterCount -or
        [datetimeoffset]::Parse($afterGroup[16].Trim()) -le $beforeLast) {
        throw 'Candidate Query Store runtime group did not preserve identity and expose the reset epoch.'
    }
    $lockJob = Start-ThreadJob -ArgumentList $SqlInstance,$database -ScriptBlock {
        param($instance,$db)
        & sqlcmd -S $instance -d $db -E -b -l 5 -t 20 -Q "BEGIN TRAN; SELECT COUNT(*) FROM dbo.capture_probe WITH (TABLOCKX); WAITFOR DELAY '00:00:05'; COMMIT TRAN" 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'The disposable lock holder failed.' }
    }
    try {
        Start-Sleep -Seconds 2
        $blockedElapsed = [Diagnostics.Stopwatch]::StartNew()
        Invoke-ProbeSql $database 'SELECT SUM([value]) AS blocked_probe_sum FROM dbo.capture_probe WITH (READCOMMITTEDLOCK) WHERE [value] > 0' | Out-Null
        $blockedElapsed.Stop()
        $null = Wait-Job $lockJob -Timeout 20
        if ($lockJob.State -ne 'Completed') { throw "Disposable lock holder did not complete: $($lockJob.State)" }
        Receive-Job $lockJob | Out-Null
    }
    finally { Remove-Job $lockJob -Force -ErrorAction SilentlyContinue }
    Invoke-ProbeSql $database 'EXEC sys.sp_query_store_flush_db' | Out-Null
    $blockedPlan = Invoke-ProbeSql $database "SELECT TOP (1) p.plan_id FROM sys.query_store_query_text AS qt JOIN sys.query_store_query AS q ON q.query_text_id=qt.query_text_id JOIN sys.query_store_plan AS p ON p.query_id=q.query_id WHERE CHARINDEX(N'blocked_probe_sum',qt.query_sql_text)>0 AND CHARINDEX(N'FROM sys.query_store_query_text',qt.query_sql_text)=0 ORDER BY p.plan_id"
    $blockedPlanId = [long]::Parse(($blockedPlan | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1).Trim())
    $waitParameters = for ($index = 0; $index -lt 8; $index++) {
        $value = if ($index -eq 0) { $blockedPlanId } else { 0 }
        "DECLARE @plan_id_$index bigint = $value;"
    }
    $waitSql = Get-Content (Join-Path $PSScriptRoot '../../collectors/sql/queries.performance.waits.sqlserver16-windows.v1.sql') -Raw
    $blockedWaits = Invoke-ProbeSql $database "${waitParameters} DECLARE @window_start datetime2(7)=DATEADD(minute,-5,SYSUTCDATETIME()), @window_end datetime2(7)=SYSUTCDATETIME(); $waitSql"
    $blockingCategoryRows = @($blockedWaits | Where-Object { $_ -match "^\s*$blockedPlanId\|\d+\|[1-9]\d*\s*$" })
    if (-not ($blockingCategoryRows | Where-Object { $_ -match "^\s*$blockedPlanId\|3\|[1-9]\d*\s*$" })) {
        $options = Invoke-ProbeSql $database 'SELECT actual_state_desc,wait_stats_capture_mode_desc FROM sys.database_query_store_options'
        $allWaits = Invoke-ProbeSql $database 'SELECT COUNT(*) FROM sys.query_store_wait_stats'
        throw "Query Store did not record a positive wait category for blocked plan $blockedPlanId. Elapsed: $($blockedElapsed.Elapsed.TotalSeconds)s; wait rows: $($blockedWaits -join '; '); all waits: $($allWaits -join '; '); options: $($options -join '; ')"
    }
    # The current asset is a cumulative interval snapshot. Re-reading a quiet
    # plan must not be counted as another interval's worth of waits.
    Start-Sleep -Seconds 1
    $repeatWaits = Invoke-ProbeSql $database "${waitParameters} DECLARE @window_start datetime2(7)=DATEADD(minute,-5,SYSUTCDATETIME()), @window_end datetime2(7)=SYSUTCDATETIME(); $waitSql"
    $repeatCategoryRows = @($repeatWaits | Where-Object { $_ -match "^\s*$blockedPlanId\|\d+\|[1-9]\d*\s*$" })
    if (($blockingCategoryRows -join ';') -ne ($repeatCategoryRows -join ';')) {
        throw "Query Store quiet-plan wait totals changed between adjacent reads: $($blockingCategoryRows -join '; ') / $($repeatCategoryRows -join '; ')"
    }
    $intervalRows = Invoke-ProbeSql $database "SELECT ws.runtime_stats_interval_id, ws.execution_type, CONVERT(int,ws.wait_category), CONVERT(bigint,SUM(CONVERT(decimal(38,0),ws.total_query_wait_time_ms))) FROM sys.query_store_wait_stats AS ws WHERE ws.plan_id=$blockedPlanId AND ws.wait_category=3 GROUP BY ws.runtime_stats_interval_id,ws.execution_type,ws.wait_category ORDER BY ws.runtime_stats_interval_id,ws.execution_type"
    $intervalGroups = @($intervalRows | Where-Object { $_ -match '^\s*\d+\|[034]\|3\|[1-9]\d*\s*$' })
    $intervalTotal = ($intervalGroups | ForEach-Object { [long]($_.Split('|')[3].Trim()) } | Measure-Object -Sum).Sum
    $reportedLockTotal = ($blockingCategoryRows | Where-Object { $_ -match "^\s*$blockedPlanId\|3\|[1-9]\d*\s*$" } | Select-Object -First 1).Split('|')[2].Trim()
    if ($intervalGroups.Count -lt 1 -or $intervalTotal -ne [long]$reportedLockTotal) {
        throw "Query Store interval groups did not reconcile with the pinned lock total: $($intervalGroups -join '; ') / $reportedLockTotal"
    }
    $probePassword = [guid]::NewGuid().ToString('N') + 'aA1!'
    Invoke-ProbeSql 'master' "CREATE LOGIN [$probeLogin] WITH PASSWORD = '$probePassword', CHECK_POLICY = OFF" | Out-Null
    $loginCreated = $true
    Invoke-ProbeSql 'master' "GRANT VIEW ANY DATABASE TO [$probeLogin]" | Out-Null
    Invoke-ProbeSql $database "CREATE USER [$probeLogin] FOR LOGIN [$probeLogin]; GRANT CONNECT TO [$probeLogin]; GRANT VIEW DATABASE PERFORMANCE STATE TO [$probeLogin]" | Out-Null
    $permissionState = Invoke-ProbeSql $database "EXECUTE AS LOGIN = N'$probeLogin'; SELECT IS_SRVROLEMEMBER(N'sysadmin'), HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DATABASE PERFORMANCE STATE'); REVERT"
    if (-not ($permissionState | Where-Object { $_ -match '^\s*0\|1\s*$' })) {
        throw "Disposable Query Store login did not have the expected least-privilege state: $($permissionState -join '; ')"
    }
    $privilegedGroup = @(Read-WorkloadRuntimeGroup $database $sourceStatement $planId $textId)
    $leastPrivilegeGroup = @(Read-WorkloadRuntimeGroup $database "EXECUTE AS LOGIN = N'$probeLogin'; $sourceStatement REVERT" $planId $textId)
    $sourceColumns = @(0..17) + @(19)
    if (($sourceColumns | ForEach-Object { $leastPrivilegeGroup[$_] }) -join '|' -ne
        (($sourceColumns | ForEach-Object { $privilegedGroup[$_] }) -join '|')) {
        throw "Candidate Query Store runtime source changed under the least-privilege login: privileged $($privilegedGroup -join '|'); login $($leastPrivilegeGroup -join '|')"
    }
    Write-Output "Pinned SQL Server 16 metadata, text and plan lookups returned synthetic workload IDs $textId / $planId; candidate runtime source passed the least-privilege login probe and reconciled database incarnation/interval/execution type, an unflushed increment (raw source rows $rawRows, first-execution changed: $ordinaryFirstChanged, advanced past prior last: $ordinaryFirstAdvanced), and same-interval reset $beforeCount to $afterCount; blocked plan wait rows: $($blockingCategoryRows -join '; '); interval groups: $($intervalGroups -join '; '); quiet repeat unchanged."
}
finally {
    try {
        if ($created) {
            Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]" | Out-Null
            Write-Output 'Disposable Query Store probe database removed.'
        }
    }
    finally {
        if ($loginCreated) {
            Invoke-ProbeSql 'master' "DROP LOGIN [$probeLogin]" | Out-Null
            Write-Output 'Disposable Query Store probe login removed.'
        }
    }
}
