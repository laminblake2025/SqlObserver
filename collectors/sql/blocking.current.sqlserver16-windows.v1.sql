SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    CONVERT(int, waiting.session_id) AS blocked_session_id,
    CONVERT(int, waiting.blocking_session_id) AS blocking_session_id,
    waiting.wait_type,
    COUNT_BIG(*) AS waiting_task_count,
    MAX(CONVERT(bigint, waiting.wait_duration_ms)) AS wait_duration_ms
FROM sys.dm_os_waiting_tasks AS waiting
WHERE waiting.session_id > 0
  AND waiting.session_id <> @@SPID
  AND waiting.blocking_session_id <> 0
  AND waiting.blocking_session_id <> waiting.session_id
  AND waiting.blocking_session_id <> @@SPID
  AND waiting.wait_type IS NOT NULL
GROUP BY waiting.session_id, waiting.blocking_session_id, waiting.wait_type
ORDER BY waiting.session_id, waiting.blocking_session_id, waiting.wait_type;
