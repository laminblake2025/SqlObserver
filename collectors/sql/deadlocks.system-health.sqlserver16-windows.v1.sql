SET NOCOUNT ON;
;WITH target_limits AS
(
    SELECT TOP (1) session_info.event_session_id, MAX(CASE WHEN field_info.name = N'max_rollover_files' THEN TRY_CONVERT(bigint, CONVERT(nvarchar(64), field_info.value)) END) AS max_rollover_files, MAX(CASE WHEN field_info.name = N'max_file_size' THEN TRY_CONVERT(bigint, CONVERT(nvarchar(64), field_info.value)) END) AS max_file_size_mb
    FROM sys.server_event_sessions AS session_info INNER JOIN sys.server_event_session_targets AS configured_target ON configured_target.event_session_id = session_info.event_session_id LEFT JOIN sys.server_event_session_fields AS field_info ON field_info.event_session_id = session_info.event_session_id AND field_info.name IN (N'max_rollover_files', N'max_file_size')
    WHERE session_info.name = N'system_health' AND configured_target.target_name = N'event_file' GROUP BY session_info.event_session_id
), system_health_target AS
(
    SELECT TOP (1) CAST(target.target_data AS xml).value('(/EventFileTarget/File/@name)[1]', 'nvarchar(260)') AS target_path
    FROM sys.dm_xe_sessions AS session_info INNER JOIN sys.dm_xe_session_targets AS target ON target.event_session_address = session_info.address CROSS JOIN target_limits AS limits
    WHERE session_info.name = N'system_health' AND target.target_name = N'event_file' AND target.target_data IS NOT NULL AND limits.max_rollover_files BETWEEN 1 AND 10 AND limits.max_file_size_mb BETWEEN 1 AND 100 AND CHARINDEX(N'\', REVERSE(CAST(target.target_data AS xml).value('(/EventFileTarget/File/@name)[1]', 'nvarchar(260)'))) > 0
), source_window AS
(
    SELECT DATEADD(day, -33, SYSUTCDATETIME()) AS start_utc
), raw_events AS
(
    -- Reviewed target limits bound rollover files before the TVF; the
    -- derived system_health pattern never accepts a caller-supplied path.
    SELECT raw.event_data, DATALENGTH(raw.event_data) AS raw_bytes FROM system_health_target AS target_path CROSS JOIN source_window CROSS APPLY (SELECT CASE WHEN CHARINDEX(N'\', REVERSE(target_path.target_path)) > 0 THEN LEFT(target_path.target_path, LEN(target_path.target_path) - CHARINDEX(N'\', REVERSE(target_path.target_path)) + 1) + N'system_health*.xel' END AS file_pattern) AS source_file CROSS APPLY sys.fn_xe_file_target_read_file(source_file.file_pattern, NULL, NULL, NULL) AS raw
), bounded_raw_events AS
(
    SELECT event_data, raw_bytes FROM raw_events WHERE raw_bytes <= 1048576
), parsed_events AS
(
    SELECT TRY_CONVERT(xml, event_data) AS event_data, raw_bytes FROM bounded_raw_events
), oversized_events AS
(
    SELECT 1 AS marker FROM raw_events WHERE raw_bytes > 1048576
), deadlock_candidates AS
(
    SELECT TRY_CONVERT(datetime2(7), event_data.value('(/event/@timestamp)[1]', 'nvarchar(64)'), 127) AS occurred_at_utc, event_data, raw_bytes, CONVERT(varbinary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(max), event_data))) AS order_hash FROM parsed_events WHERE event_data IS NOT NULL AND raw_bytes <= 1048576 AND event_data.value('(/event/@name)[1]', 'sysname') = N'xml_deadlock_report'
), deadlock_rows AS
(
    SELECT TOP (@maximum_rows) occurred_at_utc, CONVERT(nvarchar(max), event_data) AS event_xml, N'ready' AS source_state, order_hash FROM deadlock_candidates CROSS JOIN source_window WHERE occurred_at_utc IS NOT NULL AND occurred_at_utc >= source_window.start_utc
    ORDER BY occurred_at_utc DESC, order_hash DESC
), oversized_sentinel AS
(
    SELECT CAST(NULL AS datetime2(7)) AS occurred_at_utc, CAST(NULL AS nvarchar(max)) AS event_xml, N'oversized' AS source_state, CAST(NULL AS varbinary(32)) AS order_hash WHERE EXISTS (SELECT 1 FROM oversized_events)
), invalid_sentinel AS
(
    SELECT CAST(NULL AS datetime2(7)) AS occurred_at_utc, CAST(NULL AS nvarchar(max)) AS event_xml, N'invalid' AS source_state, CAST(NULL AS varbinary(32)) AS order_hash WHERE EXISTS (SELECT 1 FROM parsed_events WHERE event_data IS NULL) OR EXISTS (SELECT 1 FROM deadlock_candidates WHERE occurred_at_utc IS NULL)
), empty_sentinel AS
(
    SELECT CAST(NULL AS datetime2(7)) AS occurred_at_utc, CAST(NULL AS nvarchar(max)) AS event_xml, N'empty' AS source_state, CAST(NULL AS varbinary(32)) AS order_hash WHERE EXISTS (SELECT 1 FROM system_health_target) AND NOT EXISTS (SELECT 1 FROM deadlock_rows) AND NOT EXISTS (SELECT 1 FROM oversized_events) AND NOT EXISTS (SELECT 1 FROM parsed_events WHERE event_data IS NULL)
), missing_sentinel AS
(
    SELECT CAST(NULL AS datetime2(7)) AS occurred_at_utc, CAST(NULL AS nvarchar(max)) AS event_xml, N'missing' AS source_state, CAST(NULL AS varbinary(32)) AS order_hash WHERE NOT EXISTS (SELECT 1 FROM target_limits)
), config_sentinel AS
(
    SELECT CAST(NULL AS datetime2(7)) AS occurred_at_utc, CAST(NULL AS nvarchar(max)) AS event_xml, N'config_invalid' AS source_state, CAST(NULL AS varbinary(32)) AS order_hash WHERE (NOT EXISTS (SELECT 1 FROM target_limits) AND EXISTS (SELECT 1 FROM system_health_target)) OR (EXISTS (SELECT 1 FROM target_limits) AND NOT EXISTS (SELECT 1 FROM system_health_target))
)
SELECT occurred_at_utc, event_xml, source_state FROM
(
    SELECT occurred_at_utc, event_xml, source_state, order_hash FROM deadlock_rows UNION ALL SELECT occurred_at_utc, event_xml, source_state, order_hash FROM oversized_sentinel UNION ALL SELECT occurred_at_utc, event_xml, source_state, order_hash FROM invalid_sentinel UNION ALL SELECT occurred_at_utc, event_xml, source_state, order_hash FROM empty_sentinel UNION ALL SELECT occurred_at_utc, event_xml, source_state, order_hash FROM missing_sentinel UNION ALL SELECT occurred_at_utc, event_xml, source_state, order_hash FROM config_sentinel
) AS bounded_rows ORDER BY CASE source_state WHEN N'ready' THEN 0 WHEN N'oversized' THEN 1 WHEN N'invalid' THEN 2 WHEN N'config_invalid' THEN 3 ELSE 4 END, occurred_at_utc DESC, order_hash DESC;
