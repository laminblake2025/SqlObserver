-- Select the latest qualified completion through covering indexes before
-- fetching its full evidence header. Completion order, exact snapshot cursors,
-- target/revision fencing, row bounds, signatures and privileges are unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
CREATE OR REPLACE FUNCTION reporting.list_activity_requests_legacy
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_session_id integer,
    p_after_request_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    snapshot_run_id uuid,
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
    session_id integer,
    request_id integer,
    status_code text,
    command_code text,
    database_id integer,
    cpu_ms bigint,
    total_elapsed_ms bigint,
    reads bigint,
    writes bigint,
    logical_reads bigint,
    row_count bigint,
    percent_complete double precision,
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
SET plan_cache_mode = force_custom_plan
AS $sqlobserver$
BEGIN
    IF p_instance_id IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'activity-request query bounds are invalid' USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH repository_clock AS MATERIALIZED (SELECT clock_timestamp() AS value),
    target_state AS MATERIALIZED
    (
        SELECT target.instance_id, target.revision
        FROM control.observation_target AS target
        WHERE target.instance_id = p_instance_id
          AND target.lifecycle_state = 'active'
          AND target.host_name IS NOT NULL
    ),
    scope AS MATERIALIZED
    (
        SELECT
            target.instance_id, target.revision, evidence.run_id,
            evidence.collector_id, evidence.outcome, evidence.reason_code,
            evidence.loss_kind, evidence.loss_count_exact, evidence.lost_row_count,
            evidence.lost_byte_count, evidence.completed_at,
            repository_clock.value AS repository_time,
            CASE
                WHEN p_snapshot_run_id IS NULL AND p_snapshot_target_revision IS NULL
                 AND p_after_session_id IS NULL AND p_after_request_id IS NULL THEN true
                WHEN p_snapshot_run_id IS NOT NULL AND p_snapshot_target_revision IS NOT NULL
                 AND p_after_session_id IS NOT NULL AND p_after_request_id IS NOT NULL
                 AND evidence.run_id = p_snapshot_run_id
                 AND target.revision = p_snapshot_target_revision THEN true
                ELSE false
            END AS cursor_valid
        FROM target_state AS target
        CROSS JOIN repository_clock
        LEFT JOIN LATERAL
        (
            SELECT winner.instance_id, winner.run_id, winner.target_revision,
                   winner.collector_id, outcome.outcome, outcome.reason_code,
                   outcome.loss_kind, outcome.loss_count_exact,
                   outcome.lost_row_count, outcome.lost_byte_count,
                   outcome.completed_at
            FROM
            (
                SELECT run.instance_id, run.run_id,
                       run.target_revision, run.collector_id
                FROM telemetry.collection_run AS run
                CROSS JOIN LATERAL
                (
                    SELECT qualified.completed_at
                    FROM telemetry.collection_run_outcome AS qualified
                    WHERE qualified.run_id = run.run_id
                      AND qualified.outcome IN ('succeeded', 'partial')
                    -- Keep this lookup scoped to one run; do not reorder it
                    -- into a completion scan across unrelated fleet targets.
                    OFFSET 0
                ) AS completion
                WHERE run.instance_id = target.instance_id
                  AND run.target_revision = target.revision
                  AND run.collector_id = 'activity.requests'
                  AND (p_snapshot_run_id IS NULL OR
                       (run.run_id = p_snapshot_run_id
                        AND run.target_revision = p_snapshot_target_revision))
                ORDER BY completion.completed_at DESC, run.run_id DESC
                LIMIT 1
            ) AS winner
            JOIN telemetry.collection_run_outcome AS outcome
              ON outcome.run_id = winner.run_id
        ) AS evidence ON true
    ),
    page AS MATERIALIZED
    (
        SELECT item.*
        FROM telemetry.activity_request_snapshot AS item
        CROSS JOIN scope
        WHERE scope.cursor_valid
          AND item.instance_id = scope.instance_id
          AND item.target_revision = scope.revision
          AND item.collection_run_id = scope.run_id
          AND
          (
              p_after_session_id IS NULL
              OR (item.session_id, item.request_id) > (p_after_session_id, p_after_request_id)
          )
        ORDER BY item.session_id, item.request_id
        LIMIT p_max_results + 1
    )
    SELECT
        scope.instance_id, scope.run_id,
        CASE WHEN scope.run_id IS NULL THEN NULL::bigint ELSE scope.revision END,
        scope.collector_id, scope.outcome, scope.reason_code, scope.loss_kind,
        scope.loss_count_exact, scope.lost_row_count, scope.lost_byte_count,
        scope.completed_at, page.observed_at, page.session_id, page.request_id,
        page.status_code, page.command_code, page.database_id, page.cpu_ms,
        page.total_elapsed_ms, page.reads, page.writes, page.logical_reads,
        page.row_count, page.percent_complete,
        (SELECT count(*) > p_max_results FROM page), scope.cursor_valid,
        scope.repository_time
    FROM scope
    LEFT JOIN page ON true
    ORDER BY page.session_id NULLS LAST, page.request_id NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;

CREATE OR REPLACE FUNCTION reporting.list_activity_requests_v2_shadow
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_session_id integer,
    p_after_request_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    snapshot_run_id uuid,
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
    session_id integer,
    request_id integer,
    status_code text,
    command_code text,
    database_id integer,
    cpu_ms bigint,
    total_elapsed_ms bigint,
    reads bigint,
    writes bigint,
    logical_reads bigint,
    row_count bigint,
    percent_complete double precision,
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
SET plan_cache_mode = force_custom_plan
AS $sqlobserver$
BEGIN
    IF p_instance_id IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'activity-request query bounds are invalid' USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH repository_clock AS MATERIALIZED (SELECT clock_timestamp() AS value),
    target_state AS MATERIALIZED
    (
        SELECT target.instance_id, target.revision
        FROM control.observation_target AS target
        WHERE target.instance_id = p_instance_id
          AND target.lifecycle_state = 'active'
          AND target.host_name IS NOT NULL
    ),
    scope AS MATERIALIZED
    (
        SELECT
            target.instance_id, target.revision, evidence.run_id,
            evidence.collector_id, evidence.outcome, evidence.reason_code,
            evidence.loss_kind, evidence.loss_count_exact, evidence.lost_row_count,
            evidence.lost_byte_count, evidence.completed_at,
            repository_clock.value AS repository_time,
            CASE
                WHEN p_snapshot_run_id IS NULL AND p_snapshot_target_revision IS NULL
                 AND p_after_session_id IS NULL AND p_after_request_id IS NULL THEN true
                WHEN p_snapshot_run_id IS NOT NULL AND p_snapshot_target_revision IS NOT NULL
                 AND p_after_session_id IS NOT NULL AND p_after_request_id IS NOT NULL
                 AND evidence.run_id = p_snapshot_run_id
                 AND target.revision = p_snapshot_target_revision THEN true
                ELSE false
            END AS cursor_valid
        FROM target_state AS target
        CROSS JOIN repository_clock
        LEFT JOIN LATERAL
        (
            SELECT winner.instance_id, winner.run_id, winner.target_revision,
                   winner.collector_id, outcome.outcome, outcome.reason_code,
                   outcome.loss_kind, outcome.loss_count_exact,
                   outcome.lost_row_count, outcome.lost_byte_count,
                   outcome.completed_at
            FROM
            (
                SELECT run.instance_id, run.run_id,
                       run.target_revision, run.collector_id
                FROM telemetry.collection_run_v2 AS run
                CROSS JOIN LATERAL
                (
                    SELECT qualified.completed_at
                    FROM telemetry.collection_run_outcome AS qualified
                    WHERE qualified.run_id = run.run_id
                      AND qualified.outcome IN ('succeeded', 'partial')
                    -- Keep this lookup scoped to one run; do not reorder it
                    -- into a completion scan across unrelated fleet targets.
                    OFFSET 0
                ) AS completion
                WHERE run.instance_id = target.instance_id
                  AND run.target_revision = target.revision
                  AND run.collector_id = 'activity.requests'
                  AND (p_snapshot_run_id IS NULL OR
                       (run.run_id = p_snapshot_run_id
                        AND run.target_revision = p_snapshot_target_revision))
                ORDER BY completion.completed_at DESC, run.run_id DESC
                LIMIT 1
            ) AS winner
            JOIN telemetry.collection_run_outcome AS outcome
              ON outcome.run_id = winner.run_id
        ) AS evidence ON true
    ),
    page AS MATERIALIZED
    (
        SELECT item.*
        FROM telemetry.activity_request_snapshot AS item
        CROSS JOIN scope
        WHERE scope.cursor_valid
          AND item.instance_id = scope.instance_id
          AND item.target_revision = scope.revision
          AND item.collection_run_id = scope.run_id
          AND
          (
              p_after_session_id IS NULL
              OR (item.session_id, item.request_id) > (p_after_session_id, p_after_request_id)
          )
        ORDER BY item.session_id, item.request_id
        LIMIT p_max_results + 1
    )
    SELECT
        scope.instance_id, scope.run_id,
        CASE WHEN scope.run_id IS NULL THEN NULL::bigint ELSE scope.revision END,
        scope.collector_id, scope.outcome, scope.reason_code, scope.loss_kind,
        scope.loss_count_exact, scope.lost_row_count, scope.lost_byte_count,
        scope.completed_at, page.observed_at, page.session_id, page.request_id,
        page.status_code, page.command_code, page.database_id, page.cpu_ms,
        page.total_elapsed_ms, page.reads, page.writes, page.logical_reads,
        page.row_count, page.percent_complete,
        (SELECT count(*) > p_max_results FROM page), scope.cursor_valid,
        scope.repository_time
    FROM scope
    LEFT JOIN page ON true
    ORDER BY page.session_id NULLS LAST, page.request_id NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;
