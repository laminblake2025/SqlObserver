-- Private M5 wait shadow readers over the partitioned run successor.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION reporting.list_server_wait_summary_v2_shadow
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_baseline_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_wait_type text,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    snapshot_run_id uuid,
    baseline_run_id uuid,
    snapshot_target_revision bigint,
    collector_id text,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    observed_at timestamptz,
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
    cursor_valid boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
BEGIN
    IF p_instance_id IS NULL OR p_max_results NOT BETWEEN 1 AND 100
       OR (p_after_wait_type IS NOT NULL AND p_after_wait_type !~ '^[A-Z][A-Z0-9_]{0,119}$') THEN
        RAISE EXCEPTION 'server-wait query bounds are invalid' USING ERRCODE = '22023';
    END IF;

    RETURN QUERY EXECUTE $query$
WITH repository_clock AS MATERIALIZED (SELECT clock_timestamp() AS value),
    target_state AS MATERIALIZED
    (
        SELECT target.instance_id, target.revision
        FROM control.observation_target AS target
        WHERE target.instance_id = $1
          AND target.lifecycle_state = 'active'
          AND target.host_name IS NOT NULL
    ),
    current_evidence AS MATERIALIZED
    (
        SELECT evidence.*
        FROM (SELECT
    run.instance_id,
    run.run_id,
    run.target_revision,
    run.collector_id,
    outcome.outcome,
    outcome.reason_code,
    outcome.loss_kind,
    outcome.loss_count_exact,
    outcome.lost_row_count,
    outcome.lost_byte_count,
    outcome.completed_at
FROM telemetry.collection_run_v2 AS run
INNER JOIN telemetry.collection_run_outcome AS outcome
    ON outcome.run_id = run.run_id
WHERE outcome.outcome IN ('succeeded', 'partial')
  AND run.collector_id IN
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS evidence
        INNER JOIN target_state AS target
            ON target.instance_id = evidence.instance_id
           AND target.revision = evidence.target_revision
        WHERE evidence.collector_id = 'waits.server'
          AND ($2 IS NULL OR
              (evidence.run_id = $2 AND evidence.target_revision = $4))
        ORDER BY evidence.completed_at DESC, evidence.run_id DESC
        LIMIT 1
    ),
    ranked AS MATERIALIZED
    (
        SELECT current_row.*, 1::bigint AS position FROM current_evidence AS current_row
        UNION ALL
        SELECT previous_evidence.*, 2::bigint AS position
        FROM current_evidence AS current_row
        CROSS JOIN LATERAL
        (
            SELECT evidence.*
            FROM (SELECT
    run.instance_id,
    run.run_id,
    run.target_revision,
    run.collector_id,
    outcome.outcome,
    outcome.reason_code,
    outcome.loss_kind,
    outcome.loss_count_exact,
    outcome.lost_row_count,
    outcome.lost_byte_count,
    outcome.completed_at
FROM telemetry.collection_run_v2 AS run
INNER JOIN telemetry.collection_run_outcome AS outcome
    ON outcome.run_id = run.run_id
WHERE outcome.outcome IN ('succeeded', 'partial')
  AND run.collector_id IN
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS evidence
            WHERE evidence.instance_id = current_row.instance_id
              AND evidence.target_revision = current_row.target_revision
              AND evidence.collector_id = 'waits.server'
              AND (evidence.completed_at,evidence.run_id) < (current_row.completed_at,current_row.run_id)
            ORDER BY evidence.completed_at DESC, evidence.run_id DESC
            LIMIT 1
        ) AS previous_evidence
    ),
    scope AS MATERIALIZED
    (
        SELECT
            target.instance_id, target.revision,
            current_run.run_id, baseline.run_id AS baseline_run_id,
            current_run.collector_id, current_run.outcome, current_run.reason_code,
            current_run.loss_kind, current_run.loss_count_exact,
            current_run.lost_row_count, current_run.lost_byte_count,
            current_run.completed_at, repository_clock.value AS repository_time,
            CASE
                WHEN $2 IS NULL AND $3 IS NULL
                 AND $4 IS NULL AND $5 IS NULL THEN true
                WHEN $2 IS NOT NULL
                 AND $4 IS NOT NULL
                 AND $5 IS NOT NULL
                 AND current_run.run_id = $2
                 AND baseline.run_id IS NOT DISTINCT FROM $3
                 AND target.revision = $4 THEN true
                ELSE false
            END AS cursor_valid
        FROM target_state AS target
        CROSS JOIN repository_clock
        LEFT JOIN ranked AS current_run
            ON (($2 IS NULL AND current_run.position = 1)
                OR ($2 IS NOT NULL AND current_run.run_id = $2
                    AND current_run.target_revision = $4))
        LEFT JOIN ranked AS baseline
            ON (($2 IS NULL AND baseline.position = 2)
                OR ($2 IS NOT NULL AND baseline.position = current_run.position + 1))
    ),
    current_page AS MATERIALIZED
    (
        SELECT current_wait.*
        FROM telemetry.server_wait_snapshot AS current_wait
        CROSS JOIN scope
        WHERE scope.cursor_valid
          AND current_wait.instance_id = scope.instance_id
          AND current_wait.target_revision = scope.revision
          AND current_wait.collection_run_id = scope.run_id
          AND ($5 IS NULL OR current_wait.wait_type > $5)
        ORDER BY current_wait.wait_type
        LIMIT $6 + 1
    ),
    page AS MATERIALIZED
    (
        SELECT
            current_wait.*,
            previous_wait.wait_type IS NOT NULL AS baseline_available,
            previous_wait.wait_type IS NOT NULL
            AND
            (
                current_wait.waiting_tasks_count < previous_wait.waiting_tasks_count
                OR current_wait.wait_time_ms < previous_wait.wait_time_ms
                OR current_wait.maximum_wait_time_ms < previous_wait.maximum_wait_time_ms
                OR current_wait.signal_wait_time_ms < previous_wait.signal_wait_time_ms
            ) AS reset_detected,
            previous_wait.waiting_tasks_count AS previous_tasks,
            previous_wait.wait_time_ms AS previous_wait_ms,
            previous_wait.signal_wait_time_ms AS previous_signal_ms
        FROM current_page AS current_wait
        CROSS JOIN scope
        LEFT JOIN telemetry.server_wait_snapshot AS previous_wait
            ON previous_wait.instance_id = scope.instance_id
           AND previous_wait.target_revision = scope.revision
           AND previous_wait.collection_run_id = scope.baseline_run_id
           AND previous_wait.wait_type = current_wait.wait_type
        WHERE scope.cursor_valid
          AND current_wait.instance_id = scope.instance_id
          AND current_wait.target_revision = scope.revision
          AND current_wait.collection_run_id = scope.run_id
          AND ($5 IS NULL OR current_wait.wait_type > $5)
        ORDER BY current_wait.wait_type
        LIMIT $6 + 1
    )
    SELECT
        scope.instance_id, scope.run_id, scope.baseline_run_id,
        CASE WHEN scope.run_id IS NULL THEN NULL::bigint ELSE scope.revision END,
        scope.collector_id, scope.outcome, scope.reason_code, scope.loss_kind,
        scope.loss_count_exact, scope.lost_row_count, scope.lost_byte_count,
        scope.completed_at, page.observed_at, page.wait_type,
        page.waiting_tasks_count, page.wait_time_ms, page.maximum_wait_time_ms,
        page.signal_wait_time_ms, page.baseline_available,
        coalesce(page.reset_detected, false),
        CASE WHEN page.baseline_available AND NOT page.reset_detected
            THEN page.waiting_tasks_count - page.previous_tasks ELSE NULL END,
        CASE WHEN page.baseline_available AND NOT page.reset_detected
            THEN page.wait_time_ms - page.previous_wait_ms ELSE NULL END,
        CASE WHEN page.baseline_available AND NOT page.reset_detected
            THEN page.signal_wait_time_ms - page.previous_signal_ms ELSE NULL END,
        (SELECT count(*) > $6 FROM page), scope.cursor_valid,
        scope.repository_time
    FROM scope
    LEFT JOIN page ON true
    ORDER BY page.wait_type NULLS LAST
    LIMIT $6
    $query$ USING p_instance_id,p_snapshot_run_id,p_baseline_run_id,p_snapshot_target_revision,p_after_wait_type,p_max_results;
END
$sqlobserver$;

CREATE FUNCTION reporting.list_server_wait_history_v2_shadow(
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
        JOIN telemetry.collection_run_v2 AS run
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
            FROM telemetry.collection_run_v2 AS prior
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

CREATE FUNCTION reporting.list_server_wait_category_trend_v2_shadow(
    p_instance_id uuid, p_from_utc timestamptz, p_to_utc timestamptz)
RETURNS TABLE (
    bucket_start timestamptz,
    category text,
    wait_ms numeric,
    run_count bigint,
    missing_summary_runs bigint,
    partial_runs bigint,
    incomparable_types bigint,
    comparable_types bigint,
    repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
SET plan_cache_mode = force_custom_plan
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc <= p_from_utc OR p_to_utc - p_from_utc > interval '24 hours' THEN
        RAISE EXCEPTION 'server-wait trend bounds are invalid' USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH target_state AS MATERIALIZED (
        SELECT target.instance_id FROM control.observation_target AS target
        WHERE target.instance_id = p_instance_id
    ), categories AS MATERIALIZED (
        SELECT label FROM (VALUES
            ('Lock'), ('I/O'), ('CPU/signal'), ('Memory'),
            ('Parallelism'), ('Log'), ('Other')) AS source(label)
    ), buckets AS MATERIALIZED (
        SELECT generate_series(
            date_bin(interval '5 minutes', p_from_utc, timestamptz '2000-01-01 00:00:00+00'),
            date_bin(interval '5 minutes', p_to_utc - interval '1 microsecond',
                     timestamptz '2000-01-01 00:00:00+00'),
            interval '5 minutes') AS start_at
    ), runs AS MATERIALIZED (
        SELECT result.run_id, result.outcome, result.loss_detected,
               result.server_wait_category_summary AS summary,
               date_bin(interval '5 minutes', result.completed_at,
                        timestamptz '2000-01-01 00:00:00+00') AS start_at
        FROM telemetry.collection_run_outcome AS result
        JOIN telemetry.collection_run_v2 AS run ON run.run_id = result.run_id
        JOIN target_state AS target ON target.instance_id = run.instance_id
        WHERE run.collector_id = 'waits.server'
          AND result.completed_at >= p_from_utc
          AND result.completed_at < p_to_utc
    ), run_quality AS (
        SELECT runs.start_at,
               count(*) AS total_runs,
               count(*) FILTER (WHERE runs.summary IS NULL) AS missing_runs,
               count(*) FILTER (WHERE runs.outcome <> 'succeeded'
                                OR runs.loss_detected) AS incomplete_runs
        FROM runs GROUP BY runs.start_at
    ), category_values AS (
        SELECT runs.start_at, item.value->>'category' AS label,
               sum((item.value->>'waitMilliseconds')::numeric) AS total_wait_ms,
               sum((item.value->>'incomparableTypes')::bigint)::bigint AS unknown_types,
               sum((item.value->>'comparableTypes')::bigint)::bigint AS known_types
        FROM runs
        CROSS JOIN LATERAL jsonb_array_elements(
            coalesce(runs.summary->'categories', '[]'::jsonb)) AS item(value)
        GROUP BY runs.start_at, item.value->>'category'
    )
    SELECT bucket.start_at, category.label,
           CASE WHEN coalesce(quality.total_runs, 0) > 0
                     AND coalesce(quality.missing_runs, 0) = 0
                     AND coalesce(quality.incomplete_runs, 0) = 0
                     AND coalesce(value.unknown_types, 0) = 0
                THEN coalesce(value.total_wait_ms, 0) END,
           coalesce(quality.total_runs, 0),
           coalesce(quality.missing_runs, 0),
           coalesce(quality.incomplete_runs, 0),
           coalesce(value.unknown_types, 0),
           coalesce(value.known_types, 0),
           statement_timestamp()
    FROM target_state AS target
    CROSS JOIN buckets AS bucket
    CROSS JOIN categories AS category
    LEFT JOIN run_quality AS quality ON quality.start_at = bucket.start_at
    LEFT JOIN category_values AS value
      ON value.start_at = bucket.start_at AND value.label = category.label
    ORDER BY bucket.start_at, category.label;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_server_wait_summary_v2_shadow(uuid,uuid,uuid,bigint,text,integer) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_wait_summary_v2_shadow(uuid,uuid,uuid,bigint,text,integer) TO sqlobserver_migrator;
REVOKE ALL ON FUNCTION reporting.list_server_wait_history_v2_shadow(uuid,timestamptz,timestamptz,timestamptz,uuid,text,integer) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_wait_history_v2_shadow(uuid,timestamptz,timestamptz,timestamptz,uuid,text,integer) TO sqlobserver_migrator;
REVOKE ALL ON FUNCTION reporting.list_server_wait_category_trend_v2_shadow(uuid,timestamptz,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_wait_category_trend_v2_shadow(uuid,timestamptz,timestamptz) TO sqlobserver_migrator;
