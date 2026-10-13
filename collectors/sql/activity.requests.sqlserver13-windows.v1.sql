SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    CONVERT(int, requests.session_id) AS session_id,
    requests.request_id,
    requests.status AS request_status,
    requests.command AS request_command,
    CONVERT(int, NULLIF(requests.database_id, 0)) AS database_id,
    CONVERT(bigint, requests.cpu_time) AS cpu_time_ms,
    CONVERT(bigint, requests.total_elapsed_time) AS total_elapsed_time_ms,
    requests.reads,
    requests.writes,
    requests.logical_reads,
    requests.row_count,
    CONVERT(float, CASE
        WHEN requests.percent_complete BETWEEN 0 AND 100 THEN requests.percent_complete
        ELSE 0
    END) AS percent_complete
FROM sys.dm_exec_requests AS requests
WHERE requests.session_id > 0
  AND requests.session_id <> @@SPID
ORDER BY requests.session_id, requests.request_id;
