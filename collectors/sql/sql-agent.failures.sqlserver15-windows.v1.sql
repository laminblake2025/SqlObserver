SET NOCOUNT ON;
SELECT TOP (@scan_rows) h.job_id, CONVERT(bigint,h.instance_id) AS instance_id, h.step_id, h.run_status, h.sql_message_id AS message_id, h.sql_severity,
 h.retries_attempted, h.run_duration
FROM msdb.dbo.sysjobhistory AS h
WHERE h.run_status IN (0,2,3) ORDER BY h.instance_id DESC;
