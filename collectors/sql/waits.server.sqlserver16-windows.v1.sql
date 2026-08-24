SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    RTRIM(waits.wait_type) AS wait_type,
    waits.waiting_tasks_count,
    waits.wait_time_ms,
    waits.max_wait_time_ms,
    waits.signal_wait_time_ms
FROM sys.dm_os_wait_stats AS waits
WHERE waits.wait_type IS NOT NULL
  AND LEN(RTRIM(waits.wait_type)) BETWEEN 1 AND 120
  AND RTRIM(waits.wait_type) COLLATE Latin1_General_100_BIN2 LIKE N'[A-Za-z]%'
  AND RTRIM(waits.wait_type) COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^A-Za-z0-9_]%'
ORDER BY RTRIM(waits.wait_type);
