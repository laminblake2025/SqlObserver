-- Read per-NUMA Buffer Node PLE as a gauge in one current-revision UTC window.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_buffer_node_ple_nodes(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(node_id integer,sample_count bigint,last_observed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $nodes$
DECLARE current_revision bigint; bounded_count integer;
BEGIN
 IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
 THEN RAISE EXCEPTION 'Buffer Node PLE window bounds are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'Buffer Node PLE target scope is required' USING ERRCODE='42501'; END IF;
 SELECT target.revision INTO current_revision FROM control.observation_target AS target
 WHERE target.instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN; END IF;
 SELECT count(*) INTO bounded_count FROM (
  SELECT DISTINCT (sample.dimensions->>'node_id')::integer
  FROM telemetry.raw_metric_sample AS sample
  JOIN telemetry.collection_run AS run ON run.run_id=sample.collection_run_id
  JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
  WHERE sample.instance_id=p_instance_id
    AND sample.metric_key='engine.buffer_node_page_life_expectancy_seconds'
    AND sample.observed_at>=p_from_utc AND sample.observed_at<p_to_utc
    AND sample.dimensions->>'node_id' ~ '^(0|[1-9][0-9]{0,3})$'
    AND run.instance_id=p_instance_id AND run.target_revision=current_revision
    AND run.collector_id='engine.buffer-nodes' AND run.collector_version=1
    AND outcome.outcome='succeeded'
  LIMIT 129
 ) AS bounded;
 IF bounded_count>128 THEN RAISE EXCEPTION 'Buffer Node PLE node cap exceeded' USING ERRCODE='54000'; END IF;
 RETURN QUERY
 SELECT (sample.dimensions->>'node_id')::integer,count(*),max(sample.observed_at)
 FROM telemetry.raw_metric_sample AS sample
 JOIN telemetry.collection_run AS run ON run.run_id=sample.collection_run_id
 JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
 WHERE sample.instance_id=p_instance_id
   AND sample.metric_key='engine.buffer_node_page_life_expectancy_seconds'
   AND sample.observed_at>=p_from_utc AND sample.observed_at<p_to_utc
   AND sample.dimensions->>'node_id' ~ '^(0|[1-9][0-9]{0,3})$'
   AND run.instance_id=p_instance_id AND run.target_revision=current_revision
   AND run.collector_id='engine.buffer-nodes' AND run.collector_version=1
   AND outcome.outcome='succeeded'
 GROUP BY 1 ORDER BY 1 LIMIT 128;
END $nodes$;

CREATE FUNCTION reporting.get_buffer_node_ple_trend(
 p_instance_id uuid,p_node_id integer,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(bucket_start_utc timestamptz,sample_count bigint,
 minimum_seconds double precision,average_seconds double precision,
 maximum_seconds double precision)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $trend$
DECLARE current_revision bigint; bounded_count integer;
BEGIN
 IF p_instance_id IS NULL OR p_node_id IS NULL OR p_node_id NOT BETWEEN 0 AND 1023
    OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
 THEN RAISE EXCEPTION 'Buffer Node PLE trend bounds are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'Buffer Node PLE target scope is required' USING ERRCODE='42501'; END IF;
 SELECT target.revision INTO current_revision FROM control.observation_target AS target
 WHERE target.instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN; END IF;
 SELECT count(*) INTO bounded_count FROM (
  SELECT 1 FROM telemetry.raw_metric_sample AS sample
  JOIN telemetry.collection_run AS run ON run.run_id=sample.collection_run_id
  JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
  WHERE sample.instance_id=p_instance_id
    AND sample.metric_key='engine.buffer_node_page_life_expectancy_seconds'
    AND sample.dimensions->>'node_id'=p_node_id::text
    AND sample.observed_at>=p_from_utc AND sample.observed_at<p_to_utc
    AND run.instance_id=p_instance_id AND run.target_revision=current_revision
    AND run.collector_id='engine.buffer-nodes' AND run.collector_version=1
    AND outcome.outcome='succeeded'
  LIMIT 25001
 ) AS bounded;
 IF bounded_count>25000 THEN RAISE EXCEPTION 'Buffer Node PLE trend source cap exceeded' USING ERRCODE='54000'; END IF;
 RETURN QUERY
 SELECT date_bin(interval '1 hour',sample.observed_at,
                  '2000-01-01 00:00:00+00'::timestamptz),
        count(*),min(sample.metric_value),avg(sample.metric_value),max(sample.metric_value)
 FROM telemetry.raw_metric_sample AS sample
 JOIN telemetry.collection_run AS run ON run.run_id=sample.collection_run_id
 JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
 WHERE sample.instance_id=p_instance_id
   AND sample.metric_key='engine.buffer_node_page_life_expectancy_seconds'
   AND sample.dimensions->>'node_id'=p_node_id::text
   AND sample.observed_at>=p_from_utc AND sample.observed_at<p_to_utc
   AND run.instance_id=p_instance_id AND run.target_revision=current_revision
   AND run.collector_id='engine.buffer-nodes' AND run.collector_version=1
   AND outcome.outcome='succeeded'
 GROUP BY 1 ORDER BY 1 LIMIT 169;
END $trend$;

CREATE FUNCTION reporting.get_buffer_manager_ple_trend(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(bucket_start_utc timestamptz,sample_count bigint,
 minimum_seconds double precision,average_seconds double precision,
 maximum_seconds double precision)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $aggregate$
DECLARE current_revision bigint; bounded_count integer;
BEGIN
 IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
 THEN RAISE EXCEPTION 'Buffer Manager PLE trend bounds are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'Buffer Manager PLE target scope is required' USING ERRCODE='42501'; END IF;
 SELECT target.revision INTO current_revision FROM control.observation_target AS target
 WHERE target.instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN; END IF;
 SELECT count(*) INTO bounded_count FROM (
  SELECT 1 FROM telemetry.raw_metric_sample AS sample
  JOIN telemetry.collection_run AS run ON run.run_id=sample.collection_run_id
  JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
  WHERE sample.instance_id=p_instance_id
    AND sample.metric_key='engine.page_life_expectancy_seconds'
    AND sample.observed_at>=p_from_utc AND sample.observed_at<p_to_utc
    AND run.instance_id=p_instance_id AND run.target_revision=current_revision
    AND run.collector_id='engine.core' AND run.collector_version=1
    AND outcome.outcome='succeeded'
  LIMIT 25001
 ) AS bounded;
 IF bounded_count>25000 THEN RAISE EXCEPTION 'Buffer Manager PLE source cap exceeded' USING ERRCODE='54000'; END IF;
 RETURN QUERY
 SELECT date_bin(interval '1 hour',sample.observed_at,
                  '2000-01-01 00:00:00+00'::timestamptz),
        count(*),min(sample.metric_value),avg(sample.metric_value),max(sample.metric_value)
 FROM telemetry.raw_metric_sample AS sample
 JOIN telemetry.collection_run AS run ON run.run_id=sample.collection_run_id
 JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
 WHERE sample.instance_id=p_instance_id
   AND sample.metric_key='engine.page_life_expectancy_seconds'
   AND sample.observed_at>=p_from_utc AND sample.observed_at<p_to_utc
   AND run.instance_id=p_instance_id AND run.target_revision=current_revision
   AND run.collector_id='engine.core' AND run.collector_version=1
   AND outcome.outcome='succeeded'
 GROUP BY 1 ORDER BY 1 LIMIT 169;
END $aggregate$;

CREATE FUNCTION reporting.count_buffer_node_ple_gaps(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS bigint LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $gaps$
DECLARE current_revision bigint; gap_count bigint;
BEGIN
 IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
 THEN RAISE EXCEPTION 'Buffer Node PLE gap window bounds are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'Buffer Node PLE target scope is required' USING ERRCODE='42501'; END IF;
 SELECT target.revision INTO current_revision FROM control.observation_target AS target
 WHERE target.instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN 0; END IF;
 SELECT count(*) INTO gap_count FROM telemetry.visibility_gap AS gap
 JOIN telemetry.collection_run AS run ON run.run_id=gap.run_id
 WHERE gap.instance_id=p_instance_id AND gap.collector_id='engine.buffer-nodes'
   AND gap.gap_started_at<p_to_utc AND gap.gap_ended_at>=p_from_utc
   AND run.instance_id=p_instance_id AND run.target_revision=current_revision
   AND run.collector_version=1;
 RETURN gap_count;
END $gaps$;

REVOKE ALL ON FUNCTION reporting.list_buffer_node_ple_nodes(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.get_buffer_node_ple_trend(uuid,integer,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.count_buffer_node_ple_gaps(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.get_buffer_manager_ple_trend(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_buffer_node_ple_nodes(uuid,timestamptz,timestamptz)
 TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.get_buffer_node_ple_trend(uuid,integer,timestamptz,timestamptz)
 TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.count_buffer_node_ple_gaps(uuid,timestamptz,timestamptz)
 TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.get_buffer_manager_ple_trend(uuid,timestamptz,timestamptz)
 TO sqlobserver_server;
