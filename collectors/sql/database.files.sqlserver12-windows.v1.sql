SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    files.database_id,
    files.file_id,
    files.name AS logical_file_name,
    files.type_desc,
    files.state_desc,
    CONVERT(decimal(20, 0), files.size) * CONVERT(decimal(20, 0), 8192) AS size_bytes,
    CASE
        WHEN files.max_size = -1 THEN NULL
        WHEN files.max_size = 0
            THEN CONVERT(decimal(20, 0), files.size) * CONVERT(decimal(20, 0), 8192)
        ELSE CONVERT(decimal(20, 0), files.max_size) * CONVERT(decimal(20, 0), 8192)
    END AS max_size_bytes,
    files.growth AS growth_value,
    files.is_percent_growth,
    COALESCE(io.num_of_reads, 0) AS reads_total,
    COALESCE(io.num_of_writes, 0) AS writes_total,
    COALESCE(io.num_of_bytes_read, 0) AS bytes_read_total,
    COALESCE(io.num_of_bytes_written, 0) AS bytes_written_total,
    COALESCE(io.io_stall_read_ms, 0) AS read_stall_ms_total,
    COALESCE(io.io_stall_write_ms, 0) AS write_stall_ms_total
FROM sys.master_files AS files
LEFT JOIN sys.dm_io_virtual_file_stats(NULL, NULL) AS io
  ON io.database_id = files.database_id
 AND io.file_id = files.file_id
ORDER BY files.database_id, files.file_id;
