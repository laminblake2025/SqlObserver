SET NOCOUNT ON;

-- One source receipt on the owned encrypted, integrated, MARS-disabled connection.
-- Keep every current-session row: the reader rejects ambiguity rather than TOP 1.
-- A missing/non-TCP connection retains engine evidence without socket ownership.
SELECT SYSUTCDATETIME() AS observed_at_utc,
       CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS')) AS physical_node,
       CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS logical_instance,
       CONVERT(int, SERVERPROPERTY('ProcessID')) AS process_id,
       CONVERT(datetime2(7), i.sqlserver_start_time) AS source_start_local,
       i.sqlserver_start_time_ms_ticks AS start_ms_ticks,
       i.ms_ticks AS current_ms_ticks,
       i.process_user_time_ms,
       i.process_kernel_time_ms,
       i.cpu_count AS sql_reported_logical_processors,
       CONVERT(nvarchar(40), CONNECTIONPROPERTY('physical_net_transport')) AS physical_transport,
       CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL' AND c.parent_connection_id IS NULL
            THEN CONVERT(nvarchar(48), c.local_net_address) END AS server_address,
       CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL' AND c.parent_connection_id IS NULL
            THEN c.local_tcp_port END AS server_port,
       CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL' AND c.parent_connection_id IS NULL
            THEN CONVERT(nvarchar(48), c.client_net_address) END AS client_address,
       CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL' AND c.parent_connection_id IS NULL
            THEN c.client_tcp_port END AS client_port,
       CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL' AND c.parent_connection_id IS NULL
            THEN c.connection_id END AS server_connection_id,
       CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL' AND c.parent_connection_id IS NULL
            THEN CONVERT(int, c.session_id) END AS server_session_id,
       CONVERT(int, 2) AS source_schema
FROM sys.dm_os_sys_info AS i
LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = @@SPID;
