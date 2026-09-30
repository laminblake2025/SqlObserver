-- Share SQL gauge/rate derivation between Overview and catalog-v2 analytics.
-- Existing catalog-v1 jobs, result metadata and cursors are unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.sql_core_metric_inputs(
 p_instance_id uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,
 p_cutoff timestamptz,p_partitioned boolean)
RETURNS TABLE(source_run_id uuid,observed_at timestamptz,metric_key text,
 metric_value double precision,aggregation_weight double precision)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $inputs$
BEGIN
 IF p_instance_id IS NULL OR coalesce(current_setting('sqlobserver.target_scope',true),'') IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'sql metric scope rejected' USING ERRCODE='42501'; END IF;
 IF p_revision IS NULL OR p_revision<1 OR p_from IS NULL OR p_to IS NULL OR p_cutoff IS NULL OR p_partitioned IS NULL
 OR p_to<=p_from OR p_to-p_from>interval '31 days' OR p_to>p_cutoff+interval '1 minute'
 THEN RAISE EXCEPTION 'sql metric window rejected' USING ERRCODE='22023'; END IF;
 IF NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id AND t.revision=p_revision)
 THEN RAISE EXCEPTION 'sql metric revision changed' USING ERRCODE='40001'; END IF;
 RETURN QUERY
 WITH source_runs AS (
  SELECT r.run_id,r.instance_id,r.target_revision,r.collector_id
  FROM telemetry.collection_run r
  WHERE NOT p_partitioned AND r.instance_id=p_instance_id AND r.target_revision=p_revision AND r.collector_id='engine.core'
  UNION ALL
  SELECT r.run_id,r.instance_id,r.target_revision,r.collector_id
  FROM telemetry.collection_run_v2 r
  WHERE p_partitioned AND r.instance_id=p_instance_id AND r.target_revision=p_revision AND r.collector_id='engine.core'
 ), runs AS (
  SELECT r.run_id, max(s.observed_at) AS at,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.user_connections') AS connections,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.batch_requests_total') AS batches,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.start_time_key') AS epoch,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.process_physical_memory_bytes') AS sql_memory,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.os_available_memory_bytes') AS os_available_memory,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.memory_grants_pending') AS grants_pending,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.scheduler_runnable_tasks') AS runnable_tasks,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.scheduler_cpu_milliseconds_total') AS scheduler_cpu_ms,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.visible_scheduler_count') AS scheduler_count
  FROM telemetry.raw_metric_sample s
  JOIN source_runs r ON r.run_id=s.collection_run_id AND r.instance_id=s.instance_id
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE s.instance_id=p_instance_id AND r.target_revision=p_revision AND r.collector_id='engine.core'
   AND s.observed_at >= p_from-interval '2 minutes' AND s.observed_at < p_to
   AND s.collected_at <= p_cutoff AND o.completed_at <= p_cutoff AND o.outcome='succeeded'
   AND NOT o.loss_detected AND s.dimensions='{}'::jsonb
   AND s.metric_key IN ('engine.user_connections','engine.batch_requests_total',
    'engine.start_time_key','engine.process_physical_memory_bytes',
    'engine.os_available_memory_bytes','engine.memory_grants_pending','engine.scheduler_runnable_tasks',
    'engine.scheduler_cpu_milliseconds_total','engine.visible_scheduler_count')
  GROUP BY r.run_id
 ), paired AS (
  SELECT *, lag(at) OVER w AS prior_at, lag(batches) OVER w AS prior_batches,
   lag(epoch) OVER w AS prior_epoch, lag(scheduler_cpu_ms) OVER w AS prior_scheduler_cpu_ms,
   lag(scheduler_count) OVER w AS prior_scheduler_count
  FROM runs WINDOW w AS (ORDER BY at,run_id)
 ), measured AS (
  SELECT run_id,'engine.user_connections'::text AS metric, at, connections AS value, 1::double precision AS weight FROM paired WHERE at>=p_from
  UNION ALL
  SELECT run_id,'engine.process_physical_memory_bytes', at, sql_memory, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT run_id,'engine.os_available_memory_bytes', at, os_available_memory, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT run_id,'engine.memory_grants_pending', at, grants_pending, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT run_id,'engine.scheduler_runnable_tasks', at, runnable_tasks, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT run_id,'engine.sql_scheduler_cpu_percent', at,
   CASE WHEN epoch IS NOT NULL AND epoch=prior_epoch AND at>prior_at AND at-prior_at<=interval '2 minutes'
     AND scheduler_count>0 AND scheduler_count=prior_scheduler_count
     AND scheduler_cpu_ms>=prior_scheduler_cpu_ms
     AND scheduler_cpu_ms<=9007199254740991 AND prior_scheduler_cpu_ms<=9007199254740991
     AND scheduler_cpu_ms-prior_scheduler_cpu_ms<=
       extract(epoch FROM at-prior_at)::double precision*1000*scheduler_count*1.05
    THEN least(100::double precision,
      100*(scheduler_cpu_ms-prior_scheduler_cpu_ms)/
      (extract(epoch FROM at-prior_at)::double precision*1000*scheduler_count))
    ELSE NULL END,
   extract(epoch FROM at-prior_at)::double precision
  FROM paired WHERE at>=p_from
  UNION ALL
  SELECT run_id,'engine.batch_requests_per_second', at,
   CASE WHEN epoch IS NOT NULL AND epoch=prior_epoch AND at>prior_at AND at-prior_at<=interval '2 minutes'
     AND batches>=prior_batches AND batches<=9007199254740991 AND prior_batches<=9007199254740991
    THEN (batches-prior_batches)/extract(epoch FROM at-prior_at)::double precision ELSE NULL END,
   extract(epoch FROM at-prior_at)::double precision
  FROM paired WHERE at>=p_from
 )
 SELECT v.run_id,v.at,v.metric,v.value,v.weight FROM measured v
 ORDER BY v.at,v.run_id,v.metric;
END $inputs$;
REVOKE ALL ON FUNCTION reporting.sql_core_metric_inputs(uuid,bigint,timestamptz,timestamptz,timestamptz,boolean)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE OR REPLACE FUNCTION reporting.overview_workload_history_legacy(
 p_instance_id uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz)
RETURNS TABLE(metric_key text,bucket_at timestamptz,metric_value double precision,sample_count integer)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $overview$
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'Viewer'
 THEN RAISE EXCEPTION 'overview scope rejected' USING ERRCODE='42501'; END IF;
 RETURN QUERY
 SELECT v.metric_key,date_bin(CASE WHEN p_to-p_from>interval '1 day' THEN interval '1 hour'
  WHEN p_to-p_from>interval '6 hours' THEN interval '15 minutes' ELSE interval '5 minutes' END,
  v.observed_at,timestamptz '2000-01-01 00:00:00Z'),
  (sum(v.metric_value*v.aggregation_weight)/nullif(sum(v.aggregation_weight)
   FILTER (WHERE v.metric_value IS NOT NULL),0))::double precision,count(v.metric_value)::integer
 FROM reporting.sql_core_metric_inputs(p_instance_id,p_revision,p_from,p_to,p_cutoff,false) v
 GROUP BY 1,2 ORDER BY 1,2;
END $overview$;
REVOKE ALL ON FUNCTION reporting.overview_workload_history_legacy(uuid,bigint,timestamptz,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE OR REPLACE FUNCTION reporting.overview_workload_history_v2_shadow(
 p_instance_id uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz)
RETURNS TABLE(metric_key text,bucket_at timestamptz,metric_value double precision,sample_count integer)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $overview$
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'Viewer'
 THEN RAISE EXCEPTION 'overview scope rejected' USING ERRCODE='42501'; END IF;
 RETURN QUERY
 SELECT v.metric_key,date_bin(CASE WHEN p_to-p_from>interval '1 day' THEN interval '1 hour'
  WHEN p_to-p_from>interval '6 hours' THEN interval '15 minutes' ELSE interval '5 minutes' END,
  v.observed_at,timestamptz '2000-01-01 00:00:00Z'),
  (sum(v.metric_value*v.aggregation_weight)/nullif(sum(v.aggregation_weight)
   FILTER (WHERE v.metric_value IS NOT NULL),0))::double precision,count(v.metric_value)::integer
 FROM reporting.sql_core_metric_inputs(p_instance_id,p_revision,p_from,p_to,p_cutoff,true) v
 GROUP BY 1,2 ORDER BY 1,2;
END $overview$;
REVOKE ALL ON FUNCTION reporting.overview_workload_history_v2_shadow(uuid,bigint,timestamptz,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
-- This bounded catalog-v2 input reader retains null observations and elapsed
-- weights. RollupV1 must not consume it: activation needs versioned result/job
-- metadata and a weighted rollup adapter. It is a source contract, not a switch.
CREATE FUNCTION reporting.read_sql_metric_analytics_inputs(
 p_instance_id uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,
 p_cutoff timestamptz,p_metric_key text,p_limit integer,p_max_bytes integer)
RETURNS TABLE(source_run_id uuid,observed_at timestamptz,metric_key text,
 metric_value double precision,aggregation_weight double precision,complete boolean)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $analytics$
DECLARE partitioned boolean; input_count bigint; input_bytes bigint;
BEGIN
 IF p_from IS NULL OR p_to IS NULL OR p_cutoff IS NULL OR p_to<=p_from
 OR p_to-p_from>interval '1 day' OR p_to>p_cutoff
 OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100000
 OR p_max_bytes IS NULL OR p_max_bytes NOT BETWEEN 1 AND 8388608
 OR p_metric_key IS NOT NULL AND p_metric_key NOT IN
  ('engine.user_connections','engine.process_physical_memory_bytes','engine.os_available_memory_bytes',
   'engine.memory_grants_pending','engine.scheduler_runnable_tasks',
   'engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')
 THEN RAISE EXCEPTION 'sql analytics input bounds rejected' USING ERRCODE='22023'; END IF;
 SELECT route.use_v2 INTO partitioned FROM system.collection_run_v2_m10_read_route route WHERE route.singleton;
 IF partitioned IS NULL THEN RAISE EXCEPTION 'sql analytics route unavailable' USING ERRCODE='55000'; END IF;
 -- A limit is a hard failure, never a silently incomplete successful page.
 SELECT count(*),coalesce(sum(octet_length(row_to_json(bounded)::text)),0)
 INTO input_count,input_bytes FROM (
  SELECT v.*,v.metric_value IS NOT NULL AND v.aggregation_weight>0 AS complete
  FROM reporting.sql_core_metric_inputs(p_instance_id,p_revision,p_from,p_to,p_cutoff,partitioned) v
  WHERE p_metric_key IS NULL OR v.metric_key=p_metric_key
  ORDER BY v.observed_at,v.source_run_id,v.metric_key LIMIT p_limit+1
 ) bounded;
 IF input_count>p_limit THEN RAISE EXCEPTION 'sql analytics input row limit' USING ERRCODE='22023'; END IF;
 IF input_bytes>p_max_bytes THEN RAISE EXCEPTION 'sql analytics input byte limit' USING ERRCODE='22023'; END IF;
 RETURN QUERY
 SELECT v.source_run_id,v.observed_at,v.metric_key,v.metric_value,v.aggregation_weight,
  v.metric_value IS NOT NULL AND v.aggregation_weight>0
 FROM reporting.sql_core_metric_inputs(p_instance_id,p_revision,p_from,p_to,p_cutoff,partitioned) v
 WHERE p_metric_key IS NULL OR v.metric_key=p_metric_key
 ORDER BY v.observed_at,v.source_run_id,v.metric_key;
END $analytics$;
REVOKE ALL ON FUNCTION reporting.read_sql_metric_analytics_inputs(uuid,bigint,timestamptz,timestamptz,timestamptz,text,integer,integer)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_metric_analytics_inputs(uuid,bigint,timestamptz,timestamptz,timestamptz,text,integer,integer)
 TO sqlobserver_collector;
