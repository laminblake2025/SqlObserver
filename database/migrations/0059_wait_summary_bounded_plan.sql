-- Bound wait reads to the selected snapshot pair and current page before joining
-- baseline counters. Preserve target/revision, adjacency, reset and cursor checks.
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='5min';
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION reporting.list_server_wait_summary
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
FROM telemetry.collection_run AS run
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
FROM telemetry.collection_run AS run
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
