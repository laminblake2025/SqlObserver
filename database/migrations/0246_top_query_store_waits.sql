-- Rank measured Query Store wait deltas, keeping missing group coverage visible.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_top_query_store_waits(
    p_instance_id uuid, p_from_utc timestamptz, p_to_utc timestamptz,
    p_max_results integer)
RETURNS TABLE(database_id integer, query_fingerprint text,
    plan_fingerprint text, wait_milliseconds numeric,
    comparable_rows bigint, source_rows bigint, last_observed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc <= p_from_utc OR p_to_utc-p_from_utc > interval '7 days'
       OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Query wait ranking bounds are invalid' USING ERRCODE='22023'; END IF;
    IF NOT control.query_performance_target_claim(p_instance_id)
    THEN RAISE EXCEPTION 'Query wait target scope is required' USING ERRCODE='42501'; END IF;

    RETURN QUERY
    WITH source AS MATERIALIZED (
        SELECT delta.database_id, delta.query_fingerprint,
               delta.plan_fingerprint, delta.observed_at,
               delta.wait_transition,
               CASE WHEN delta.wait_transition='comparable' THEN
                   (SELECT COALESCE(sum(category.value::numeric),0)
                    FROM jsonb_each_text(delta.wait_delta) AS category(key,value))
               ELSE NULL END AS wait_ms
        FROM events.query_store_group_delta AS delta
        JOIN control.observation_target AS target
          ON target.instance_id=delta.instance_id
         AND target.revision=delta.target_revision
        WHERE delta.instance_id=p_instance_id
          AND delta.observed_at>=p_from_utc AND delta.observed_at<p_to_utc
    ), ranked AS (
        SELECT source.database_id, source.query_fingerprint,
               source.plan_fingerprint,
               sum(source.wait_ms) AS total_wait_ms,
               count(*) FILTER (WHERE source.wait_transition='comparable') AS comparable_count,
               count(*) AS source_count, max(source.observed_at) AS latest_at
        FROM source
        GROUP BY source.database_id, source.query_fingerprint, source.plan_fingerprint
        HAVING count(*) FILTER (WHERE source.wait_transition='comparable') > 0
    )
    SELECT ranked.database_id, encode(ranked.query_fingerprint,'hex'),
           encode(ranked.plan_fingerprint,'hex'), ranked.total_wait_ms,
           ranked.comparable_count, ranked.source_count, ranked.latest_at
    FROM ranked
    ORDER BY ranked.total_wait_ms DESC, ranked.database_id,
             ranked.query_fingerprint, ranked.plan_fingerprint
    LIMIT p_max_results;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_top_query_store_waits(
    uuid,timestamptz,timestamptz,integer)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_top_query_store_waits(
    uuid,timestamptz,timestamptz,integer) TO sqlobserver_server;
