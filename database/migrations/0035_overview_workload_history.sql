-- Bounded, run/revision-bound workload analytics. Historical counters without an
-- engine start marker remain unavailable for rates, including apparently positive resets.
SET LOCAL ROLE sqlobserver_migrator;
CREATE FUNCTION reporting.overview_workload_history(p_instance_id uuid, p_revision bigint,
 p_from timestamptz, p_to timestamptz, p_cutoff timestamptz)
RETURNS TABLE(metric_key text, bucket_at timestamptz, metric_value double precision, sample_count integer)
LANGUAGE plpgsql SECURITY DEFINER STABLE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $overview$
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'') <> 'Viewer'
 OR coalesce(current_setting('sqlobserver.target_scope',true),'') IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'overview scope rejected' USING ERRCODE='42501'; END IF;
 IF p_instance_id IS NULL OR p_revision IS NULL OR p_revision < 1 OR p_from IS NULL OR p_to IS NULL OR p_cutoff IS NULL
 OR p_to <= p_from OR p_to-p_from > interval '31 days' OR p_to > p_cutoff + interval '1 minute'
 THEN RAISE EXCEPTION 'overview window rejected' USING ERRCODE='22023'; END IF;
 IF NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id AND t.revision=p_revision)
 THEN RAISE EXCEPTION 'overview revision changed' USING ERRCODE='40001'; END IF;
 RETURN QUERY
 WITH runs AS (
  SELECT r.run_id, max(s.observed_at) AS at,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.user_connections') AS connections,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.batch_requests_total') AS batches,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.start_time_key') AS epoch
  FROM telemetry.raw_metric_sample s
  JOIN telemetry.collection_run r ON r.run_id=s.collection_run_id AND r.instance_id=s.instance_id
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE s.instance_id=p_instance_id AND r.target_revision=p_revision AND r.collector_id='engine.core'
   AND s.observed_at >= p_from-interval '2 minutes' AND s.observed_at < p_to
   AND s.collected_at <= p_cutoff AND o.completed_at <= p_cutoff AND o.outcome='succeeded'
   AND NOT o.loss_detected AND s.dimensions='{}'::jsonb
   AND s.metric_key IN ('engine.user_connections','engine.batch_requests_total','engine.start_time_key')
  GROUP BY r.run_id
 ), paired AS (
  SELECT *, lag(at) OVER w AS prior_at, lag(batches) OVER w AS prior_batches, lag(epoch) OVER w AS prior_epoch
  FROM runs WINDOW w AS (ORDER BY at,run_id)
 ), measured AS (
  SELECT 'engine.user_connections'::text AS metric, at, connections AS value, 1::double precision AS weight FROM paired WHERE at>=p_from
  UNION ALL
  SELECT 'engine.batch_requests_per_second', at,
   CASE WHEN epoch IS NOT NULL AND epoch=prior_epoch AND at>prior_at AND at-prior_at<=interval '2 minutes'
     AND batches>=prior_batches AND batches<=9007199254740991 AND prior_batches<=9007199254740991
    THEN (batches-prior_batches)/extract(epoch FROM at-prior_at)::double precision ELSE NULL END,
   extract(epoch FROM at-prior_at)::double precision
  FROM paired WHERE at>=p_from
 )
 SELECT v.metric, date_bin(CASE WHEN p_to-p_from>interval '1 day' THEN interval '1 hour' WHEN p_to-p_from>interval '6 hours' THEN interval '15 minutes' ELSE interval '5 minutes' END,
  v.at, timestamptz '2000-01-01 00:00:00Z'),
  (sum(v.value*v.weight)/nullif(sum(v.weight) FILTER (WHERE v.value IS NOT NULL),0))::double precision, count(v.value)::integer
 FROM measured v GROUP BY 1,2 ORDER BY 1,2;
END $overview$;
REVOKE ALL ON FUNCTION reporting.overview_workload_history(uuid,bigint,timestamptz,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reporting.overview_workload_history(uuid,bigint,timestamptz,timestamptz,timestamptz) TO sqlobserver_server;

-- Preserve old eight-counter collectors while accepting the ninth startup marker.
-- The existing ingestion function remains the authority for all other validation.
DO $upgrade$
DECLARE source text; signature regprocedure;
BEGIN
 SELECT p.oid::regprocedure INTO STRICT signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='control' AND p.prokind='f' AND p.prosrc LIKE '%metric_count <> 8%';
 source := pg_get_functiondef(signature);
 IF position('FROM unnest(p_metric_keys) AS metric(metric_key)' IN source)=0
 THEN RAISE EXCEPTION 'unexpected core ingestion definition'; END IF;
 source := replace(source,'metric_count <> 8',
   '(metric_count NOT IN (8,9) OR (metric_count=9 AND NOT (''engine.start_time_key''=ANY(p_metric_keys))))');
 source := replace(source,'FROM unnest(p_metric_keys) AS metric(metric_key)',
   'FROM unnest(array_remove(p_metric_keys,''engine.start_time_key'')) AS metric(metric_key)');
 source := replace(source,$old$'engine.target_memory_bytes'
$old$,$new$'engine.target_memory_bytes',
                   'engine.start_time_key'
$new$);
 IF position($marker$'engine.start_time_key'
$marker$ IN source)=0 THEN RAISE EXCEPTION 'core metric allowlist upgrade failed'; END IF;
 EXECUTE source;
END $upgrade$;

-- Deploy with the matching collector bundle. Historical run digests are unchanged.
-- Upgrade exactly the three M4 registry rows; restore their append-only trigger in this transaction.
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $bundle_upgrade$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('34214cef39c56f1d984bee1da82fd40ac410552eca04f6bd64420b001bd3114c','hex')
 WHERE collector_id IN ('engine.core','database.inventory','database.files') AND collector_version=1
 AND asset_bundle_sha256=decode('1dd0cc6cbdc4171ff656c658974cf4105c8e2594e5d1f26a5fc66011adaa284e','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>3 THEN RAISE EXCEPTION 'Unexpected prior core bundle registry' USING ERRCODE='55000'; END IF;
END $bundle_upgrade$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
