param(
    [Parameter(Mandatory = $true)]
    [string] $SqlInstance
)

$ErrorActionPreference = 'Stop'
$database = 'SqlObserverQueryStoreProbe_' + [guid]::NewGuid().ToString('N')
$created = $false

function Invoke-ProbeSql([string] $databaseName, [string] $statement, [switch] $Wide) {
    $sqlcmdArgs = @('-S', $SqlInstance, '-d', $databaseName, '-E', '-b',
        '-W', '-h', '-1', '-s', '|', '-l', '5', '-t', '20', '-Q', $statement)
    if ($Wide) { $sqlcmdArgs += @('-w', '65535') }
    $output = & sqlcmd @sqlcmdArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed in $databaseName`: $($output -join [Environment]::NewLine)" }
    return @($output)
}

try {
    $major = Invoke-ProbeSql 'master' "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))"
    if (@($major | Where-Object { $_ -match '^\s*16\s*$' }).Count -ne 1) {
        throw 'The local Query Store text probe requires a SQL Server 2022 (major 16) instance.'
    }
    Invoke-ProbeSql 'master' "CREATE DATABASE [$database]" | Out-Null
    $created = $true
    Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET QUERY_STORE = ON; ALTER DATABASE [$database] SET QUERY_STORE (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL, INTERVAL_LENGTH_MINUTES = 1)" | Out-Null
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
    Write-Output "Pinned SQL Server 16 metadata, text and plan lookups returned synthetic workload IDs $textId / $planId."
}
finally {
    if ($created) {
        Invoke-ProbeSql 'master' "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]" | Out-Null
        Write-Output 'Disposable Query Store probe database removed.'
    }
}
