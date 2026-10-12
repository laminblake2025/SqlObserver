-- Private shadow projections preserve the public M7 page contracts while
-- historical parity is established. No server role can call these yet.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.get_top_queries_projection_v3(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
 p_metric text,p_limit integer,p_after_database_id integer,
 p_after_interval_end timestamptz,p_after_query bytea,p_after_plan bytea,
 p_after_run_id uuid,p_after_metric bigint,p_after_observation_key bytea,
 p_snapshot_utc timestamptz)
RETURNS TABLE(database_id integer,query_fingerprint bytea,
 plan_fingerprint bytea,interval_start timestamptz,interval_end timestamptz,
 observed_at timestamptz,semantics text,cpu_ms bigint,duration_ms bigint,
 execution_count bigint,logical_reads bigint,writes bigint,rows_processed bigint,
 source events.query_performance_source,source_state text,coverage text,
 freshness boolean,truncated boolean,committed_at timestamptz,
 collection_run_id uuid,observation_key bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control,events
SET plan_cache_mode=force_custom_plan AS $top$
BEGIN
 IF p_limit IS NULL OR p_limit<=0 OR p_limit>200 OR
    p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc OR
    p_to_utc-p_from_utc>interval '7 days' OR p_snapshot_utc IS NULL OR
    p_metric NOT IN ('cpu','duration','executions','logical_reads','writes','rows') THEN
  RAISE EXCEPTION 'query performance top projection bounds invalid'
   USING ERRCODE='22023';
 END IF;
 RETURN QUERY
 WITH visible_runs AS MATERIALIZED (
  SELECT run.collection_run_id,run.coverage,run.freshness,
   run.truncated,run.committed_at
  FROM events.query_performance_run AS run
  WHERE run.instance_id=p_instance_id AND run.committed_at<=p_snapshot_utc
    AND control.query_performance_target_claim(p_instance_id)
 ), latest_observations AS NOT MATERIALIZED (
  SELECT DISTINCT ON (o.database_id,o.query_fingerprint,o.plan_fingerprint,
                      o.source,o.semantics)
   o.database_id,o.query_fingerprint,o.plan_fingerprint,o.interval_start,
   o.interval_end,o.observed_at,o.semantics,o.cpu_ms,o.duration_ms,
   o.execution_count,o.logical_reads,o.writes,o.rows_processed,o.source,
   o.source_state,r.coverage,r.freshness,r.truncated,r.committed_at,
   o.collection_run_id,o.observation_key
  FROM events.query_performance_observation_v3 AS o
  JOIN visible_runs AS r ON r.collection_run_id=o.collection_run_id
  WHERE o.instance_id=p_instance_id AND o.interval_end>=p_from_utc
    AND o.interval_end<=p_to_utc AND o.interval_start>=p_from_utc
  ORDER BY o.database_id,o.query_fingerprint,o.plan_fingerprint,o.source,
   o.semantics,o.interval_end DESC,o.observed_at DESC,r.committed_at DESC,
   o.collection_run_id DESC,o.observation_key DESC
 ), candidates AS NOT MATERIALIZED (
  SELECT latest.*,
   CASE p_metric WHEN 'cpu' THEN latest.cpu_ms
    WHEN 'duration' THEN latest.duration_ms
    WHEN 'executions' THEN latest.execution_count
    WHEN 'logical_reads' THEN latest.logical_reads
    WHEN 'writes' THEN latest.writes
    ELSE latest.rows_processed END AS metric_value
  FROM latest_observations AS latest
 )
 SELECT candidate.database_id,candidate.query_fingerprint,
  candidate.plan_fingerprint,candidate.interval_start,candidate.interval_end,
  candidate.observed_at,candidate.semantics,candidate.cpu_ms,
  candidate.duration_ms,candidate.execution_count,candidate.logical_reads,
  candidate.writes,candidate.rows_processed,candidate.source,
  candidate.source_state,candidate.coverage,candidate.freshness,
  candidate.truncated,candidate.committed_at,candidate.collection_run_id,
  candidate.observation_key
 FROM candidates AS candidate
 WHERE candidate.metric_value IS NOT NULL AND candidate.metric_value>=0
   AND (p_after_query IS NULL OR candidate.metric_value<p_after_metric OR
    (candidate.metric_value=p_after_metric AND
     (candidate.database_id,candidate.interval_end,candidate.query_fingerprint,
      coalesce(candidate.plan_fingerprint,''::bytea),
      candidate.collection_run_id,candidate.observation_key) >
     (p_after_database_id,p_after_interval_end,p_after_query,
      coalesce(p_after_plan,''::bytea),p_after_run_id,
      coalesce(p_after_observation_key,''::bytea))))
 ORDER BY candidate.metric_value DESC,candidate.database_id,
  candidate.interval_end,candidate.query_fingerprint,
  coalesce(candidate.plan_fingerprint,''::bytea),candidate.collection_run_id,
  candidate.observation_key
 LIMIT p_limit+1;
END $top$;
REVOKE ALL ON FUNCTION control.get_top_queries_projection_v3(
 uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,
 bytea,bytea,uuid,bigint,bytea,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_top_queries_projection_v3(
 uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,
 bytea,bytea,uuid,bigint,bytea,timestamptz) TO sqlobserver_migrator;

CREATE FUNCTION control.get_query_history_projection_v3(
 p_instance_id uuid,p_database_id integer,p_query_fingerprint bytea,
 p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,
 p_after_interval_end timestamptz,p_after_run_id uuid,p_after_plan bytea,
 p_after_observation_key bytea,p_snapshot_utc timestamptz)
RETURNS TABLE(database_id integer,query_fingerprint bytea,
 plan_fingerprint bytea,interval_start timestamptz,interval_end timestamptz,
 observed_at timestamptz,semantics text,cpu_ms bigint,duration_ms bigint,
 execution_count bigint,logical_reads bigint,writes bigint,rows_processed bigint,
 source events.query_performance_source,source_state text,coverage text,
 freshness boolean,truncated boolean,committed_at timestamptz,
 collection_run_id uuid,observation_key bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control,events
SET plan_cache_mode=force_custom_plan AS $history$
BEGIN
 IF p_limit IS NULL OR p_limit<=0 OR p_limit>200 OR
    p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc OR
    p_to_utc-p_from_utc>interval '7 days' OR p_snapshot_utc IS NULL OR
    p_database_id IS NULL OR p_database_id<=0 OR
    p_query_fingerprint IS NULL OR octet_length(p_query_fingerprint)<>32 THEN
  RAISE EXCEPTION 'query performance history projection bounds invalid'
   USING ERRCODE='22023';
 END IF;
 RETURN QUERY
 SELECT o.database_id,o.query_fingerprint,o.plan_fingerprint,
  o.interval_start,o.interval_end,o.observed_at,o.semantics,o.cpu_ms,
  o.duration_ms,o.execution_count,o.logical_reads,o.writes,o.rows_processed,
  o.source,o.source_state,r.coverage,r.freshness,r.truncated,r.committed_at,
  o.collection_run_id,o.observation_key
 FROM events.query_performance_observation_v3 AS o
 JOIN events.query_performance_run AS r
  ON r.collection_run_id=o.collection_run_id
 WHERE o.instance_id=p_instance_id AND o.database_id=p_database_id
   AND o.query_fingerprint=p_query_fingerprint
   AND o.interval_end>=p_from_utc AND o.interval_end<=p_to_utc
   AND o.interval_start>=p_from_utc
   AND r.instance_id=p_instance_id AND r.committed_at<=p_snapshot_utc
   AND control.query_performance_target_claim(p_instance_id)
   AND (p_after_interval_end IS NULL OR
    (o.interval_end>p_after_interval_end OR
     (o.interval_end=p_after_interval_end AND
      (o.collection_run_id>p_after_run_id OR
       (o.collection_run_id=p_after_run_id AND
        (coalesce(o.plan_fingerprint,''::bytea)>
         coalesce(p_after_plan,''::bytea) OR
         (coalesce(o.plan_fingerprint,''::bytea)=
          coalesce(p_after_plan,''::bytea) AND
          o.observation_key>
          coalesce(p_after_observation_key,''::bytea))))))))
 ORDER BY o.interval_end,o.collection_run_id,
  coalesce(o.plan_fingerprint,''::bytea),o.observation_key
 LIMIT p_limit+1;
END $history$;
REVOKE ALL ON FUNCTION control.get_query_history_projection_v3(
 uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,
 uuid,bytea,bytea,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_query_history_projection_v3(
 uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,
 uuid,bytea,bytea,timestamptz) TO sqlobserver_migrator;
