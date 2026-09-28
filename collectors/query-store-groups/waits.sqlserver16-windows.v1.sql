SET NOCOUNT ON;
/* Pinned Query Store wait-group source; publication requires the fenced interval writer. */
/* Caller supplies @probe_rows (1..2001), @window_start and @window_end in UTC. */
IF @probe_rows < 1 OR @probe_rows > 2001 THROW 50000, 'Invalid Query Store wait source row cap.', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.database_recovery_status
    WHERE database_id = DB_ID() AND database_guid IS NOT NULL
) THROW 50001, 'Query Store database identity is unavailable.', 1;
DECLARE @observed_at datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
DECLARE @wait_capture_mode varchar(32) =
    (SELECT TOP (1) wait_stats_capture_mode_desc FROM sys.database_query_store_options);
SELECT COALESCE(@wait_capture_mode, 'UNAVAILABLE') AS wait_capture_mode,
       @observed_at AS observed_at;
IF @wait_capture_mode = 'ON'
BEGIN
    WITH database_identity AS (
        SELECT database_guid
        FROM sys.database_recovery_status
        WHERE database_id = DB_ID() AND database_guid IS NOT NULL
    ), source_groups AS (
        SELECT q.query_hash, q.context_settings_id, q.object_id, q.query_text_id,
               p.plan_id, p.initial_compile_start_time,
               rsi.runtime_stats_interval_id, rsi.start_time, rsi.end_time,
               ws.execution_type, CONVERT(int, ws.wait_category) AS wait_category,
               CONVERT(bigint, SUM(CONVERT(decimal(38,0), ws.total_query_wait_time_ms))) AS wait_ms
        FROM sys.query_store_query AS q
        JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
        JOIN sys.query_store_wait_stats AS ws ON ws.plan_id = p.plan_id
        JOIN sys.query_store_runtime_stats_interval AS rsi
          ON rsi.runtime_stats_interval_id = ws.runtime_stats_interval_id
        WHERE ws.wait_category BETWEEN 0 AND 31
          AND rsi.start_time < @window_end
          AND rsi.end_time > DATEADD(minute, -5, @window_start)
        GROUP BY q.query_hash, q.context_settings_id, q.object_id, q.query_text_id,
                 p.plan_id, p.initial_compile_start_time,
                 rsi.runtime_stats_interval_id, rsi.start_time, rsi.end_time,
                 ws.execution_type, ws.wait_category
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
           source_groups.wait_category, source_groups.wait_ms,
           @observed_at AS observed_at, source_groups.query_text_id
    FROM source_groups CROSS JOIN database_identity
    ORDER BY source_groups.end_time DESC, source_groups.plan_id,
             source_groups.execution_type, source_groups.wait_category;
END;
