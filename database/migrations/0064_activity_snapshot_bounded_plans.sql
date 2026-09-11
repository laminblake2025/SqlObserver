-- Keep the exact M5 evidence predicates inside the existing protected functions.

-- This permits latest-run and page bounds to precede historical joins without

-- changing the view, privileges, target/revision checks or cursor semantics.

SET LOCAL ROLE sqlobserver_migrator;

SET LOCAL lock_timeout='5s';

CREATE OR REPLACE FUNCTION reporting.list_activity_sessions
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_session_id integer,
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
    status_code text,
    is_user_process boolean,
    database_id integer,
    open_transaction_count integer,
    cpu_ms bigint,
    memory_usage_pages bigint,
    reads bigint,
    writes bigint,
    logical_reads bigint,
    total_elapsed_ms bigint,
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
        RAISE EXCEPTION 'activity-session query bounds are invalid' USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH repository_clock AS MATERIALIZED
    (
        SELECT clock_timestamp() AS value
    ),
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
            target.instance_id,
            target.revision,
            evidence.run_id,
            evidence.collector_id,
            evidence.outcome,
            evidence.reason_code,
            evidence.loss_kind,
            evidence.loss_count_exact,
            evidence.lost_row_count,
            evidence.lost_byte_count,
            evidence.completed_at,
            repository_clock.value AS repository_time,
            CASE
                WHEN p_snapshot_run_id IS NULL
                 AND p_snapshot_target_revision IS NULL
                 AND p_after_session_id IS NULL THEN true
                WHEN p_snapshot_run_id IS NOT NULL
                 AND p_snapshot_target_revision IS NOT NULL
                 AND p_after_session_id IS NOT NULL
                 AND evidence.run_id = p_snapshot_run_id
                 AND target.revision = p_snapshot_target_revision THEN true
                ELSE false
            END AS cursor_valid
        FROM target_state AS target
        CROSS JOIN repository_clock
        LEFT JOIN LATERAL
        (
            SELECT candidate.*
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
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS candidate
            WHERE candidate.instance_id = target.instance_id
              AND candidate.target_revision = target.revision
              AND candidate.collector_id = 'activity.sessions'
              AND (p_snapshot_run_id IS NULL OR
                   (candidate.run_id = p_snapshot_run_id AND candidate.target_revision = p_snapshot_target_revision))
            ORDER BY candidate.completed_at DESC, candidate.run_id DESC
            LIMIT 1
        ) AS evidence ON true
    ),
    page AS MATERIALIZED
    (
        SELECT item.*
        FROM telemetry.activity_session_snapshot AS item
        CROSS JOIN scope
        WHERE scope.cursor_valid
          AND item.instance_id = scope.instance_id
          AND item.target_revision = scope.revision
          AND item.collection_run_id = scope.run_id
          AND (p_after_session_id IS NULL OR item.session_id > p_after_session_id)
        ORDER BY item.session_id
        LIMIT p_max_results + 1
    )
    SELECT
        scope.instance_id,
        scope.run_id,
        CASE WHEN scope.run_id IS NULL THEN NULL::bigint ELSE scope.revision END,
        scope.collector_id,
        scope.outcome,
        scope.reason_code,
        scope.loss_kind,
        scope.loss_count_exact,
        scope.lost_row_count,
        scope.lost_byte_count,
        scope.completed_at,
        page.observed_at,
        page.session_id,
        page.status_code,
        page.is_user_process,
        page.database_id,
        page.open_transaction_count,
        page.cpu_ms,
        page.memory_usage_pages,
        page.reads,
        page.writes,
        page.logical_reads,
        page.total_elapsed_ms,
        (SELECT count(*) > p_max_results FROM page),
        scope.cursor_valid,
        scope.repository_time
    FROM scope
    LEFT JOIN page ON true
    ORDER BY page.session_id NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;

CREATE OR REPLACE FUNCTION reporting.list_activity_requests
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
            SELECT candidate.*
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
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS candidate
            WHERE candidate.instance_id = target.instance_id
              AND candidate.target_revision = target.revision
              AND candidate.collector_id = 'activity.requests'
              AND (p_snapshot_run_id IS NULL OR
                   (candidate.run_id = p_snapshot_run_id AND candidate.target_revision = p_snapshot_target_revision))
            ORDER BY candidate.completed_at DESC, candidate.run_id DESC
            LIMIT 1
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

CREATE OR REPLACE FUNCTION reporting.list_current_blocking
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_blocked_session_id integer,
    p_after_blocker_kind text,
    p_after_blocker_session_id integer,
    p_after_wait_type text,
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
    blocked_session_id integer,
    blocker_kind text,
    blocker_session_id integer,
    wait_type text,
    waiting_task_count bigint,
    wait_duration_ms bigint,
    root_blocker_session_id integer,
    chain_depth integer,
    chain_state text,
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
    IF p_instance_id IS NULL OR p_max_results NOT BETWEEN 1 AND 256 THEN
        RAISE EXCEPTION 'current-blocking query bounds are invalid' USING ERRCODE = '22023';
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
                 AND p_after_blocked_session_id IS NULL AND p_after_blocker_kind IS NULL
                 AND p_after_blocker_session_id IS NULL AND p_after_wait_type IS NULL THEN true
                WHEN p_snapshot_run_id IS NOT NULL AND p_snapshot_target_revision IS NOT NULL
                 AND p_after_blocked_session_id IS NOT NULL AND p_after_blocker_kind IS NOT NULL
                 AND p_after_wait_type IS NOT NULL
                 AND evidence.run_id = p_snapshot_run_id
                 AND target.revision = p_snapshot_target_revision THEN true
                ELSE false
            END AS cursor_valid
        FROM target_state AS target
        CROSS JOIN repository_clock
        LEFT JOIN LATERAL
        (
            SELECT candidate.*
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
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS candidate
            WHERE candidate.instance_id = target.instance_id
              AND candidate.target_revision = target.revision
              AND candidate.collector_id = 'blocking.current'
              AND (p_snapshot_run_id IS NULL OR
                   (candidate.run_id = p_snapshot_run_id AND candidate.target_revision = p_snapshot_target_revision))
            ORDER BY candidate.completed_at DESC, candidate.run_id DESC
            LIMIT 1
        ) AS evidence ON true
    ),
    page AS MATERIALIZED
    (
        SELECT item.*
        FROM events.blocking_edge AS item
        CROSS JOIN scope
        WHERE scope.cursor_valid
          AND item.instance_id = scope.instance_id
          AND item.target_revision = scope.revision
          AND item.collection_run_id = scope.run_id
          AND
          (
              p_after_blocked_session_id IS NULL
              OR (item.blocked_session_id, item.blocker_kind,
                  coalesce(item.blocker_session_id, 0), item.wait_type)
                 > (p_after_blocked_session_id, p_after_blocker_kind,
                    coalesce(p_after_blocker_session_id, 0), p_after_wait_type)
          )
        ORDER BY item.blocked_session_id, item.blocker_kind,
                 coalesce(item.blocker_session_id, 0), item.wait_type
        LIMIT p_max_results + 1
    )
    SELECT
        scope.instance_id, scope.run_id,
        CASE WHEN scope.run_id IS NULL THEN NULL::bigint ELSE scope.revision END,
        scope.collector_id, scope.outcome, scope.reason_code, scope.loss_kind,
        scope.loss_count_exact, scope.lost_row_count, scope.lost_byte_count,
        scope.completed_at, page.observed_at, page.blocked_session_id,
        page.blocker_kind, page.blocker_session_id, page.wait_type,
        page.waiting_task_count, page.wait_duration_ms,
        page.root_blocker_session_id, page.chain_depth, page.chain_state,
        (SELECT count(*) > p_max_results FROM page), scope.cursor_valid,
        scope.repository_time
    FROM scope
    LEFT JOIN page ON true
    ORDER BY page.blocked_session_id NULLS LAST, page.blocker_kind NULLS LAST,
             coalesce(page.blocker_session_id, 0), page.wait_type NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;

CREATE OR REPLACE FUNCTION reporting.list_blocking_history
(
    p_instance_id uuid,
    p_from_utc timestamptz,
    p_to_utc timestamptz,
    p_after_observed_at timestamptz,
    p_after_run_id uuid,
    p_after_blocked_session_id integer,
    p_after_blocker_kind text,
    p_after_blocker_session_id integer,
    p_after_wait_type text,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    observed_at timestamptz,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    blocked_session_id integer,
    blocker_kind text,
    blocker_session_id integer,
    wait_type text,
    waiting_task_count bigint,
    wait_duration_ms bigint,
    root_blocker_session_id integer,
    chain_depth integer,
    chain_state text,
    has_more boolean,
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
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc <= p_from_utc OR p_to_utc - p_from_utc > interval '24 hours'
       OR p_max_results NOT BETWEEN 1 AND 100
       OR NOT
       (
           (p_after_observed_at IS NULL AND p_after_run_id IS NULL
            AND p_after_blocked_session_id IS NULL AND p_after_blocker_kind IS NULL
            AND p_after_blocker_session_id IS NULL AND p_after_wait_type IS NULL)
           OR
           (p_after_observed_at IS NOT NULL AND p_after_run_id IS NOT NULL
            AND p_after_blocked_session_id IS NOT NULL AND p_after_blocker_kind IS NOT NULL
            AND p_after_wait_type IS NOT NULL)
       ) THEN
        RAISE EXCEPTION 'blocking-history query bounds or cursor are invalid'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH repository_clock AS MATERIALIZED (SELECT clock_timestamp() AS value),
    target_state AS MATERIALIZED
    (
        SELECT target.instance_id
        FROM control.observation_target AS target
        WHERE target.instance_id = p_instance_id
    ),
    page AS MATERIALIZED
    (
        SELECT edge.*, evidence.outcome, evidence.reason_code,
               evidence.loss_kind, evidence.loss_count_exact,
               evidence.lost_row_count, evidence.lost_byte_count,
               evidence.completed_at
        FROM events.blocking_edge AS edge
        INNER JOIN (SELECT
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
            ON evidence.instance_id = edge.instance_id
           AND evidence.run_id = edge.collection_run_id
           AND evidence.target_revision = edge.target_revision
           AND evidence.collector_id = 'blocking.current'
        INNER JOIN target_state AS target
            ON target.instance_id = edge.instance_id
        WHERE edge.observed_at >= p_from_utc
          AND edge.observed_at < p_to_utc
          AND
          (
              p_after_observed_at IS NULL
              OR (edge.observed_at, edge.collection_run_id, edge.blocked_session_id,
                  edge.blocker_kind, coalesce(edge.blocker_session_id, 0), edge.wait_type)
                 < (p_after_observed_at, p_after_run_id, p_after_blocked_session_id,
                    p_after_blocker_kind, coalesce(p_after_blocker_session_id, 0), p_after_wait_type)
          )
        ORDER BY edge.observed_at DESC, edge.collection_run_id DESC,
                 edge.blocked_session_id DESC, edge.blocker_kind DESC,
                 coalesce(edge.blocker_session_id, 0) DESC, edge.wait_type DESC
        LIMIT p_max_results + 1
    )
    SELECT
        target.instance_id, page.observed_at, page.collection_run_id,
        page.target_revision, page.outcome, page.reason_code, page.loss_kind,
        page.loss_count_exact, page.lost_row_count, page.lost_byte_count,
        page.completed_at, page.blocked_session_id, page.blocker_kind,
        page.blocker_session_id, page.wait_type, page.waiting_task_count,
        page.wait_duration_ms, page.root_blocker_session_id, page.chain_depth,
        page.chain_state, (SELECT count(*) > p_max_results FROM page),
        repository_clock.value
    FROM target_state AS target
    CROSS JOIN repository_clock
    LEFT JOIN page ON true
    ORDER BY page.observed_at DESC NULLS LAST, page.collection_run_id DESC NULLS LAST,
             page.blocked_session_id DESC NULLS LAST, page.blocker_kind DESC NULLS LAST,
             coalesce(page.blocker_session_id, 0) DESC, page.wait_type DESC NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;
