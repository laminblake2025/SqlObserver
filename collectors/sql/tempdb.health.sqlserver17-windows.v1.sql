SET NOCOUNT ON;
USE tempdb;
SELECT TOP (@maximum_rows) df.file_id, CONVERT(bigint,df.size) * CONVERT(bigint,8192) AS size_bytes, CONVERT(bigint,FILEPROPERTY(df.name,'SpaceUsed')) * CONVERT(bigint,8192) AS used_bytes,
 (CONVERT(bigint,df.size)-CONVERT(bigint,ISNULL(FILEPROPERTY(df.name,'SpaceUsed'),0))) * CONVERT(bigint,8192) AS free_bytes,
 CONVERT(bigint,(SELECT SUM(total_log_size_in_bytes) FROM sys.dm_db_log_space_usage)) AS log_total_bytes,
 CONVERT(bigint,(SELECT SUM(used_log_space_in_bytes) FROM sys.dm_db_log_space_usage)) AS log_used_bytes
FROM sys.database_files AS df WHERE CONVERT(bigint,df.size) <= (9223372036854775807 / 8192) ORDER BY df.file_id;
