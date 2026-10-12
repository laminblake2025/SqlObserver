-- Expose comparable Query Store group deltas for one opaque query/plan identity.
-- Baseline, reset and ambiguous rows remain counted but contribute no values.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.get_query_store_group_plan_trend(
 p_instance_id uuid,p_database_id integer,p_query_fingerprint bytea,
 p_plan_fingerprint bytea,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(bucket_start_utc timestamptz,source_rows bigint,
 runtime_comparable_rows bigint,runtime_baseline_rows bigint,
 runtime_reset_rows bigint,runtime_ambiguous_rows bigint,
 cpu_ms numeric,duration_ms numeric,execution_count numeric,
 logical_reads numeric,writes numeric,rows_processed numeric,
 wait_comparable_rows bigint,wait_baseline_rows bigint,
 wait_reset_rows bigint,wait_unavailable_rows bigint,
 wait_stale_rows bigint,wait_categories jsonb)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,control,events
SET TimeZone='UTC'
AS $group_trend$
BEGIN
 IF p_instance_id IS NULL OR p_database_id NOT BETWEEN 1 AND 32767
    OR octet_length(p_query_fingerprint)<>32
    OR octet_length(p_plan_fingerprint)<>32
    OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
    OR NOT control.query_performance_target_claim(p_instance_id)
 THEN RAISE EXCEPTION 'Query Store group trend preflight failed' USING ERRCODE='22023'; END IF;

 RETURN QUERY
 WITH source AS MATERIALIZED (
  SELECT date_bin(interval '1 hour',delta.observed_at,
                  '2000-01-01 00:00:00+00'::timestamptz) AS bucket,
         delta.runtime_transition,delta.cpu_ms,delta.duration_ms,
         delta.execution_count,delta.logical_reads,delta.writes,
         delta.rows_processed,delta.wait_transition,delta.wait_delta
  FROM events.query_store_group_delta AS delta
  JOIN control.observation_target AS target
    ON target.instance_id=delta.instance_id
   AND target.revision=delta.target_revision
  WHERE delta.instance_id=p_instance_id
    AND delta.database_id=p_database_id
    AND delta.query_fingerprint=p_query_fingerprint
    AND delta.plan_fingerprint=p_plan_fingerprint
    AND delta.observed_at>=p_from_utc AND delta.observed_at<p_to_utc
 ), metrics AS (
  SELECT source.bucket,count(*) AS source_rows,
         count(*) FILTER (WHERE source.runtime_transition='comparable') AS runtime_comparable_rows,
         count(*) FILTER (WHERE source.runtime_transition='baseline_unavailable') AS runtime_baseline_rows,
         count(*) FILTER (WHERE source.runtime_transition='reset') AS runtime_reset_rows,
         count(*) FILTER (WHERE source.runtime_transition='epoch_ambiguous') AS runtime_ambiguous_rows,
         sum(source.cpu_ms::numeric) AS cpu_ms,
         sum(source.duration_ms::numeric) AS duration_ms,
         sum(source.execution_count::numeric) AS execution_count,
         sum(source.logical_reads::numeric) AS logical_reads,
         sum(source.writes::numeric) AS writes,
         sum(source.rows_processed::numeric) AS rows_processed,
         count(*) FILTER (WHERE source.wait_transition='comparable') AS wait_comparable_rows,
         count(*) FILTER (WHERE source.wait_transition='baseline_unavailable') AS wait_baseline_rows,
         count(*) FILTER (WHERE source.wait_transition='reset') AS wait_reset_rows,
         count(*) FILTER (WHERE source.wait_transition='unavailable') AS wait_unavailable_rows,
         count(*) FILTER (WHERE source.wait_transition='stale') AS wait_stale_rows
  FROM source GROUP BY source.bucket
 ), category_totals AS (
  SELECT source.bucket,category.key,sum(category.value::numeric) AS milliseconds
  FROM source CROSS JOIN LATERAL jsonb_each_text(source.wait_delta) AS category(key,value)
  WHERE source.wait_transition='comparable'
  GROUP BY source.bucket,category.key
 ), category_maps AS (
  SELECT category_totals.bucket,
         jsonb_object_agg(category_totals.key,category_totals.milliseconds) AS categories
  FROM category_totals GROUP BY category_totals.bucket
 )
 SELECT metrics.bucket,metrics.source_rows,metrics.runtime_comparable_rows,
        metrics.runtime_baseline_rows,metrics.runtime_reset_rows,
        metrics.runtime_ambiguous_rows,metrics.cpu_ms,metrics.duration_ms,
        metrics.execution_count,metrics.logical_reads,metrics.writes,
        metrics.rows_processed,metrics.wait_comparable_rows,
        metrics.wait_baseline_rows,metrics.wait_reset_rows,
        metrics.wait_unavailable_rows,metrics.wait_stale_rows,
        coalesce(category_maps.categories,'{}'::jsonb)
 FROM metrics LEFT JOIN category_maps ON category_maps.bucket=metrics.bucket
 ORDER BY metrics.bucket
 LIMIT 169;
END $group_trend$;

REVOKE ALL ON FUNCTION control.get_query_store_group_plan_trend(
 uuid,integer,bytea,bytea,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_query_store_group_plan_trend(
 uuid,integer,bytea,bytea,timestamptz,timestamptz)
 TO sqlobserver_server;
