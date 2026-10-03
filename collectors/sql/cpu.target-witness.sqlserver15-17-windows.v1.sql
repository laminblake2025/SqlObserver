SET NOCOUNT ON;

-- One receipt on the same owned connection before/after native sampling.
-- The SQL node name and PID do not prove local Windows process association.
-- Process counters remain exact cumulative milliseconds, never OS CPU percent.
SELECT SYSUTCDATETIME() AS observed_at_utc,
       CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS')) AS physical_node,
       CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS logical_instance,
       CONVERT(int, SERVERPROPERTY('ProcessID')) AS process_id,
       CONVERT(datetime2(7), sqlserver_start_time) AS source_start_local,
       sqlserver_start_time_ms_ticks AS start_ms_ticks,
       ms_ticks AS current_ms_ticks,
       process_user_time_ms,
       process_kernel_time_ms,
       cpu_count AS sql_reported_logical_processors,
       CONVERT(nvarchar(40), CONNECTIONPROPERTY('physical_net_transport')) AS physical_transport,
       CONVERT(int, 1) AS source_schema
FROM sys.dm_os_sys_info;
