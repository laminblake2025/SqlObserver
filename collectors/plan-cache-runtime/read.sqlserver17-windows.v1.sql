SET NOCOUNT ON;
-- SQL Server 17 passive cache source; requires VIEW SERVER PERFORMANCE STATE.
IF @maximum_rows IS NULL OR @maximum_rows NOT BETWEEN 1 AND 2000
    THROW 51000, 'Cache candidate limit is outside the reviewed bounds', 1;
DECLARE @probe_rows int=@maximum_rows+1;
DECLARE @started datetime2(7)=SYSUTCDATETIME(),@completed datetime2(7),@epoch varchar(64);
SELECT @epoch=CONVERT(varchar(64),HASHBYTES('SHA2_256',
    CONVERT(binary(8),sqlserver_start_time_ms_ticks)+
    CONVERT(varbinary(48),CONVERT(varchar(33),sqlserver_start_time,126))),2)
FROM sys.dm_os_sys_info;

-- Materialize the bounded native rows BEFORE expanding any plan attributes.
DECLARE @candidates TABLE
(
    candidate_id int IDENTITY(1,1) PRIMARY KEY,
    plan_handle varbinary(64),sql_handle varbinary(64),query_hash binary(8),query_plan_hash binary(8),
    statement_start_offset int,statement_end_offset int,plan_generation_num bigint,creation_time datetime,
    cpu_us bigint,duration_us bigint,executions bigint,logical_reads bigint,writes bigint,total_rows bigint
);
INSERT @candidates(plan_handle,sql_handle,query_hash,query_plan_hash,statement_start_offset,statement_end_offset,
    plan_generation_num,creation_time,cpu_us,duration_us,executions,logical_reads,writes,total_rows)
SELECT TOP (@probe_rows) plan_handle,sql_handle,query_hash,query_plan_hash,statement_start_offset,statement_end_offset,
    plan_generation_num,creation_time,total_worker_time,total_elapsed_time,execution_count,total_logical_reads,total_logical_writes,total_rows
FROM sys.dm_exec_query_stats
ORDER BY total_worker_time DESC,total_elapsed_time DESC,query_hash,plan_handle,sql_handle,
    statement_start_offset,statement_end_offset,plan_generation_num,creation_time;

DECLARE @resolved TABLE
(
    candidate_id int PRIMARY KEY,database_id int,resolution_kind tinyint,
    query_fingerprint varchar(64),plan_fingerprint varchar(64),entry_fingerprint varchar(64),
    cpu_us bigint,duration_us bigint,executions bigint,logical_reads bigint,writes bigint,total_rows bigint
);
INSERT @resolved
SELECT c.candidate_id,db.database_id,
    CASE WHEN db.database_id IS NULL OR db.database_id NOT BETWEEN 1 AND 32767 THEN 4
         WHEN db.database_id<=4 OR db.database_id=32767 THEN 2
         WHEN d.database_id IS NULL THEN 3
         WHEN d.state<>0 OR d.user_access<>0 THEN 2
         WHEN c.query_hash IS NULL OR c.query_hash=0x0000000000000000
              OR c.query_plan_hash IS NULL OR c.query_plan_hash=0x0000000000000000 THEN 4 ELSE 1 END,
    CONVERT(varchar(64),token.query_token,2),
    CONVERT(varchar(64),HASHBYTES('SHA2_256',token.query_token+c.query_plan_hash),2),
    CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(binary(4),db.database_id)+
        CONVERT(binary(4),DATALENGTH(c.plan_handle))+c.plan_handle+
        CONVERT(binary(4),DATALENGTH(c.sql_handle))+c.sql_handle+
        CONVERT(binary(4),c.statement_start_offset)+CONVERT(binary(4),c.statement_end_offset)+
        CONVERT(binary(8),c.plan_generation_num)+
        CONVERT(varbinary(48),CONVERT(varchar(33),c.creation_time,126))),2),
    c.cpu_us,c.duration_us,c.executions,c.logical_reads,c.writes,c.total_rows
FROM @candidates c
OUTER APPLY (SELECT CASE WHEN COUNT(*)=1 THEN MIN(TRY_CONVERT(int,pa.value)) END AS database_id
    FROM sys.dm_exec_plan_attributes(c.plan_handle) pa WHERE pa.attribute='dbid') db
LEFT JOIN sys.databases d ON d.database_id=db.database_id
-- Preserve the legacy normalized cache-query fingerprint. Cache-entry
-- identity keeps the distinct statements/allocations out of the same baseline.
CROSS APPLY (SELECT CONVERT(binary(32),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
    CONCAT(CONVERT(varchar(20),c.query_hash),':cache:',CONVERT(varchar(20),db.database_id))))) AS query_token) token;
SET @completed=SYSUTCDATETIME();

-- Account for the candidate lookahead even if it belongs to an excluded database.
SELECT CONVERT(int,1) AS source_format,
    TODATETIMEOFFSET(@started,'+00:00') AS read_started_at_utc,
    TODATETIMEOFFSET(@completed,'+00:00') AS read_completed_at_utc,@epoch AS source_epoch,
    (SELECT COUNT(*) FROM @candidates) AS candidate_rows,
    (SELECT COUNT(*) FROM @resolved WHERE resolution_kind=4) AS unresolved_rows,
    (SELECT COUNT(*) FROM @resolved WHERE resolution_kind=3) AS invisible_database_rows,
    (SELECT COUNT(*) FROM @resolved WHERE resolution_kind=2) AS excluded_rows,
    (SELECT COUNT(*) FROM @resolved WHERE resolution_kind=1) AS emitted_rows;
SELECT database_id,query_fingerprint,plan_fingerprint,entry_fingerprint,
    cpu_us,duration_us,executions,logical_reads,writes,total_rows
FROM @resolved WHERE resolution_kind=1 ORDER BY candidate_id;
