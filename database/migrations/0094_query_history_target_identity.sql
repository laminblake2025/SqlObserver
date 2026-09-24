-- Use direct target identity for keyed history while retaining the exact
-- query-owner path for historical NULL rows during bounded backfill.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE OR REPLACE FUNCTION control.get_query_history_projection(
  p_instance_id uuid, p_database_id integer, p_query_fingerprint bytea,
  p_from_utc timestamptz, p_to_utc timestamptz, p_limit integer,
  p_after_interval_end timestamptz, p_after_run_id uuid, p_after_plan bytea,
  p_after_observation_key bytea, p_snapshot_utc timestamptz)
RETURNS TABLE(
  database_id integer,query_fingerprint bytea,plan_fingerprint bytea,
  interval_start timestamptz,interval_end timestamptz,observed_at timestamptz,
  semantics text,cpu_ms bigint,duration_ms bigint,execution_count bigint,
  logical_reads bigint,writes bigint,rows_processed bigint,
  source events.query_performance_source,source_state text,coverage text,
  freshness boolean,truncated boolean,committed_at timestamptz,
  collection_run_id uuid,observation_key bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, control, events
SET plan_cache_mode = force_custom_plan AS $fn$
BEGIN
  IF p_limit IS NULL OR p_limit <= 0 OR p_limit > 200 OR
     p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc <= p_from_utc OR
     p_to_utc-p_from_utc > interval '7 days' OR p_snapshot_utc IS NULL OR
     p_database_id IS NULL OR p_database_id <= 0 OR
     p_query_fingerprint IS NULL OR octet_length(p_query_fingerprint) <> 32 THEN
    RAISE EXCEPTION 'query performance history projection bounds invalid' USING ERRCODE='22023';
  END IF;

  RETURN QUERY
  WITH window_observations AS NOT MATERIALIZED (
    SELECT keyed.*
    FROM events.query_performance_observation AS keyed
    WHERE keyed.instance_id=p_instance_id
      AND keyed.database_id=p_database_id
      AND keyed.query_fingerprint=p_query_fingerprint
      AND keyed.interval_end>=p_from_utc AND keyed.interval_end<=p_to_utc
      AND keyed.interval_start>=p_from_utc
    UNION ALL
    SELECT legacy.*
    FROM events.query_performance_observation AS legacy
    JOIN events.query_performance_query AS owner
      USING(collection_run_id,database_id,query_fingerprint)
    WHERE legacy.instance_id IS NULL AND owner.instance_id=p_instance_id
      AND legacy.database_id=p_database_id
      AND legacy.query_fingerprint=p_query_fingerprint
      AND legacy.interval_end>=p_from_utc AND legacy.interval_end<=p_to_utc
      AND legacy.interval_start>=p_from_utc
  )
  SELECT o.database_id,o.query_fingerprint,o.plan_fingerprint,
         o.interval_start,o.interval_end,o.observed_at,o.semantics,
         o.cpu_ms,o.duration_ms,o.execution_count,o.logical_reads,
         o.writes,o.rows_processed,o.source,o.source_state,
         r.coverage,r.freshness,r.truncated,r.committed_at,
         o.collection_run_id,o.observation_key
  FROM window_observations AS o
  JOIN events.query_performance_run AS r
    ON r.collection_run_id=o.collection_run_id
  WHERE r.instance_id=p_instance_id AND r.committed_at<=p_snapshot_utc
    AND control.query_performance_target_claim(p_instance_id)
    AND (p_after_interval_end IS NULL OR
      (o.interval_end>p_after_interval_end OR
       (o.interval_end=p_after_interval_end AND
        (o.collection_run_id>p_after_run_id OR
         (o.collection_run_id=p_after_run_id AND
          (coalesce(o.plan_fingerprint,''::bytea)>coalesce(p_after_plan,''::bytea) OR
           (coalesce(o.plan_fingerprint,''::bytea)=coalesce(p_after_plan,''::bytea) AND
            o.observation_key>coalesce(p_after_observation_key,''::bytea))))))))
  ORDER BY o.interval_end,o.collection_run_id,
           coalesce(o.plan_fingerprint,''::bytea),o.observation_key
  LIMIT p_limit + 1;
END;
$fn$;
