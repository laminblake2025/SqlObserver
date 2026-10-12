SET NOCOUNT ON;

-- Buffer Manager PLE is an instance-wide aggregate. Buffer Node exposes
-- the individual NUMA-node values without requiring host-local counters.
DECLARE @observed_at_utc datetime2(7) = SYSUTCDATETIME();

SELECT TOP (@maximum_rows)
    @observed_at_utc AS observed_at_utc,
    TRY_CONVERT(int, RTRIM(counter.instance_name)) AS node_id,
    CONVERT(bigint, counter.cntr_value) AS page_life_expectancy_seconds
FROM sys.dm_os_performance_counters AS counter
WHERE RTRIM(counter.object_name) LIKE N'%:Buffer Node'
  AND RTRIM(counter.counter_name) = N'Page life expectancy'
ORDER BY node_id;
