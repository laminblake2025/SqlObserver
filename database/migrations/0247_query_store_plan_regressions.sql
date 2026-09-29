-- Surface changed dominant plans with materially slower comparable runtime deltas.
-- This is an investigation lead, not proof that the plan caused the slowdown.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_query_store_plan_regressions(
    p_instance_id uuid, p_from_utc timestamptz, p_to_utc timestamptz,
    p_max_results integer)
RETURNS TABLE(database_id integer, query_fingerprint text,
    previous_plan_fingerprint text, current_plan_fingerprint text,
    previous_average_ms numeric, current_average_ms numeric,
    slowdown_ratio numeric, previous_executions numeric,
    current_executions numeric, previous_comparable_rows bigint,
    previous_source_rows bigint, current_comparable_rows bigint,
    current_source_rows bigint, last_observed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc <= p_from_utc OR p_to_utc-p_from_utc > interval '7 days'
       OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Plan regression bounds are invalid' USING ERRCODE='22023'; END IF;
    IF NOT control.query_performance_target_claim(p_instance_id)
    THEN RAISE EXCEPTION 'Plan regression target scope is required' USING ERRCODE='42501'; END IF;

    RETURN QUERY
    WITH source AS MATERIALIZED (
        SELECT CASE WHEN delta.observed_at < p_from_utc THEN 0 ELSE 1 END AS period,
               delta.database_id, delta.database_incarnation,
               delta.query_fingerprint, delta.plan_fingerprint,
               delta.observed_at, delta.runtime_transition,
               delta.duration_ms, delta.execution_count
        FROM events.query_store_group_delta AS delta
        JOIN control.observation_target AS target
          ON target.instance_id=delta.instance_id
         AND target.revision=delta.target_revision
        WHERE delta.instance_id=p_instance_id
          AND delta.observed_at>=p_from_utc-(p_to_utc-p_from_utc)
          AND delta.observed_at<p_to_utc
    ), grouped AS (
        SELECT source.period, source.database_id, source.database_incarnation,
               source.query_fingerprint, source.plan_fingerprint,
               count(*) AS source_rows,
               count(*) FILTER (WHERE source.runtime_transition='comparable'
                                AND source.execution_count>0) AS comparable_rows,
               sum(source.duration_ms::numeric) FILTER
                 (WHERE source.runtime_transition='comparable'
                  AND source.execution_count>0) AS duration_total,
               sum(source.execution_count::numeric) FILTER
                 (WHERE source.runtime_transition='comparable'
                  AND source.execution_count>0) AS executions,
               max(source.observed_at) AS last_observed
        FROM source
        GROUP BY source.period,source.database_id,source.database_incarnation,
                 source.query_fingerprint,source.plan_fingerprint
    ), dominant AS (
        SELECT grouped.*,
               row_number() OVER (PARTITION BY grouped.period,grouped.database_id,
                    grouped.database_incarnation,grouped.query_fingerprint
                    ORDER BY grouped.executions DESC NULLS LAST,
                             grouped.plan_fingerprint) AS plan_rank
        FROM grouped
    ), compared AS (
        SELECT current_plan.database_id,current_plan.query_fingerprint,
               previous_plan.plan_fingerprint AS previous_plan,
               current_plan.plan_fingerprint AS current_plan,
               previous_plan.duration_total/previous_plan.executions AS previous_average,
               current_plan.duration_total/current_plan.executions AS current_average,
               previous_plan.executions AS previous_count,
               current_plan.executions AS current_count,
               previous_plan.comparable_rows AS previous_comparable,
               previous_plan.source_rows AS previous_source,
               current_plan.comparable_rows AS current_comparable,
               current_plan.source_rows AS current_source,
               current_plan.last_observed AS latest
        FROM dominant AS current_plan
        JOIN dominant AS previous_plan
          ON previous_plan.period=0 AND current_plan.period=1
         AND previous_plan.database_id=current_plan.database_id
         AND previous_plan.database_incarnation=current_plan.database_incarnation
         AND previous_plan.query_fingerprint=current_plan.query_fingerprint
         AND previous_plan.plan_rank=1 AND current_plan.plan_rank=1
        WHERE previous_plan.plan_fingerprint<>current_plan.plan_fingerprint
          AND previous_plan.comparable_rows>=2 AND current_plan.comparable_rows>=2
          AND previous_plan.executions>=10 AND current_plan.executions>=10
    ), unique_queries AS (
        SELECT compared.*,
               row_number() OVER (PARTITION BY compared.database_id,
                    compared.query_fingerprint
                    ORDER BY compared.current_average/compared.previous_average DESC,
                             compared.latest DESC) AS query_rank
        FROM compared
        WHERE compared.previous_average>0
          AND compared.current_average>=compared.previous_average*2
          AND compared.current_average-compared.previous_average>=50
    )
    SELECT unique_queries.database_id,encode(unique_queries.query_fingerprint,'hex'),
           encode(unique_queries.previous_plan,'hex'),encode(unique_queries.current_plan,'hex'),
           round(unique_queries.previous_average,2),round(unique_queries.current_average,2),
           round(unique_queries.current_average/unique_queries.previous_average,2),
           unique_queries.previous_count,unique_queries.current_count,
           unique_queries.previous_comparable,unique_queries.previous_source,
           unique_queries.current_comparable,unique_queries.current_source,unique_queries.latest
    FROM unique_queries
    WHERE unique_queries.query_rank=1
    ORDER BY unique_queries.current_average/unique_queries.previous_average DESC,
             unique_queries.database_id,unique_queries.query_fingerprint
    LIMIT p_max_results;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_query_store_plan_regressions(
    uuid,timestamptz,timestamptz,integer)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_query_store_plan_regressions(
    uuid,timestamptz,timestamptz,integer) TO sqlobserver_server;
