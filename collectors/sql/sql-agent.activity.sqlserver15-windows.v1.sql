-- Staged passive source only. Requires VIEW SERVER STATE (15),
-- VIEW SERVER SECURITY STATE (16/17), and explicit msdb table SELECT visibility.
-- No Agent role/sysadmin grant, executable path/account projection or duration inference.
SET NOCOUNT ON;
DECLARE @capture_started_utc datetime2(7) = SYSUTCDATETIME();
IF @maximum_rows <> 513 THROW 50001, 'Invalid bounded Agent source limit.', 1;
DECLARE @before_session int, @before_start datetime;
DECLARE @services TABLE (candidate bit NOT NULL, valid_path bit NOT NULL, status int, pid int, startup datetimeoffset(7));
INSERT @services
SELECT CASE WHEN CHARINDEX(N'sqlagent.exe', LOWER(filename)) > 0 THEN 1 ELSE 0 END,
       CASE WHEN LOWER(RIGHT(p.path, CHARINDEX(N'/', REVERSE(p.path) + N'/') - 1)) COLLATE Latin1_General_100_BIN2 = N'sqlagent.exe'
                 AND CHARINDEX(N'"', p.path) = 0 THEN 1 ELSE 0 END,
       status, process_id, last_startup_time
FROM sys.dm_server_services
CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(filename, N'\', N'/'))) AS path) AS n
CROSS APPLY (SELECT CASE WHEN LEN(n.path) >= 2 AND LEFT(n.path, 1) = N'"' AND RIGHT(n.path, 1) = N'"'
                          THEN SUBSTRING(n.path, 2, LEN(n.path) - 2) ELSE n.path END AS path) AS p;
SELECT TOP (1) @before_session = session_id, @before_start = agent_start_date
FROM msdb.dbo.syssessions ORDER BY session_id DESC;
SELECT @capture_started_utc AS source_observed_utc,
       COUNT(CASE WHEN candidate = 1 THEN 1 END) AS service_match_count,
       CASE WHEN COUNT(CASE WHEN candidate = 1 THEN 1 END) = 1 THEN MAX(CASE WHEN candidate = 1 AND valid_path = 1 THEN status END) END AS service_status,
       CASE WHEN COUNT(CASE WHEN candidate = 1 THEN 1 END) = 1 THEN MAX(CASE WHEN candidate = 1 AND valid_path = 1 THEN pid END) END AS service_process_id,
       CASE WHEN COUNT(CASE WHEN candidate = 1 THEN 1 END) = 1 THEN MAX(CASE WHEN candidate = 1 AND valid_path = 1 THEN startup END) END AS service_startup_utc,
       @before_session AS agent_session_id, @before_start AS agent_start_local, CAST(1 AS int) AS source_schema
FROM @services;
SELECT TOP (@maximum_rows) job_id AS job_id, session_id AS agent_session_id,
       run_requested_date AS requested_local, queued_date AS queued_local,
       start_execution_date AS start_execution_local, stop_execution_date AS stop_execution_local,
       last_executed_step_id AS last_executed_step_id, last_executed_step_date AS last_executed_step_local
FROM msdb.dbo.sysjobactivity
WHERE session_id = @before_session
ORDER BY CONVERT(char(36), job_id) COLLATE Latin1_General_100_BIN2;
DELETE FROM @services;
INSERT @services
SELECT CASE WHEN CHARINDEX(N'sqlagent.exe', LOWER(filename)) > 0 THEN 1 ELSE 0 END,
       CASE WHEN LOWER(RIGHT(p.path, CHARINDEX(N'/', REVERSE(p.path) + N'/') - 1)) COLLATE Latin1_General_100_BIN2 = N'sqlagent.exe'
                 AND CHARINDEX(N'"', p.path) = 0 THEN 1 ELSE 0 END,
       status, process_id, last_startup_time
FROM sys.dm_server_services
CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(filename, N'\', N'/'))) AS path) AS n
CROSS APPLY (SELECT CASE WHEN LEN(n.path) >= 2 AND LEFT(n.path, 1) = N'"' AND RIGHT(n.path, 1) = N'"'
                          THEN SUBSTRING(n.path, 2, LEN(n.path) - 2) ELSE n.path END AS path) AS p;
DECLARE @after_session int, @after_start datetime;
SELECT TOP (1) @after_session = session_id, @after_start = agent_start_date
FROM msdb.dbo.syssessions ORDER BY session_id DESC;
SELECT SYSUTCDATETIME() AS source_observed_utc,
       COUNT(CASE WHEN candidate = 1 THEN 1 END) AS service_match_count,
       CASE WHEN COUNT(CASE WHEN candidate = 1 THEN 1 END) = 1 THEN MAX(CASE WHEN candidate = 1 AND valid_path = 1 THEN status END) END AS service_status,
       CASE WHEN COUNT(CASE WHEN candidate = 1 THEN 1 END) = 1 THEN MAX(CASE WHEN candidate = 1 AND valid_path = 1 THEN pid END) END AS service_process_id,
       CASE WHEN COUNT(CASE WHEN candidate = 1 THEN 1 END) = 1 THEN MAX(CASE WHEN candidate = 1 AND valid_path = 1 THEN startup END) END AS service_startup_utc,
       @after_session AS agent_session_id, @after_start AS agent_start_local, CAST(1 AS int) AS source_schema
FROM @services;
