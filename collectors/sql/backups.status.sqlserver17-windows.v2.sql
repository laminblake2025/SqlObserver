SET NOCOUNT ON;
IF @maximum_rows IS NULL OR @maximum_rows < 1 OR @maximum_rows > 3073
 OR @maximum_databases IS NULL OR @maximum_databases < 1 OR @maximum_databases > 513
 THROW 51007,'backups.status v2 source bounds are invalid.',1;
IF COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DATABASE'),0) <> 1
 THROW 51005,'VIEW ANY DATABASE is required for database inventory.',1;

DECLARE @hadr_enabled int=CONVERT(int,SERVERPROPERTY('IsHadrEnabled'));
DECLARE @source_observed_utc datetime2(6)=SYSUTCDATETIME();
DECLARE @history_lower_bound_local datetime=DATEADD(day,-35,GETDATE());
DECLARE @source_time_zone_id nvarchar(128)=CURRENT_TIMEZONE_ID();
IF @hadr_enabled=1
BEGIN
 IF COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE'),0) <> 1
  THROW 51006,'AG metadata and server state visibility are required for local replica context.',1;
END;

;WITH local_replicas AS
(
 SELECT group_id,replica_id,role_desc
 FROM sys.dm_hadr_availability_replica_states WHERE is_local=1 AND @hadr_enabled=1
), bounded_databases AS
(
 SELECT TOP (@maximum_databases)
  d.database_id,d.name,d.recovery_model_desc,d.state_desc,d.group_database_id,d.replica_id,
  rs.database_guid,rs.family_guid,rs.recovery_fork_guid
 FROM sys.databases AS d
 LEFT JOIN sys.database_recovery_status AS rs ON rs.database_id=d.database_id
 WHERE d.database_id<>2
 ORDER BY d.database_id
), inventory AS
(
 SELECT d.*,
  COUNT(*) OVER(PARTITION BY database_guid) AS database_guid_count,
  lr.group_id,lr.role_desc,
  CONVERT(bit,CASE WHEN d.group_database_id IS NOT NULL AND lr.replica_id IS NOT NULL
   THEN sys.fn_hadr_backup_is_preferred_replica(d.name) ELSE NULL END) AS local_replica_preferred
 FROM bounded_databases AS d
 LEFT JOIN local_replicas AS lr ON lr.replica_id=d.replica_id
), ranked_backups AS
(
 SELECT d.database_id,b.type,b.is_copy_only,b.backup_start_date,b.backup_finish_date,
  b.time_zone,b.backup_size,b.has_backup_checksums,b.is_damaged,b.has_incomplete_metadata,
  b.first_recovery_fork_guid,b.last_recovery_fork_guid,
  ROW_NUMBER() OVER(PARTITION BY d.database_id,b.type,b.is_copy_only
   ORDER BY CASE WHEN b.backup_finish_date IS NULL THEN 0 ELSE 1 END,
    b.backup_finish_date DESC,b.backup_set_id DESC) AS slot_rank
 FROM inventory AS d
 INNER JOIN msdb.dbo.backupset AS b
  ON b.database_guid=d.database_guid AND b.family_guid=d.family_guid
   AND b.last_recovery_fork_guid=d.recovery_fork_guid
 WHERE d.database_guid IS NOT NULL AND d.database_guid_count=1
  AND (b.backup_finish_date>=@history_lower_bound_local OR b.backup_finish_date IS NULL
   AND (b.backup_start_date IS NULL OR b.backup_start_date>=@history_lower_bound_local))
  AND b.type IN ('D','I','L')
), unclassified AS
(
 SELECT DISTINCT database_id FROM ranked_backups
 WHERE is_copy_only IS NULL OR type='I' AND is_copy_only=1
), streams AS
(
 SELECT CONVERT(int,0) AS row_kind,d.database_id,
  CONVERT(char(1),NULL) AS backup_type,CONVERT(bit,NULL) AS copy_only,
  CONVERT(datetime,NULL) AS source_local_start,CONVERT(datetime,NULL) AS source_local_finish,
  CONVERT(smallint,NULL) AS recorded_start_offset_minutes,CONVERT(bigint,NULL) AS size_bytes,
  CONVERT(bit,NULL) AS has_checksum,CONVERT(bit,NULL) AS is_damaged,
  CONVERT(bit,NULL) AS metadata_complete,
  CONVERT(uniqueidentifier,NULL) AS first_recovery_fork_guid,
  CONVERT(uniqueidentifier,NULL) AS last_recovery_fork_guid
 FROM inventory AS d
 UNION ALL
 SELECT 1,b.database_id,b.type,b.is_copy_only,b.backup_start_date,b.backup_finish_date,
  CONVERT(smallint,CASE WHEN b.time_zone BETWEEN -48 AND 48 THEN b.time_zone*15 ELSE NULL END),
  TRY_CONVERT(bigint,b.backup_size),b.has_backup_checksums,b.is_damaged,
  CONVERT(bit,CASE WHEN b.has_incomplete_metadata=0 AND b.first_recovery_fork_guid IS NOT NULL
   AND b.last_recovery_fork_guid IS NOT NULL THEN 1
   WHEN b.has_incomplete_metadata=1 THEN 0 ELSE NULL END),
  b.first_recovery_fork_guid,b.last_recovery_fork_guid
 FROM ranked_backups AS b WHERE b.slot_rank=1 AND b.is_copy_only IS NOT NULL
  AND (b.type<>'I' OR b.is_copy_only=0)
)
SELECT TOP (@maximum_rows)
 s.row_kind,d.database_id AS catalog_database_id,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),d.database_guid))) AS database_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),d.family_guid))) AS family_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),d.recovery_fork_guid))) AS recovery_fork_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),d.group_id))) AS group_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),d.group_database_id))) AS ag_database_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),d.replica_id))) AS local_replica_fingerprint,
 CONVERT(bit,CASE WHEN d.database_guid IS NOT NULL AND d.database_guid_count=1 THEN 1 ELSE 0 END) AS database_identity_known,
 CONVERT(nvarchar(60),d.recovery_model_desc) AS recovery_model_desc,
 CONVERT(nvarchar(60),d.state_desc) AS catalog_database_state_desc,
 CONVERT(smallint,CASE WHEN d.group_database_id IS NOT NULL OR d.replica_id IS NOT NULL OR d.group_id IS NOT NULL
  THEN 2 WHEN @hadr_enabled IN (0,1) THEN 1 ELSE 0 END) AS visibility_scope,
 CONVERT(nvarchar(60),d.role_desc) AS local_replica_role_desc,d.local_replica_preferred,
 CASE s.backup_type WHEN 'D' THEN 1 WHEN 'I' THEN 2 WHEN 'L' THEN 3 ELSE NULL END AS backup_kind,
 s.copy_only,s.source_local_start,s.source_local_finish,s.recorded_start_offset_minutes,
 s.size_bytes,s.has_checksum,s.is_damaged,s.metadata_complete,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),s.first_recovery_fork_guid))) AS backup_first_recovery_fork_fingerprint,
 CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(16),s.last_recovery_fork_guid))) AS backup_last_recovery_fork_fingerprint,
 @source_observed_utc AS source_observed_utc,@source_time_zone_id AS source_time_zone_id,
 @history_lower_bound_local AS history_lower_bound_local,
 CONVERT(bit,CASE WHEN u.database_id IS NULL THEN 0 ELSE 1 END) AS unclassified_backup_seen
FROM streams AS s
INNER JOIN inventory AS d ON d.database_id=s.database_id
LEFT JOIN unclassified AS u ON u.database_id=d.database_id
ORDER BY d.database_id,s.row_kind,backup_kind,s.copy_only;
