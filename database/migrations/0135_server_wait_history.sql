-- Historical server waits retain the exact adjacent-run baseline. Unknown
-- baselines and counter resets remain null, never invented zero-valued deltas.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_server_wait_history(
    p_instance_id uuid,
    p_from_utc timestamptz,
    p_to_utc timestamptz,
    p_after_observed_at timestamptz,
    p_after_run_id uuid,
    p_after_wait_type text,
    p_max_results integer)
RETURNS TABLE (
    target_instance_id uuid,
    observed_at timestamptz,
    snapshot_run_id uuid,
    baseline_run_id uuid,
    snapshot_target_revision bigint,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    wait_type text,
    waiting_tasks_count bigint,
    wait_time_ms bigint,
    maximum_wait_time_ms bigint,
    signal_wait_time_ms bigint,
    baseline_available boolean,
    reset_detected boolean,
    waiting_tasks_delta bigint,
    wait_time_ms_delta bigint,
    signal_wait_time_ms_delta bigint,
    has_more boolean,
    repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
SET plan_cache_mode = force_custom_plan
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc <= p_from_utc OR p_to_utc - p_from_utc > interval '24 hours'
       OR p_max_results NOT BETWEEN 1 AND 100
       OR NOT (
           (p_after_observed_at IS NULL AND p_after_run_id IS NULL
            AND p_after_wait_type IS NULL)
           OR
           (p_after_observed_at >= p_from_utc
            AND p_after_observed_at < p_to_utc
            AND p_after_run_id IS NOT NULL
            AND p_after_wait_type ~ '^[A-Z][A-Z0-9_]{0,119}$')
       ) THEN
        RAISE EXCEPTION 'server-wait history bounds or cursor are invalid'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH repository_clock AS MATERIALIZED (SELECT clock_timestamp() AS value),
    target_state AS MATERIALIZED (
        SELECT target.instance_id
        FROM control.observation_target AS target
        WHERE target.instance_id = p_instance_id
    ),
    page AS MATERIALIZED (
        SELECT current_wait.*,
               result.outcome, result.reason_code, result.loss_kind,
               result.loss_count_exact, result.lost_row_count,
               result.lost_byte_count, result.completed_at
        FROM telemetry.server_wait_snapshot AS current_wait
        JOIN telemetry.collection_run AS run
          ON run.run_id = current_wait.collection_run_id
         AND run.instance_id = current_wait.instance_id
         AND run.target_revision = current_wait.target_revision
         AND run.collector_id = 'waits.server'
        JOIN telemetry.collection_run_outcome AS result
          ON result.run_id = run.run_id
         AND result.outcome IN ('succeeded', 'partial')
        JOIN target_state AS target
          ON target.instance_id = current_wait.instance_id
        WHERE current_wait.observed_at >= p_from_utc
          AND current_wait.observed_at < p_to_utc
          AND (p_after_observed_at IS NULL OR
               (current_wait.observed_at, current_wait.collection_run_id,
                current_wait.wait_type) <
               (p_after_observed_at, p_after_run_id, p_after_wait_type))
        ORDER BY current_wait.observed_at DESC,
                 current_wait.collection_run_id DESC,
                 current_wait.wait_type DESC
        LIMIT p_max_results + 1
    ),
    compared AS MATERIALIZED (
        SELECT current_wait.*,
               prior_run.run_id AS prior_run_id,
               prior_wait.wait_type IS NOT NULL AS has_baseline,
               coalesce(prior_wait.wait_type IS NOT NULL AND (
                   current_wait.waiting_tasks_count < prior_wait.waiting_tasks_count
                   OR current_wait.wait_time_ms < prior_wait.wait_time_ms
                   OR current_wait.maximum_wait_time_ms < prior_wait.maximum_wait_time_ms
                   OR current_wait.signal_wait_time_ms < prior_wait.signal_wait_time_ms
               ), false) AS has_reset,
               prior_wait.waiting_tasks_count AS prior_tasks,
               prior_wait.wait_time_ms AS prior_wait_ms,
               prior_wait.signal_wait_time_ms AS prior_signal_ms
        FROM page AS current_wait
        LEFT JOIN LATERAL (
            SELECT prior.run_id
            FROM telemetry.collection_run AS prior
            JOIN telemetry.collection_run_outcome AS prior_result
              ON prior_result.run_id = prior.run_id
             AND prior_result.outcome IN ('succeeded', 'partial')
            WHERE prior.instance_id = current_wait.instance_id
              AND prior.target_revision = current_wait.target_revision
              AND prior.collector_id = 'waits.server'
              AND (prior_result.completed_at, prior.run_id) <
                  (current_wait.completed_at, current_wait.collection_run_id)
            ORDER BY prior_result.completed_at DESC, prior.run_id DESC
            LIMIT 1
        ) AS prior_run ON true
        LEFT JOIN telemetry.server_wait_snapshot AS prior_wait
          ON prior_wait.instance_id = current_wait.instance_id
         AND prior_wait.target_revision = current_wait.target_revision
         AND prior_wait.collection_run_id = prior_run.run_id
         AND prior_wait.wait_type = current_wait.wait_type
    )
    SELECT target.instance_id, compared.observed_at,
           compared.collection_run_id, compared.prior_run_id,
           compared.target_revision, compared.outcome, compared.reason_code,
           compared.loss_kind, compared.loss_count_exact,
           compared.lost_row_count, compared.lost_byte_count,
           compared.completed_at, compared.wait_type,
           compared.waiting_tasks_count, compared.wait_time_ms,
           compared.maximum_wait_time_ms, compared.signal_wait_time_ms,
           compared.has_baseline, compared.has_reset,
           CASE WHEN compared.has_baseline AND NOT compared.has_reset
                THEN compared.waiting_tasks_count - compared.prior_tasks END,
           CASE WHEN compared.has_baseline AND NOT compared.has_reset
                THEN compared.wait_time_ms - compared.prior_wait_ms END,
           CASE WHEN compared.has_baseline AND NOT compared.has_reset
                THEN compared.signal_wait_time_ms - compared.prior_signal_ms END,
           (SELECT count(*) > p_max_results FROM page),
           repository_clock.value
    FROM target_state AS target
    CROSS JOIN repository_clock
    LEFT JOIN compared ON true
    ORDER BY compared.observed_at DESC NULLS LAST,
             compared.collection_run_id DESC NULLS LAST,
             compared.wait_type DESC NULLS LAST
    LIMIT p_max_results;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_server_wait_history(
    uuid,timestamptz,timestamptz,timestamptz,uuid,text,integer)
    FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_wait_history(
    uuid,timestamptz,timestamptz,timestamptz,uuid,text,integer)
    TO sqlobserver_server;
