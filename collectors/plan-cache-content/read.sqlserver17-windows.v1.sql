SET NOCOUNT ON;
-- Staged content capture; existing counter-only assets remain immutable.
IF @maximum_content_entries IS NULL OR @maximum_content_entries NOT BETWEEN 0 AND 8
 OR @maximum_content_entries>@maximum_rows
 THROW 51000, 'Cache content entry limit is outside the reviewed bounds', 1;
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

-- Content is attempted only for a bounded prefix of the already materialized
-- candidates. No raw handle, batch text or XML crosses the protected reader.
DECLARE @content TABLE(candidate_id int PRIMARY KEY,database_id int,
 query_fingerprint varchar(64),plan_fingerprint varchar(64),entry_fingerprint varchar(64),
 text_state varchar(32),query_text nvarchar(max),plan_state varchar(32),plan_xml nvarchar(max));
DECLARE @candidate int=0,@next int,@source_bytes int=0,@text_bytes bigint,@plan_bytes bigint,
 @text nvarchar(max),@plan nvarchar(max),@text_state varchar(32),@plan_state varchar(32);
WHILE @candidate<@maximum_content_entries
BEGIN
 SELECT @next=MIN(c.candidate_id) FROM @candidates c JOIN @resolved r ON r.candidate_id=c.candidate_id
 WHERE c.candidate_id>@candidate AND c.candidate_id<=@maximum_content_entries AND r.resolution_kind=1;
 IF @next IS NULL BREAK;
 SET @candidate=@next; SET @text=NULL; SET @plan=NULL;
 SET @text_state='unavailable'; SET @plan_state='unavailable';
 -- Statement offsets are UTF-16 byte offsets. A non-SC binary collation avoids
 -- treating a surrogate pair as one character when extracting the statement.
 SELECT @text_bytes=CASE WHEN c.statement_end_offset=-1 THEN DATALENGTH(t.text)
                        ELSE CONVERT(bigint,c.statement_end_offset)+2 END-c.statement_start_offset,
  @text=CASE WHEN c.statement_start_offset>=0 AND c.statement_start_offset%2=0
   AND (c.statement_end_offset=-1 OR c.statement_end_offset>=c.statement_start_offset AND c.statement_end_offset%2=0)
   AND CASE WHEN c.statement_end_offset=-1 THEN DATALENGTH(t.text) ELSE CONVERT(bigint,c.statement_end_offset)+2 END<=DATALENGTH(t.text)
   AND CASE WHEN c.statement_end_offset=-1 THEN DATALENGTH(t.text) ELSE CONVERT(bigint,c.statement_end_offset)+2 END-c.statement_start_offset BETWEEN 2 AND 32768
   THEN SUBSTRING(t.text COLLATE Latin1_General_100_BIN2,c.statement_start_offset/2+1,
    (CASE WHEN c.statement_end_offset=-1 THEN DATALENGTH(t.text) ELSE CONVERT(bigint,c.statement_end_offset)+2 END-c.statement_start_offset)/2) END
 FROM @candidates c OUTER APPLY sys.dm_exec_sql_text(c.sql_handle) t WHERE c.candidate_id=@candidate;
 IF @text_bytes>32768 SET @text_state='oversized';
 ELSE IF @text IS NOT NULL AND DATALENGTH(@text)>0 SET @text_state='available';
 SELECT @plan_bytes=DATALENGTH(p.query_plan),
  @plan=CASE WHEN DATALENGTH(p.query_plan) BETWEEN 2 AND 2097152 THEN p.query_plan END
 FROM @candidates c OUTER APPLY sys.dm_exec_text_query_plan(c.plan_handle,c.statement_start_offset,c.statement_end_offset) p
 WHERE c.candidate_id=@candidate;
 IF @plan_bytes>2097152 SET @plan_state='oversized';
 ELSE IF @plan IS NOT NULL AND DATALENGTH(@plan)>0 SET @plan_state='available';
 -- A removed/recompiled candidate must not acquire replacement content through
 -- a reused handle. Recheck every native allocation/statement identity field.
 IF NOT EXISTS(SELECT 1 FROM @candidates c JOIN sys.dm_exec_query_stats live
  ON live.plan_handle=c.plan_handle AND live.sql_handle=c.sql_handle
   AND live.query_hash=c.query_hash AND live.query_plan_hash=c.query_plan_hash
   AND live.statement_start_offset=c.statement_start_offset AND live.statement_end_offset=c.statement_end_offset
   AND live.plan_generation_num=c.plan_generation_num AND live.creation_time=c.creation_time
  WHERE c.candidate_id=@candidate)
 BEGIN SET @text=NULL; SET @plan=NULL; SET @text_state='unavailable'; SET @plan_state='unavailable'; END;
 IF @text_state='available'
 BEGIN
  IF @source_bytes+DATALENGTH(@text)>2097152 BEGIN SET @text=NULL; SET @text_state='response_byte_limit'; END
  ELSE SET @source_bytes=@source_bytes+DATALENGTH(@text);
 END;
 IF @plan_state='available'
 BEGIN
  IF @source_bytes+DATALENGTH(@plan)>2097152 BEGIN SET @plan=NULL; SET @plan_state='response_byte_limit'; END
  ELSE SET @source_bytes=@source_bytes+DATALENGTH(@plan);
 END;
 INSERT @content SELECT @candidate,r.database_id,r.query_fingerprint,r.plan_fingerprint,r.entry_fingerprint,
  @text_state,@text,@plan_state,@plan FROM @resolved r WHERE r.candidate_id=@candidate;
END;

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

SELECT database_id,query_fingerprint,plan_fingerprint,entry_fingerprint,text_state,query_text,plan_state,plan_xml
FROM @content ORDER BY candidate_id;
