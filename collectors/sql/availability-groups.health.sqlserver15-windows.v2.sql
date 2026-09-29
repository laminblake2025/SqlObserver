SET NOCOUNT ON;

;WITH local_replica AS
(
    SELECT group_id, role_desc
    FROM sys.dm_hadr_availability_replica_states
    WHERE is_local = 1
), evidence AS
(
    SELECT
        CONVERT(int, 0) AS row_kind,
        CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(36), ag.group_id))) AS group_fingerprint,
        CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(36), ar.replica_id))) AS replica_fingerprint,
        CONVERT(binary(32), NULL) AS database_fingerprint,
        CONVERT(smallint, CASE lr.role_desc WHEN 'PRIMARY' THEN 1 WHEN 'SECONDARY' THEN 2 ELSE 3 END) AS visibility_scope,
        CONVERT(bit, CASE WHEN ars.replica_id IS NOT NULL THEN 1 ELSE 0 END) AS state_observed,
        ars.role_desc, ars.operational_state_desc, ars.connected_state_desc,
        CONVERT(nvarchar(32), NULL) AS synchronization_state_desc,
        CONVERT(nvarchar(32), NULL) AS synchronization_health_desc,
        CONVERT(nvarchar(32), NULL) AS database_state_desc,
        CONVERT(bit, NULL) AS is_suspended,
        CONVERT(bigint, NULL) AS log_send_queue_kb,
        CONVERT(bigint, NULL) AS log_send_rate_kb_per_second,
        CONVERT(bigint, NULL) AS redo_queue_kb,
        CONVERT(bigint, NULL) AS redo_rate_kb_per_second,
        CONVERT(datetime, NULL) AS source_last_commit_local
    FROM sys.availability_groups AS ag
    INNER JOIN sys.availability_replicas AS ar ON ar.group_id = ag.group_id
    LEFT JOIN sys.dm_hadr_availability_replica_states AS ars
        ON ars.group_id = ar.group_id AND ars.replica_id = ar.replica_id
    LEFT JOIN local_replica AS lr ON lr.group_id = ag.group_id

    UNION ALL

    SELECT
        CONVERT(int, 1),
        CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(36), ag.group_id))),
        CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(36), ar.replica_id))),
        CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(36), adc.group_database_id))),
        CONVERT(smallint, CASE lr.role_desc WHEN 'PRIMARY' THEN 1 WHEN 'SECONDARY' THEN 2 ELSE 3 END),
        CONVERT(bit, CASE WHEN drs.replica_id IS NOT NULL THEN 1 ELSE 0 END),
        CONVERT(nvarchar(32), NULL), CONVERT(nvarchar(32), NULL), CONVERT(nvarchar(32), NULL),
        drs.synchronization_state_desc, drs.synchronization_health_desc, drs.database_state_desc,
        drs.is_suspended, drs.log_send_queue_size, drs.log_send_rate,
        drs.redo_queue_size, drs.redo_rate, drs.last_commit_time
    FROM sys.availability_groups AS ag
    INNER JOIN sys.availability_replicas AS ar ON ar.group_id = ag.group_id
    INNER JOIN sys.availability_databases_cluster AS adc ON adc.group_id = ag.group_id
    LEFT JOIN sys.dm_hadr_database_replica_states AS drs
        ON drs.group_id = ag.group_id AND drs.replica_id = ar.replica_id
        AND drs.group_database_id = adc.group_database_id
    LEFT JOIN local_replica AS lr ON lr.group_id = ag.group_id
)
SELECT TOP (@maximum_rows)
    row_kind, group_fingerprint, replica_fingerprint, database_fingerprint,
    visibility_scope, state_observed, role_desc, operational_state_desc,
    connected_state_desc, synchronization_state_desc, synchronization_health_desc,
    database_state_desc, is_suspended, log_send_queue_kb,
    log_send_rate_kb_per_second, redo_queue_kb, redo_rate_kb_per_second,
    source_last_commit_local
FROM evidence
ORDER BY row_kind, group_fingerprint, replica_fingerprint, database_fingerprint;
