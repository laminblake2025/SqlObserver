SET NOCOUNT ON;
;WITH target_limits AS
(
    SELECT TOP (1) session_info.event_session_id, MAX(CASE WHEN field_info.name = N'max_rollover_files' THEN TRY_CONVERT(bigint, CONVERT(nvarchar(64), field_info.value)) END) AS max_rollover_files, MAX(CASE WHEN field_info.name = N'max_file_size' THEN TRY_CONVERT(bigint, CONVERT(nvarchar(64), field_info.value)) END) AS max_file_size_mb
    FROM sys.server_event_sessions AS session_info INNER JOIN sys.server_event_session_targets AS configured_target ON configured_target.event_session_id = session_info.event_session_id LEFT JOIN sys.server_event_session_fields AS field_info ON field_info.event_session_id = session_info.event_session_id AND field_info.object_id = configured_target.target_id AND field_info.name IN (N'max_rollover_files', N'max_file_size')
    WHERE session_info.name = N'system_health' AND configured_target.name = N'event_file' GROUP BY session_info.event_session_id
), system_health_target AS
(
    SELECT TOP (1) CAST(target.target_data AS xml).value('(/EventFileTarget/File/@name)[1]', 'nvarchar(260)') AS target_path
    FROM sys.dm_xe_sessions AS session_info INNER JOIN sys.dm_xe_session_targets AS target ON target.event_session_address = session_info.address CROSS JOIN target_limits AS limits
    WHERE session_info.name = N'system_health' AND target.target_name = N'event_file' AND target.target_data IS NOT NULL AND limits.max_rollover_files BETWEEN 1 AND 10 AND limits.max_file_size_mb BETWEEN 1 AND 100 AND CHARINDEX(N'\', REVERSE(CAST(target.target_data AS xml).value('(/EventFileTarget/File/@name)[1]', 'nvarchar(260)'))) > 0
), source_window AS
(
    SELECT DATEADD(day, -33, SYSUTCDATETIME()) AS start_utc
), bounded_raw_events AS
(
    -- Read the existing files once. Filter on XE metadata before returning bounded XML.
    SELECT candidate.occurred_at_utc, candidate.event_xml,
           CASE WHEN candidate.occurred_at_utc IS NULL THEN N'empty'
                WHEN candidate.raw_bytes > 1048576 THEN N'oversized' ELSE N'ready' END AS source_state
    FROM system_health_target AS target_path CROSS JOIN source_window
    CROSS APPLY (SELECT LEFT(target_path.target_path, LEN(target_path.target_path) - CHARINDEX(N'\', REVERSE(target_path.target_path)) + 1) + N'system_health*.xel' AS file_pattern) AS source_file
    OUTER APPLY
    (
        SELECT TOP (@maximum_rows) raw.timestamp_utc AS occurred_at_utc,
               CASE WHEN DATALENGTH(raw.event_data) <= 1048576 THEN raw.event_data END AS event_xml,
               DATALENGTH(raw.event_data) AS raw_bytes
        FROM sys.fn_xe_file_target_read_file(source_file.file_pattern, NULL, NULL, NULL) AS raw
        WHERE raw.object_name = N'xml_deadlock_report' AND raw.timestamp_utc >= source_window.start_utc
        ORDER BY raw.timestamp_utc DESC, raw.file_name DESC, raw.file_offset DESC
    ) AS candidate
), source_rows AS
(
    SELECT occurred_at_utc,event_xml,source_state FROM bounded_raw_events
    UNION ALL
    SELECT CAST(NULL AS datetime2(7)),CAST(NULL AS nvarchar(max)),
           CASE WHEN EXISTS (SELECT 1 FROM target_limits) THEN N'config_invalid' ELSE N'missing' END
    WHERE NOT EXISTS (SELECT 1 FROM system_health_target)
)
SELECT occurred_at_utc,event_xml,source_state FROM source_rows
ORDER BY CASE source_state WHEN N'ready' THEN 0 WHEN N'oversized' THEN 1 ELSE 2 END, occurred_at_utc DESC;
