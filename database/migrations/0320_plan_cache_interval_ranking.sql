-- Rank observed cache counter differences without assigning boundary-crossing
-- differences to a smaller window or replacing unknown comparisons with zero.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_plan_cache_interval_queries(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
 p_metric text,p_max_results integer)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
SET plan_cache_mode=force_custom_plan
AS $ranking$
DECLARE v_result jsonb;
BEGIN
 IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
    OR p_metric IS NULL OR p_metric NOT IN ('cpu','duration','executions','logical_reads','writes','rows')
    OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN
  RAISE EXCEPTION 'Cache ranking bounds are invalid' USING ERRCODE='22023'; END IF;
 IF NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'Cache ranking requires exact target scope' USING ERRCODE='42501'; END IF;

 WITH frames AS MATERIALIZED (
  SELECT receipt.* FROM events.plan_cache_runtime_receipt receipt
  JOIN control.observation_target target ON target.instance_id=receipt.instance_id
   AND target.revision=receipt.target_revision
  WHERE receipt.instance_id=p_instance_id
   AND receipt.source_read_completed_at>=p_from_utc AND receipt.source_read_completed_at<p_to_utc
   AND receipt.committed_at>=p_from_utc-interval '5 minutes'
   AND receipt.committed_at<p_to_utc+interval '5 minutes'
 ), source AS MATERIALIZED (
  SELECT delta.* FROM events.plan_cache_runtime_delta delta
  JOIN frames ON frames.instance_id=delta.instance_id AND frames.run_id=delta.run_id
   AND frames.committed_at=delta.committed_at
  WHERE delta.instance_id=p_instance_id
   AND delta.committed_at>=p_from_utc-interval '5 minutes'
   AND delta.committed_at<p_to_utc+interval '5 minutes'
 ), comparable AS MATERIALIZED (
  SELECT source.* FROM source WHERE source.transition='comparable'
   AND source.delta_from>=p_from_utc AND source.delta_to<p_to_utc
 ), totals AS (
  -- Do not combine databases that reused the same numeric ID after recreation.
  -- Valid increments may cross a server restart only as separate, qualified tuples.
  SELECT comparable.database_id,comparable.database_incarnation,
   comparable.query_fingerprint,comparable.plan_fingerprint,
   sum(comparable.cpu_us::numeric)/1000 AS cpu_ms,
   sum(comparable.duration_us::numeric)/1000 AS duration_ms,
   sum(comparable.execution_count::numeric) AS executions,
   sum(comparable.logical_reads::numeric) AS logical_reads,
   sum(comparable.writes::numeric) AS writes,
   sum(comparable.rows_processed::numeric) AS rows_processed,
   count(*) AS comparable_rows,min(comparable.delta_from) AS first_observed_at,
   max(comparable.delta_to) AS last_observed_at
  FROM comparable GROUP BY comparable.database_id,comparable.database_incarnation,
   comparable.query_fingerprint,comparable.plan_fingerprint
 ), ranked AS (
  SELECT totals.*,row_number() OVER(ORDER BY
   CASE p_metric WHEN 'cpu' THEN totals.cpu_ms WHEN 'duration' THEN totals.duration_ms
    WHEN 'executions' THEN totals.executions WHEN 'logical_reads' THEN totals.logical_reads
    WHEN 'writes' THEN totals.writes WHEN 'rows' THEN totals.rows_processed END DESC,
   totals.database_id,totals.query_fingerprint,totals.plan_fingerprint,totals.database_incarnation) AS ordinal
  FROM totals
 ), page AS MATERIALIZED (
  SELECT ranked.* FROM ranked WHERE ranked.ordinal<=p_max_results+1
 )
 SELECT jsonb_build_object(
  'targetId',p_instance_id,'fromUtc',p_from_utc,'toUtc',p_to_utc,
  'semantics','plan_cache_delta','metric',p_metric,
  'coverage',CASE WHEN EXISTS(SELECT 1 FROM comparable) THEN 'observed_entries_only' ELSE 'no_comparable_rows' END,
  'sourceFrames',(SELECT count(*) FROM frames),
  'incompleteFrames',(SELECT count(*) FROM frames WHERE NOT frames.qualified_exhaustive),
  'comparableRows',(SELECT count(*) FROM comparable),
  'unavailableRows',(SELECT count(*) FROM source WHERE transition<>'comparable'),
  'boundaryRows',(SELECT count(*) FROM source WHERE transition='comparable' AND delta_from<p_from_utc),
  'hasMore',EXISTS(SELECT 1 FROM page WHERE ordinal>p_max_results),
  'items',coalesce((SELECT jsonb_agg(jsonb_build_object(
   'databaseId',page.database_id,'databaseIncarnation',encode(page.database_incarnation,'hex'),
   'queryFingerprint',encode(page.query_fingerprint,'hex'),'planFingerprint',encode(page.plan_fingerprint,'hex'),
   'cpuMilliseconds',page.cpu_ms::text,'durationMilliseconds',page.duration_ms::text,
   'executions',page.executions::text,'logicalReads',page.logical_reads::text,
   'writes',page.writes::text,'rows',page.rows_processed::text,
   'comparableRows',page.comparable_rows,'firstObservedAtUtc',page.first_observed_at,
   'lastObservedAtUtc',page.last_observed_at) ORDER BY page.ordinal)
   FROM page WHERE page.ordinal<=p_max_results),'[]'::jsonb)) INTO v_result;
 RETURN v_result;
END $ranking$;

REVOKE ALL ON FUNCTION reporting.list_plan_cache_interval_queries(uuid,timestamptz,timestamptz,text,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_plan_cache_interval_queries(uuid,timestamptz,timestamptz,text,integer)
 TO sqlobserver_server;
