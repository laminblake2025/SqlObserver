SET NOCOUNT ON;
/* Candidate Query Store source for interval watermarks; not a production collector asset. */
/* Caller supplies @probe_rows (1..2001), @window_start and @window_end in UTC. */
DECLARE @observed_at datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
IF @probe_rows < 1 OR @probe_rows > 2001 THROW 50000, 'Invalid Query Store source row cap.', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.database_recovery_status
    WHERE database_id = DB_ID() AND database_guid IS NOT NULL
) THROW 50001, 'Query Store database identity is unavailable.', 1;

WITH database_identity AS (
    SELECT database_guid
    FROM sys.database_recovery_status
    WHERE database_id = DB_ID() AND database_guid IS NOT NULL
), source_groups AS (
    SELECT q.query_hash, q.context_settings_id, q.object_id, q.query_text_id,
           p.plan_id, p.initial_compile_start_time,
           rsi.runtime_stats_interval_id, rsi.start_time, rsi.end_time,
           rs.execution_type,
           CONVERT(bigint, SUM(rs.avg_cpu_time * rs.count_executions / 1000.0)) AS cpu_ms,
           CONVERT(bigint, SUM(rs.avg_duration * rs.count_executions / 1000.0)) AS duration_ms,
           CONVERT(bigint, SUM(CONVERT(decimal(38,0), rs.count_executions))) AS executions,
           CONVERT(bigint, SUM(rs.avg_logical_io_reads * rs.count_executions)) AS logical_reads,
           CONVERT(bigint, SUM(rs.avg_logical_io_writes * rs.count_executions)) AS writes,
           CONVERT(bigint, SUM(rs.avg_rowcount * rs.count_executions)) AS row_count,
           MIN(rs.first_execution_time) AS first_execution_time,
           MAX(rs.last_execution_time) AS last_execution_time
    FROM sys.query_store_query AS q
    JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
    JOIN sys.query_store_runtime_stats AS rs ON rs.plan_id = p.plan_id
    JOIN sys.query_store_runtime_stats_interval AS rsi
      ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
    WHERE rsi.start_time < @window_end
      AND rsi.end_time > DATEADD(minute, -5, @window_start)
    GROUP BY q.query_hash, q.context_settings_id, q.object_id, q.query_text_id,
             p.plan_id, p.initial_compile_start_time,
             rsi.runtime_stats_interval_id, rsi.start_time, rsi.end_time,
             rs.execution_type
)
SELECT TOP (@probe_rows) CONVERT(int, DB_ID()) AS database_id,
       database_identity.database_guid,
       CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max),
           CONCAT(CONVERT(varchar(20), source_groups.query_hash), ':',
                  CONVERT(varchar(20), source_groups.context_settings_id), ':',
                  CONVERT(varchar(20), source_groups.object_id)))), 2) AS query_fingerprint,
       CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(8), source_groups.plan_id)), 2) AS plan_fingerprint,
       source_groups.plan_id, source_groups.initial_compile_start_time,
       source_groups.runtime_stats_interval_id, source_groups.start_time,
       source_groups.end_time, source_groups.execution_type,
       source_groups.cpu_ms, source_groups.duration_ms, source_groups.executions,
       source_groups.logical_reads, source_groups.writes, source_groups.row_count,
       source_groups.first_execution_time, source_groups.last_execution_time,
       @observed_at AS observed_at, source_groups.query_text_id
FROM source_groups CROSS JOIN database_identity
ORDER BY source_groups.end_time DESC, source_groups.plan_id, source_groups.execution_type;
