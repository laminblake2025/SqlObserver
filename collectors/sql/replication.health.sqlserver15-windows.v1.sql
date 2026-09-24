SET NOCOUNT ON;

-- The collector is bound to the registered distribution database before this
-- batch runs. Unbound targets emit only a typed visibility-gap row.
IF @distribution_database IS NULL
BEGIN
    SELECT CONVERT(int, 7) AS topology_state, CONVERT(int, 1) AS role_state,
           CONVERT(binary(32), NULL) AS publication_fingerprint,
           CONVERT(binary(32), NULL) AS subscription_fingerprint,
           CONVERT(int, 1) AS status_state, CONVERT(bigint, NULL) AS pending_commands,
           CONVERT(int, NULL) AS latency_millis, CONVERT(int, NULL) AS rate_milli,
           CONVERT(bigint, NULL) AS last_success_age_seconds, CONVERT(bit, 1) AS last_success_unknown,
           CONVERT(int, 3) AS coverage_state;
    RETURN;
END;

-- These are the reviewed read-only replication-monitor surfaces. Only
-- aggregate counters and relative age leave SQL Server; names never cross the
-- collector boundary.
;WITH status_by_agent AS
(
    SELECT agent_id, SUM(CONVERT(bigint, CASE WHEN UndelivCmdsInDistDB < 0 THEN NULL ELSE UndelivCmdsInDistDB END)) AS pending_commands
    FROM dbo.MSdistribution_status GROUP BY agent_id
), latest_history AS
(
    SELECT h.agent_id, h.runstatus, h.current_delivery_latency, h.current_delivery_rate, h.time,
           ROW_NUMBER() OVER (PARTITION BY h.agent_id ORDER BY h.time DESC) AS ordinal
    FROM dbo.MSdistribution_history AS h
), evidence AS
(
    SELECT a.id, CONVERT(int, CASE WHEN a.publication IS NULL THEN 7 ELSE 5 END) AS topology_state,
           CONVERT(int, 4) AS role_state,
           CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(4000), a.publication))) AS publication_fingerprint,
           CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(nvarchar(4000), CONCAT(a.subscriber_db, N'|', ISNULL(a.subscriber_name, N''))))) AS subscription_fingerprint,
           CONVERT(int, CASE WHEN h.runstatus = 6 THEN 4 WHEN j.job_id IS NULL THEN 1 WHEN j.start_execution_date IS NULL OR j.stop_execution_date IS NOT NULL THEN 6 WHEN h.runstatus = 1 THEN 5 WHEN h.runstatus = 5 THEN 3 WHEN h.runstatus IN (2, 3, 4) THEN 2 ELSE 1 END) AS status_state,
           CONVERT(bigint, CASE WHEN s.pending_commands > 2000000000 THEN 2000000000 ELSE s.pending_commands END) AS pending_commands,
           CONVERT(int, CASE WHEN j.job_id IS NULL OR j.start_execution_date IS NULL OR j.stop_execution_date IS NOT NULL THEN NULL WHEN h.current_delivery_latency IS NULL OR h.current_delivery_latency < 0 THEN NULL ELSE h.current_delivery_latency END) AS latency_millis,
           CONVERT(int, CASE WHEN j.job_id IS NULL OR j.start_execution_date IS NULL OR j.stop_execution_date IS NOT NULL THEN NULL WHEN h.current_delivery_rate IS NULL OR h.current_delivery_rate < 0 THEN NULL WHEN h.current_delivery_rate > 2147483 THEN 2147483000 ELSE CONVERT(bigint, h.current_delivery_rate * 1000.0) END) AS rate_milli,
           CONVERT(bigint, CASE WHEN h.time IS NULL THEN NULL ELSE DATEDIFF_BIG(SECOND, h.time, GETDATE()) END) AS last_success_age_seconds,
           CONVERT(bit, CASE WHEN h.time IS NULL OR h.runstatus <> 2 THEN 1 ELSE 0 END) AS last_success_unknown,
           CONVERT(int, 1) AS coverage_state
    FROM dbo.MSdistribution_agents AS a LEFT JOIN status_by_agent AS s ON s.agent_id = a.id
    LEFT JOIN latest_history AS h ON h.agent_id = a.id AND h.ordinal = 1
    LEFT JOIN msdb.dbo.sysjobactivity AS j ON j.job_id = CONVERT(uniqueidentifier, a.job_id)
      AND j.session_id = (SELECT MAX(session_id) FROM msdb.dbo.sysjobactivity)
)
SELECT TOP (@maximum_rows) topology_state, role_state, publication_fingerprint, subscription_fingerprint,
       status_state, pending_commands, latency_millis, rate_milli, last_success_age_seconds,
       last_success_unknown, coverage_state FROM evidence
UNION ALL
SELECT CONVERT(int, 7), CONVERT(int, 4), CONVERT(binary(32), NULL), CONVERT(binary(32), NULL),
       CONVERT(int, 1), CONVERT(bigint, NULL), CONVERT(int, NULL), CONVERT(int, NULL),
       CONVERT(bigint, NULL), CONVERT(bit, 1), CONVERT(int, 3)
WHERE NOT EXISTS (SELECT 1 FROM evidence);
