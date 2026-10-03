SET NOCOUNT ON;

-- Detection receipts, not physical occurrence timestamps. Local startup time
-- must never acquire the current UTC offset. The node name is SQL-reported
-- identity, not an assertion of a hardware UUID.
DECLARE @observed_at_utc datetime2(6) = SYSUTCDATETIME();
DECLARE @physical_node nvarchar(128) = CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS'));
DECLARE @logical_instance nvarchar(128) = CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'));
DECLARE @process_id int = CONVERT(int, SERVERPROPERTY('ProcessID'));
DECLARE @start_local datetime, @start_ms_ticks bigint, @current_ms_ticks bigint;
DECLARE @hadr_enabled bit = CONVERT(bit, SERVERPROPERTY('IsHadrEnabled'));
SELECT @start_local = sqlserver_start_time,
       @start_ms_ticks = sqlserver_start_time_ms_ticks,
       @current_ms_ticks = ms_ticks
FROM sys.dm_os_sys_info;

;WITH local_replica AS
(
    SELECT group_id, replica_id, role_desc
    FROM sys.dm_hadr_availability_replica_states
    WHERE is_local = 1
), members AS
(
    SELECT ar.group_id, ar.replica_id, lr.replica_id AS observer_replica_id,
           rs.role_desc, lr.role_desc AS observer_role,
           CONVERT(bit, CASE WHEN rs.replica_id IS NULL THEN 0 ELSE 1 END) AS state_observed,
           COUNT(*) OVER (PARTITION BY ar.group_id) AS expected_group_members
    FROM sys.availability_replicas AS ar
    LEFT JOIN sys.dm_hadr_availability_replica_states AS rs
      ON rs.group_id = ar.group_id AND rs.replica_id = ar.replica_id
    LEFT JOIN local_replica AS lr ON lr.group_id = ar.group_id
    WHERE @hadr_enabled = 1
), evidence AS
(
    SELECT CONVERT(int, 0) AS row_kind,
           CONVERT(uniqueidentifier, NULL) AS group_id,
           CONVERT(uniqueidentifier, NULL) AS replica_id,
           CONVERT(uniqueidentifier, NULL) AS observer_replica_id,
           CONVERT(nvarchar(60), NULL) AS role_desc,
           CONVERT(nvarchar(60), NULL) AS observer_role,
           CONVERT(bit, NULL) AS state_observed,
           CONVERT(int, NULL) AS expected_group_members
    UNION ALL
    SELECT 1, group_id, replica_id, observer_replica_id,
           role_desc, observer_role, state_observed, expected_group_members
    FROM members
)
SELECT TOP (@maximum_rows)
       row_kind, @observed_at_utc AS observed_at_utc,
       @physical_node AS physical_node, @logical_instance AS logical_instance,
       @process_id AS process_id, CONVERT(datetime2(6), @start_local) AS source_start_local,
       @start_ms_ticks AS start_ms_ticks, @current_ms_ticks AS current_ms_ticks,
       @hadr_enabled AS hadr_enabled,
       group_id, replica_id, observer_replica_id, role_desc, observer_role,
       state_observed, expected_group_members, CONVERT(int, 1) AS source_schema
FROM evidence
ORDER BY row_kind, CONVERT(varchar(36), group_id), CONVERT(varchar(36), replica_id);
