SET NOCOUNT ON;
/* M7 passive SQL Server 16 asset. Requires VIEW DATABASE PERFORMANCE STATE for Query Store. */
DECLARE @now datetime2(7) = SYSUTCDATETIME();
IF @probe_rows < 1 OR @probe_rows > 2001 SET @probe_rows = 2001;
DECLARE @query_store_state varchar(32) = (SELECT TOP (1) actual_state_desc FROM sys.database_query_store_options);
DECLARE @query_store_permission bit = CONVERT(bit,HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DATABASE PERFORMANCE STATE'));
SELECT CASE WHEN @query_store_permission=0 THEN 'PERMISSION_DENIED' ELSE COALESCE(@query_store_state,'UNSUPPORTED') END AS query_store_state;
IF @query_store_state IN ('READ_WRITE','READ_ONLY')
BEGIN
 SELECT TOP (@probe_rows) CONVERT(int,DB_ID()),CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONCAT(CONVERT(varchar(20),q.query_hash),':',CONVERT(varchar(20),q.context_settings_id),':',CONVERT(varchar(20),q.object_id)))),2),CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(8),p.plan_id)),2),CONVERT(varchar(16),'query_store'),CONVERT(varchar(32),qs.actual_state_desc),CONVERT(bigint,SUM(rs.avg_cpu_time*rs.count_executions/1000.0)),CONVERT(bigint,SUM(rs.avg_duration*rs.count_executions/1000.0)),CONVERT(bigint,SUM(rs.count_executions)),CONVERT(bigint,SUM(rs.avg_logical_io_reads*rs.count_executions)),CONVERT(bigint,SUM(rs.avg_logical_io_writes*rs.count_executions)),CONVERT(bigint,SUM(rs.avg_rowcount*rs.count_executions)),MIN(rsi.start_time),MAX(rsi.end_time),@now,CONVERT(bit,1),CONVERT(bit,0)
 FROM sys.query_store_query q JOIN sys.query_store_plan p ON p.query_id=q.query_id JOIN sys.query_store_runtime_stats rs ON rs.plan_id=p.plan_id JOIN sys.query_store_runtime_stats_interval rsi ON rsi.runtime_stats_interval_id=rs.runtime_stats_interval_id CROSS JOIN sys.database_query_store_options qs
 WHERE rsi.start_time >= DATEADD(minute,-5,@window_start) AND rsi.end_time < @window_end GROUP BY q.query_hash,q.context_settings_id,q.object_id,p.plan_id,qs.actual_state_desc ORDER BY SUM(rs.avg_cpu_time*rs.count_executions) DESC,q.query_hash,q.context_settings_id,q.object_id,p.plan_id;
END
