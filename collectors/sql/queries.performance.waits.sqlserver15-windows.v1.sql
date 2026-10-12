SET NOCOUNT ON;
/* M7 passive, bounded Query Store wait-category totals for SQL Server 15. */
DECLARE @wait_capture_mode varchar(32) =
    (SELECT TOP (1) wait_stats_capture_mode_desc FROM sys.database_query_store_options);
SELECT COALESCE(@wait_capture_mode,'UNAVAILABLE') AS wait_capture_mode;
IF @wait_capture_mode = 'ON'
BEGIN
    SELECT TOP (256) ws.plan_id, CONVERT(int,ws.wait_category) AS category,
           CONVERT(bigint,SUM(CONVERT(decimal(38,0),ws.total_query_wait_time_ms))) AS wait_ms
    FROM sys.query_store_wait_stats AS ws
    JOIN sys.query_store_runtime_stats_interval AS rsi
      ON rsi.runtime_stats_interval_id = ws.runtime_stats_interval_id
    WHERE ws.plan_id IN (@plan_id_0,@plan_id_1,@plan_id_2,@plan_id_3,
                         @plan_id_4,@plan_id_5,@plan_id_6,@plan_id_7)
      AND ws.wait_category BETWEEN 0 AND 31
      AND rsi.start_time < @window_end
      AND rsi.end_time > DATEADD(minute,-5,@window_start)
    GROUP BY ws.plan_id,ws.wait_category
    HAVING SUM(CONVERT(decimal(38,0),ws.total_query_wait_time_ms))
           BETWEEN 1 AND 9223372036854775807
    ORDER BY ws.plan_id,ws.wait_category;
END
