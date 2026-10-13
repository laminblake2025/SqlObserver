SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    CONVERT(int, sessions.session_id) AS session_id,
    sessions.status AS session_status,
    sessions.is_user_process,
    CONVERT(int, NULLIF(sessions.database_id, 0)) AS database_id,
    sessions.open_transaction_count,
    CONVERT(bigint, sessions.cpu_time) AS cpu_time_ms,
    CONVERT(bigint, sessions.memory_usage) AS memory_usage_pages,
    sessions.reads,
    sessions.writes,
    sessions.logical_reads,
    CONVERT(bigint, sessions.total_elapsed_time) AS total_elapsed_time_ms
FROM sys.dm_exec_sessions AS sessions
WHERE sessions.session_id > 0
  AND sessions.session_id <> @@SPID
ORDER BY sessions.session_id;
