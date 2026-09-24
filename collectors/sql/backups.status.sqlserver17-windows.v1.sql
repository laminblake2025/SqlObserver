SET NOCOUNT ON;
IF COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DATABASE'),0) <> 1
 THROW 51005,'VIEW ANY DATABASE is required for complete backup status.',1;
WITH current_databases AS
(
 SELECT d.name AS database_name, rs.database_guid
 FROM sys.databases AS d
 LEFT JOIN sys.database_recovery_status AS rs ON rs.database_id=d.database_id
 WHERE d.database_id <> 2
),
bounded AS
(
 SELECT b.*, ROW_NUMBER() OVER (PARTITION BY b.database_guid,b.type ORDER BY b.backup_finish_date DESC,b.backup_set_id DESC) AS rn
 FROM msdb.dbo.backupset AS b
 WHERE b.backup_finish_date >= DATEADD(day,-35,GETDATE()) AND b.type IN ('D','I','L')
 AND b.database_guid IS NOT NULL AND (b.type <> 'D' OR b.is_copy_only = 0)
),
requested AS
(
 SELECT d.database_name,d.database_guid,CONVERT(char(1),'D') AS backup_type FROM current_databases AS d
 UNION ALL
 SELECT d.database_name,d.database_guid,b.type FROM current_databases AS d
 JOIN bounded AS b ON b.database_guid=d.database_guid AND b.rn=1 AND b.type IN ('I','L')
)
SELECT TOP (@maximum_rows)
 CONVERT(binary(32),HASHBYTES('SHA2_256',CASE WHEN k.database_guid IS NULL THEN CONVERT(varbinary(256),k.database_name) ELSE CONVERT(varbinary(16),k.database_guid) END)) AS database_fingerprint,
 CASE k.backup_type WHEN 'D' THEN 1 WHEN 'I' THEN 2 WHEN 'L' THEN 3 END AS backup_kind,
 b.backup_finish_date AS source_local_finish, TRY_CONVERT(bigint,backup_size) AS size_bytes, b.is_copy_only AS copy_only,
 b.has_backup_checksums AS has_checksum, b.is_damaged AS is_damaged, TRY_CONVERT(bigint,backup_set_id) AS backup_set_id,
 CONVERT(smallint,CASE WHEN b.time_zone BETWEEN -48 AND 48 THEN b.time_zone * 15 ELSE NULL END) AS time_zone_offset_minutes,
 CONVERT(bit,CASE WHEN k.database_guid IS NULL THEN 1 ELSE 0 END) AS database_identity_unknown
FROM requested AS k
LEFT JOIN bounded AS b ON b.database_guid=k.database_guid AND b.type=k.backup_type AND b.rn=1
ORDER BY b.backup_finish_date DESC,b.backup_set_id DESC,k.database_name,k.backup_type;
