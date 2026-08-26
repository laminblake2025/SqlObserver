SET NOCOUNT ON;
;WITH local_replica AS (SELECT ars.group_id,ars.role_desc AS local_role,CONVERT(smallint,CASE ars.role_desc WHEN 'PRIMARY' THEN 1 WHEN 'SECONDARY' THEN 2 ELSE 3 END) AS visibility_scope,CONVERT(bit,CASE WHEN ars.is_local=1 AND ars.role_desc IS NOT NULL THEN 1 ELSE 0 END) AS local_state_available FROM sys.dm_hadr_availability_replica_states AS ars WHERE ars.is_local=1), rows AS (SELECT 0 AS row_kind, CONVERT(binary(32),HASHBYTES('SHA2_256', ag.name)) AS group_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256', CONVERT(nvarchar(36),ar.replica_id))) AS replica_fingerprint,
 ars.role_desc, ars.operational_state_desc, ars.connected_state_desc, CONVERT(bit,CASE WHEN local_replica.local_state_available=1 AND ars.role_desc IS NOT NULL AND ars.operational_state_desc IS NOT NULL AND ars.connected_state_desc IS NOT NULL THEN 1 ELSE 0 END) AS state_available,
 CAST(NULL AS binary(32)) AS database_fingerprint, CAST(NULL AS nvarchar(32)) AS synchronization_state, CAST(NULL AS nvarchar(32)) AS database_state, local_replica.visibility_scope
FROM sys.availability_groups AS ag INNER JOIN sys.availability_replicas AS ar ON ar.group_id=ag.group_id LEFT JOIN sys.dm_hadr_availability_replica_states AS ars ON ars.group_id=ar.group_id AND ars.replica_id=ar.replica_id LEFT JOIN local_replica ON local_replica.group_id=ag.group_id
UNION ALL
SELECT 1, CONVERT(binary(32),HASHBYTES('SHA2_256', ag.name)), CONVERT(binary(32),HASHBYTES('SHA2_256', CONVERT(nvarchar(36),drs.group_database_id))),
 CAST(N'' AS nvarchar(32)), CAST(N'' AS nvarchar(32)), CAST(N'' AS nvarchar(32)), CONVERT(bit,CASE WHEN local_replica.local_state_available=1 AND drs.database_state_desc IS NOT NULL AND drs.synchronization_state_desc IS NOT NULL THEN 1 ELSE 0 END),
 CONVERT(binary(32),HASHBYTES('SHA2_256', CONVERT(nvarchar(32),drs.database_id))), drs.synchronization_state_desc, drs.database_state_desc, local_replica.visibility_scope
FROM sys.availability_groups AS ag LEFT JOIN sys.dm_hadr_database_replica_states AS drs ON drs.group_id=ag.group_id AND drs.is_local=1 LEFT JOIN local_replica ON local_replica.group_id=ag.group_id)
SELECT TOP (@maximum_rows) * FROM rows ORDER BY row_kind, group_fingerprint, replica_fingerprint;
