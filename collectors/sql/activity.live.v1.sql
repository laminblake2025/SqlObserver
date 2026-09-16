SET NOCOUNT ON;
SET LOCK_TIMEOUT 1000;
IF TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) NOT IN (15,16,17)
 THROW 51000, 'Unsupported live activity engine version', 1;
IF ISNULL(IS_SRVROLEMEMBER('sysadmin'),1) <> 0
 THROW 51000, 'Live activity requires a non-sysadmin collector', 1;
IF (TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))=15 AND HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE')<>1)
 OR (TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))>=16 AND HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE')<>1)
 THROW 51000, 'Live activity metadata permission unavailable', 1;

SELECT TOP (513) s.session_id,r.request_id,COALESCE(r.database_id,s.database_id) AS database_id,
 DB_NAME(COALESCE(r.database_id,s.database_id)) AS database_name,s.login_name,s.host_name,s.program_name,
 COALESCE(r.status,s.status) AS status,r.command,s.is_user_process,r.wait_type,r.blocking_session_id,
 CONVERT(bigint,COALESCE(r.cpu_time,s.cpu_time)) AS cpu_ms,
 CONVERT(bigint,CASE WHEN r.request_id IS NULL THEN s.memory_usage ELSE r.granted_query_memory END)*8192 AS memory_bytes,
 COALESCE(r.reads,s.reads) AS reads,COALESCE(r.writes,s.writes) AS writes,COALESCE(r.logical_reads,s.logical_reads) AS logical_reads,
 CONVERT(bigint,COALESCE(r.total_elapsed_time,s.total_elapsed_time)) AS elapsed_ms,
 i.sqlserver_start_time,s.login_time,r.start_time,r.sql_handle,r.statement_start_offset,r.statement_end_offset
INTO #live FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id
CROSS JOIN sys.dm_os_sys_info i WHERE s.session_id<>@@SPID
ORDER BY CASE WHEN r.request_id IS NOT NULL AND s.is_user_process=1 THEN 0 WHEN s.is_user_process=1 THEN 1 ELSE 2 END,
 s.session_id,r.request_id;
SELECT session_id,request_id,database_id,database_name,login_name,host_name,program_name,status,command,is_user_process,
 wait_type,blocking_session_id,cpu_ms,memory_bytes,reads,writes,logical_reads,elapsed_ms,
 sqlserver_start_time,login_time,start_time FROM #live
 ORDER BY CASE WHEN request_id IS NOT NULL AND is_user_process=1 THEN 0 WHEN is_user_process=1 THEN 1 ELSE 2 END,session_id,request_id;
-- Text failure is a separate result; the already returned metadata remains usable.
BEGIN TRY
 SELECT TOP (512) l.session_id,l.request_id,l.sqlserver_start_time,l.login_time,l.start_time,
 SUBSTRING(t.text,(l.statement_start_offset/2)+1,
 CASE WHEN l.statement_end_offset=-1 THEN 16385 ELSE
 CASE WHEN (l.statement_end_offset-l.statement_start_offset)/2+1>16385 THEN 16385
 ELSE (l.statement_end_offset-l.statement_start_offset)/2+1 END END) AS statement_text
 FROM #live l OUTER APPLY sys.dm_exec_sql_text(l.sql_handle) t
 WHERE l.request_id IS NOT NULL AND @captureText=1 ORDER BY l.session_id,l.request_id;
END TRY
BEGIN CATCH
 SELECT CONVERT(int,NULL) AS session_id WHERE 1=0;
END CATCH;
SELECT TOP (1024) database_id,name FROM sys.databases ORDER BY name,database_id;
