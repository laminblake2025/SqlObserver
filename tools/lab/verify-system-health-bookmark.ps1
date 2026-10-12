param(
    [Parameter(Mandatory = $true)]
    [string] $SqlInstance
)

$ErrorActionPreference = 'Stop'

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
$builder['Data Source'] = $SqlInstance
$builder['Initial Catalog'] = 'master'
$builder['Integrated Security'] = $true
$builder['Encrypt'] = $true
$builder['TrustServerCertificate'] = $false
$builder['Connect Timeout'] = 5
$builder['Application Name'] = 'SqlObserver XE Bookmark Probe'
$builder['Pooling'] = $false

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)

function Read-BufferGroups([string] $filePattern, [AllowNull()][string] $initialFileName, [AllowNull()][object] $initialOffset) {
    $command = $connection.CreateCommand()
    try {
        $command.CommandTimeout = 30
        $command.CommandText = @'
SELECT TOP (257) raw.file_name, raw.file_offset, COUNT_BIG(*) AS event_count
FROM sys.fn_xe_file_target_read_file(
    @file_pattern, NULL, @initial_file_name, @initial_offset) AS raw
GROUP BY raw.file_name, raw.file_offset
ORDER BY raw.file_name DESC, raw.file_offset DESC;
'@
        $null = $command.Parameters.Add('@file_pattern', [System.Data.SqlDbType]::NVarChar, 260)
        $command.Parameters['@file_pattern'].Value = $filePattern
        $null = $command.Parameters.Add('@initial_file_name', [System.Data.SqlDbType]::NVarChar, 260)
        $command.Parameters['@initial_file_name'].Value = if ($null -eq $initialFileName) { [DBNull]::Value } else { $initialFileName }
        $null = $command.Parameters.Add('@initial_offset', [System.Data.SqlDbType]::BigInt)
        $command.Parameters['@initial_offset'].Value = if ($null -eq $initialOffset) { [DBNull]::Value } else { [long] $initialOffset }

        $reader = $command.ExecuteReader()
        try {
            $groups = [System.Collections.Generic.List[object]]::new()
            while ($reader.Read()) {
                $groups.Add([pscustomobject]@{
                    FileName = $reader.GetString(0)
                    Offset = $reader.GetInt64(1)
                    EventCount = $reader.GetInt64(2)
                })
            }
            return $groups.ToArray()
        }
        finally { $reader.Dispose() }
    }
    finally { $command.Dispose() }
}

function Buffer-Key($group) {
    return "{0}|{1}" -f $group.FileName, $group.Offset
}

try {
    $connection.Open()
    $major = [int] $connection.ServerVersion.Split('.')[0]
    if ($major -lt 15 -or $major -gt 17) {
        throw 'The XE bookmark probe supports SQL Server 2019, 2022, and 2025 only.'
    }
    $discovery = $connection.CreateCommand()
    try {
        $discovery.CommandTimeout = 10
        $discovery.CommandText = @'
;WITH limits AS
(
    SELECT s.event_session_id,
           MAX(CASE WHEN f.name = N'max_rollover_files'
               THEN TRY_CONVERT(bigint, CONVERT(nvarchar(64), f.value)) END) AS max_rollover_files,
           MAX(CASE WHEN f.name = N'max_file_size'
               THEN TRY_CONVERT(bigint, CONVERT(nvarchar(64), f.value)) END) AS max_file_size_mb
    FROM sys.server_event_sessions AS s
    INNER JOIN sys.server_event_session_targets AS configured
        ON configured.event_session_id = s.event_session_id
    LEFT JOIN sys.server_event_session_fields AS f
        ON f.event_session_id = s.event_session_id
       AND f.object_id = configured.target_id
       AND f.name IN (N'max_rollover_files', N'max_file_size')
    WHERE s.name = N'system_health' AND configured.name = N'event_file'
    GROUP BY s.event_session_id
), running_target AS
(
    SELECT CAST(target.target_data AS xml).value(
        '(/EventFileTarget/File/@name)[1]', 'nvarchar(260)') AS target_path,
        limits.max_rollover_files, limits.max_file_size_mb
    FROM sys.dm_xe_sessions AS running_session
    INNER JOIN sys.dm_xe_session_targets AS target
        ON target.event_session_address = running_session.address
    CROSS JOIN limits
    WHERE running_session.name = N'system_health'
      AND target.target_name = N'event_file'
      AND target.target_data IS NOT NULL
)
SELECT TOP (1)
    LEFT(target_path, LEN(target_path) - CHARINDEX(N'\', REVERSE(target_path)) + 1)
        + N'system_health*.xel' AS file_pattern
FROM running_target
WHERE max_rollover_files BETWEEN 1 AND 10
  AND max_file_size_mb BETWEEN 1 AND 100
  AND CHARINDEX(N'\', REVERSE(target_path)) > 0;
'@
        $filePattern = $discovery.ExecuteScalar()
        if ($null -eq $filePattern -or $filePattern -is [DBNull] -or [string]::IsNullOrWhiteSpace([string] $filePattern)) {
            throw 'The running system_health event-file target is unavailable or exceeds the collector bounds.'
        }
        if (([string] $filePattern).Length -gt 260) {
            throw 'The system_health event-file pattern exceeds the XE reader path bound.'
        }
    }
    finally { $discovery.Dispose() }

    $full = @(Read-BufferGroups ([string] $filePattern) $null $null)
    if ($full.Count -ge 257) { throw 'More than 256 XE buffers were returned; the bounded probe is inconclusive.' }
    if ($full.Count -lt 5) { throw 'Fewer than five XE buffers are retained; the bookmark probe is inconclusive.' }
    $shared = @($full | Where-Object { $_.EventCount -gt 1 }).Count
    if ($shared -eq 0) { throw 'No retained XE buffer contains multiple events; the bookmark probe is inconclusive.' }

    # Leave the newest two buffers out of count comparison: they can grow
    # between the full and resumed reads while system_health stays active.
    $anchorIndex = -1
    for ($index = $full.Count - 2; $index -ge 3; $index--) {
        if ($full[$index].EventCount -gt 1) {
            $anchorIndex = $index
            break
        }
    }
    if ($anchorIndex -lt 0) {
        throw 'No shared-offset buffer has a stable successor; the bookmark probe is inconclusive.'
    }
    $anchor = $full[$anchorIndex]
    $resumed = @(Read-BufferGroups ([string] $filePattern) $anchor.FileName $anchor.Offset)
    if ($resumed.Count -ge 257) { throw 'The resumed read exceeded the probe bound.' }

    $resumedByKey = @{}
    foreach ($group in $resumed) {
        $key = Buffer-Key $group
        if ($resumedByKey.ContainsKey($key)) { throw 'The resumed read returned a buffer twice.' }
        $resumedByKey[$key] = $group.EventCount
    }
    foreach ($index in $anchorIndex..($full.Count - 1)) {
        if ($resumedByKey.ContainsKey((Buffer-Key $full[$index]))) {
            throw 'The resumed read replayed the anchor or an earlier retained buffer.'
        }
    }
    for ($index = 2; $index -lt $anchorIndex; $index++) {
        $key = Buffer-Key $full[$index]
        if (-not $resumedByKey.ContainsKey($key) -or $resumedByKey[$key] -ne $full[$index].EventCount) {
            throw 'The resumed read skipped or changed a stable retained buffer.'
        }
    }

    [pscustomobject]@{
        ProductMajorVersion = $major
        RetainedBuffersExamined = $full.Count
        SharedOffsetBuffers = $shared
        StableBuffersCompared = $anchorIndex - 2
        ResumedBuffers = $resumed.Count
        Result = 'buffer-granularity cursor behavior verified for retained files'
    }
}
finally { $connection.Dispose() }
