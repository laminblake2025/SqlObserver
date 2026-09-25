SET NOCOUNT ON;

WITH bounded_files AS
(
    SELECT TOP (@maximum_rows + 1)
        files.database_id,
        files.file_id
    FROM sys.master_files AS files
    ORDER BY files.database_id, files.file_id
)
SELECT
    SYSUTCDATETIME() AS observed_at_utc,
    files.database_id,
    files.file_id,
    CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS')) AS physical_node,
    volume.volume_id,
    volume.volume_mount_point,
    volume.total_bytes,
    volume.available_bytes
FROM bounded_files AS files
OUTER APPLY sys.dm_os_volume_stats(files.database_id, files.file_id) AS volume
ORDER BY files.database_id, files.file_id;
