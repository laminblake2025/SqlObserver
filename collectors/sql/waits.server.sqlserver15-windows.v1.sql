SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    waits.wait_type,
    waits.waiting_tasks_count,
    waits.wait_time_ms,
    waits.max_wait_time_ms,
    waits.signal_wait_time_ms
FROM sys.dm_os_wait_stats AS waits
WHERE waits.wait_type IS NOT NULL
ORDER BY waits.wait_type;
