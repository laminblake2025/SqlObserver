CREATE PROCEDURE [sqlobserver_agent].[capture_v1]
    @maximum_rows int
WITH EXECUTE AS CALLER
AS
BEGIN
SET NOCOUNT ON;
DECLARE @capture_started_utc datetime2(7) = SYSUTCDATETIME();
IF @maximum_rows IS NULL OR @maximum_rows <> 513 THROW 50001, 'Invalid bounded Agent source limit.', 1;
DECLARE @before_session int, @before_start datetime;
DECLARE @services TABLE (candidate bit NOT NULL, valid_path bit NOT NULL, status int, pid int, startup datetimeoffset(7));
INSERT @services
SELECT CONVERT(bit, CASE WHEN CHARINDEX(N'sqlagent.exe', LOWER(service.filename)) > 0 THEN 1 ELSE 0 END) AS candidate,
       CONVERT(bit, CASE WHEN token.delimited = 1
          AND CHARINDEX(N'"', token.path) = 0
          AND CHARINDEX(NCHAR(9), token.path) = 0 AND CHARINDEX(NCHAR(10), token.path) = 0
          AND CHARINDEX(NCHAR(11), token.path) = 0 AND CHARINDEX(NCHAR(12), token.path) = 0
          AND CHARINDEX(NCHAR(13), token.path) = 0
          AND LOWER(RIGHT(token.path, CHARINDEX(N'/', REVERSE(token.path) + N'/') - 1)) COLLATE Latin1_General_100_BIN2 = N'sqlagent.exe'
          AND (LEFT(normalized.path, 1) = N'"' OR CHARINDEX(N' ', token.path) = 0)
          THEN 1 ELSE 0 END) AS valid_path,
       service.status, service.process_id AS pid, service.last_startup_time AS startup
FROM sys.dm_server_services AS service
CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(service.filename, N'\', N'/'))) AS path) AS normalized
CROSS APPLY (SELECT CHARINDEX(N'"', normalized.path, 2) AS closing_quote) AS quoted
CROSS APPLY (SELECT
    CASE WHEN LEFT(normalized.path, 1) = N'"' THEN
        CASE WHEN quoted.closing_quote > 1 THEN SUBSTRING(normalized.path, 2, quoted.closing_quote - 2) ELSE N'' END
        ELSE normalized.path END AS path,
    CASE WHEN LEFT(normalized.path, 1) <> N'"' THEN 1
         WHEN quoted.closing_quote > 1 AND (quoted.closing_quote = LEN(normalized.path)
           OR UNICODE(SUBSTRING(normalized.path, quoted.closing_quote + 1, 1)) IN (9,32)) THEN 1
         ELSE 0 END AS delimited) AS token
;
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
SELECT CONVERT(bit, CASE WHEN CHARINDEX(N'sqlagent.exe', LOWER(service.filename)) > 0 THEN 1 ELSE 0 END) AS candidate,
       CONVERT(bit, CASE WHEN token.delimited = 1
          AND CHARINDEX(N'"', token.path) = 0
          AND CHARINDEX(NCHAR(9), token.path) = 0 AND CHARINDEX(NCHAR(10), token.path) = 0
          AND CHARINDEX(NCHAR(11), token.path) = 0 AND CHARINDEX(NCHAR(12), token.path) = 0
          AND CHARINDEX(NCHAR(13), token.path) = 0
          AND LOWER(RIGHT(token.path, CHARINDEX(N'/', REVERSE(token.path) + N'/') - 1)) COLLATE Latin1_General_100_BIN2 = N'sqlagent.exe'
          AND (LEFT(normalized.path, 1) = N'"' OR CHARINDEX(N' ', token.path) = 0)
          THEN 1 ELSE 0 END) AS valid_path,
       service.status, service.process_id AS pid, service.last_startup_time AS startup
FROM sys.dm_server_services AS service
CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(service.filename, N'\', N'/'))) AS path) AS normalized
CROSS APPLY (SELECT CHARINDEX(N'"', normalized.path, 2) AS closing_quote) AS quoted
CROSS APPLY (SELECT
    CASE WHEN LEFT(normalized.path, 1) = N'"' THEN
        CASE WHEN quoted.closing_quote > 1 THEN SUBSTRING(normalized.path, 2, quoted.closing_quote - 2) ELSE N'' END
        ELSE normalized.path END AS path,
    CASE WHEN LEFT(normalized.path, 1) <> N'"' THEN 1
         WHEN quoted.closing_quote > 1 AND (quoted.closing_quote = LEN(normalized.path)
           OR UNICODE(SUBSTRING(normalized.path, quoted.closing_quote + 1, 1)) IN (9,32)) THEN 1
         ELSE 0 END AS delimited) AS token
;
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
END;
