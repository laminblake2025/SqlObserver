SET NOCOUNT ON;
WITH bounded AS
(
 SELECT b.*, ROW_NUMBER() OVER (PARTITION BY b.database_name,b.type ORDER BY b.backup_finish_date DESC,b.backup_set_id DESC) AS rn
 FROM msdb.dbo.backupset AS b WHERE b.backup_finish_date >= DATEADD(day,-35,GETDATE()) AND b.type IN ('D','I','L')
)
SELECT TOP (@maximum_rows) CONVERT(binary(32), HASHBYTES('SHA2_256', database_name)) AS database_fingerprint,
 CASE type WHEN 'D' THEN 1 WHEN 'I' THEN 2 WHEN 'L' THEN 3 END AS backup_kind,
 backup_finish_date AS source_local_finish, TRY_CONVERT(bigint,backup_size) AS size_bytes, is_copy_only AS copy_only,
 has_backup_checksums AS has_checksum, is_damaged AS is_damaged, TRY_CONVERT(bigint,backup_set_id) AS backup_set_id, CONVERT(smallint,NULL) AS time_zone_offset_minutes
FROM bounded WHERE rn=1 ORDER BY backup_finish_date DESC, backup_set_id DESC;
