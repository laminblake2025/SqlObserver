SET NOCOUNT ON;

WITH metric_values AS
(
    SELECT N'engine.batch_requests_total' AS metric_id, CONVERT(float, MAX(pc.cntr_value)) AS metric_value
    FROM sys.dm_os_performance_counters AS pc
    WHERE RTRIM(pc.object_name) LIKE N'%:SQL Statistics'
      AND RTRIM(pc.counter_name) = N'Batch Requests/sec'

    UNION ALL

    SELECT N'engine.sql_compilations_total', CONVERT(float, MAX(pc.cntr_value))
    FROM sys.dm_os_performance_counters AS pc
    WHERE RTRIM(pc.object_name) LIKE N'%:SQL Statistics'
      AND RTRIM(pc.counter_name) = N'SQL Compilations/sec'

    UNION ALL

    SELECT N'engine.sql_recompilations_total', CONVERT(float, MAX(pc.cntr_value))
    FROM sys.dm_os_performance_counters AS pc
    WHERE RTRIM(pc.object_name) LIKE N'%:SQL Statistics'
      AND RTRIM(pc.counter_name) = N'SQL Re-Compilations/sec'

    UNION ALL

    SELECT N'engine.page_life_expectancy_seconds', CONVERT(float, MAX(pc.cntr_value))
    FROM sys.dm_os_performance_counters AS pc
    WHERE RTRIM(pc.object_name) LIKE N'%:Buffer Manager'
      AND RTRIM(pc.counter_name) = N'Page life expectancy'

    UNION ALL

    SELECT N'engine.user_connections', CONVERT(float, MAX(pc.cntr_value))
    FROM sys.dm_os_performance_counters AS pc
    WHERE RTRIM(pc.object_name) LIKE N'%:General Statistics'
      AND RTRIM(pc.counter_name) = N'User Connections'

    UNION ALL

    SELECT N'engine.process_physical_memory_bytes', CONVERT(float, memory.physical_memory_in_use_kb) * 1024.0
    FROM sys.dm_os_process_memory AS memory

    UNION ALL

    SELECT N'engine.os_available_memory_bytes', CONVERT(float, memory.available_physical_memory_kb) * 1024.0
    FROM sys.dm_os_sys_memory AS memory

    UNION ALL

    SELECT N'engine.memory_grants_pending', CONVERT(float, COUNT_BIG(*))
    FROM sys.dm_exec_query_memory_grants AS grant_request
    WHERE grant_request.grant_time IS NULL

    UNION ALL

    SELECT N'engine.scheduler_runnable_tasks', CONVERT(float, COALESCE(SUM(CONVERT(bigint, scheduler.runnable_tasks_count)), 0))
    FROM sys.dm_os_schedulers AS scheduler
    WHERE scheduler.status = N'VISIBLE ONLINE'
      AND scheduler.scheduler_id < 1048576

    UNION ALL

    SELECT N'engine.committed_memory_bytes', CONVERT(float, info.committed_kb) * 1024.0
    FROM sys.dm_os_sys_info AS info

    UNION ALL

    -- Stable local startup identity, used only to detect counter epochs (not a UTC timestamp).
    SELECT N'engine.start_time_key', CONVERT(float, DATEDIFF_BIG(SECOND, CONVERT(datetime2, '20000101', 112), info.sqlserver_start_time))
    FROM sys.dm_os_sys_info AS info

    UNION ALL

    SELECT N'engine.target_memory_bytes', CONVERT(float, info.committed_target_kb) * 1024.0
    FROM sys.dm_os_sys_info AS info
)
SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    metric_id,
    metric_value
FROM metric_values
WHERE metric_value IS NOT NULL
  AND metric_value >= 0
ORDER BY metric_id;
