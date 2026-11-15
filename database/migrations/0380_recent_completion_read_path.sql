-- Prefer indexed recent completions, with an exact historical fallback.
-- A recent winner is also the global winner: every omitted completion is older.
-- Exact snapshot reads bypass history selection; evidence/cursor contracts remain unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.find_completed_run(
    p_instance_id uuid,
    p_collector_id text,
    p_target_revision bigint,
    p_schedule_revision bigint DEFAULT NULL,
    p_qualified_only boolean DEFAULT true,
    p_exact_run_id uuid DEFAULT NULL,
    p_before_completed_at timestamptz DEFAULT NULL,
    p_before_run_id uuid DEFAULT NULL)
RETURNS uuid
LANGUAGE plpgsql STABLE PARALLEL RESTRICTED
SET search_path = pg_catalog
SET TimeZone = 'UTC'
SET plan_cache_mode = force_custom_plan
AS $find_run$
DECLARE
    selected_run uuid;
    recent_start timestamptz := coalesce(p_before_completed_at, statement_timestamp()) - interval '1 hour';
BEGIN
    IF p_instance_id IS NULL OR p_collector_id IS NULL OR p_target_revision IS NULL THEN
        RETURN NULL;
    END IF;
    IF p_exact_run_id IS NOT NULL THEN
        SELECT run.run_id INTO selected_run
        FROM telemetry.collection_run AS run
        JOIN telemetry.collection_run_outcome AS completion ON completion.run_id = run.run_id
        WHERE run.run_id = p_exact_run_id
          AND run.instance_id = p_instance_id
          AND run.collector_id = p_collector_id
          AND run.target_revision = p_target_revision
          AND (p_schedule_revision IS NULL OR run.schedule_revision = p_schedule_revision)
          AND (NOT p_qualified_only OR completion.outcome IN ('succeeded', 'partial'));
        RETURN selected_run;
    END IF;

    SELECT run.run_id INTO selected_run
    FROM (
        SELECT outcome.run_id, outcome.completed_at, outcome.outcome
        FROM telemetry.collection_run_outcome AS outcome
        WHERE outcome.completed_at >= recent_start
          AND (p_before_completed_at IS NULL OR
               (outcome.completed_at, outcome.run_id) < (p_before_completed_at, p_before_run_id))
        -- Retain the indexed completion range before joining target metadata.
        OFFSET 0
    ) AS completion
    JOIN telemetry.collection_run AS run ON run.run_id = completion.run_id
    WHERE run.instance_id = p_instance_id
      AND run.collector_id = p_collector_id
      AND run.target_revision = p_target_revision
      AND (p_schedule_revision IS NULL OR run.schedule_revision = p_schedule_revision)
      AND (NOT p_qualified_only OR completion.outcome IN ('succeeded', 'partial'))
    ORDER BY completion.completed_at DESC, run.run_id DESC
    LIMIT 1;
    IF selected_run IS NOT NULL THEN
        RETURN selected_run;
    END IF;

    -- Stale, inactive and never-collected targets retain their original semantics.
    SELECT run.run_id INTO selected_run
    FROM telemetry.collection_run AS run
    CROSS JOIN LATERAL (
        SELECT outcome.completed_at
        FROM telemetry.collection_run_outcome AS outcome
        WHERE outcome.run_id = run.run_id
          AND (NOT p_qualified_only OR outcome.outcome IN ('succeeded', 'partial'))
        OFFSET 0
    ) AS completion
    WHERE run.instance_id = p_instance_id
      AND run.collector_id = p_collector_id
      AND run.target_revision = p_target_revision
      AND (p_schedule_revision IS NULL OR run.schedule_revision = p_schedule_revision)
      AND (p_before_completed_at IS NULL OR
           (completion.completed_at, run.run_id) < (p_before_completed_at, p_before_run_id))
    ORDER BY completion.completed_at DESC, run.run_id DESC
    LIMIT 1;
    RETURN selected_run;
END
$find_run$;
REVOKE ALL ON FUNCTION reporting.find_completed_run(uuid,text,bigint,bigint,boolean,uuid,timestamptz,uuid)
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

CREATE OR REPLACE FUNCTION reporting.list_activity_sessions_legacy
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
            FROM (SELECT reporting.find_completed_run(target.instance_id, 'activity.sessions', target.revision, NULL, true, p_snapshot_run_id) AS run_id OFFSET 0) AS chosen
            JOIN (SELECT
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
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS candidate ON candidate.run_id = chosen.run_id
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

CREATE OR REPLACE FUNCTION reporting.list_current_blocking_legacy
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
            FROM (SELECT reporting.find_completed_run(target.instance_id, 'blocking.current', target.revision, NULL, true, p_snapshot_run_id) AS run_id OFFSET 0) AS chosen
            JOIN (SELECT
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
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS candidate ON candidate.run_id = chosen.run_id
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
                FROM (SELECT reporting.find_completed_run(target.instance_id, 'activity.requests', target.revision, NULL, true, p_snapshot_run_id) AS run_id OFFSET 0) AS chosen
            JOIN telemetry.collection_run AS run ON run.run_id = chosen.run_id
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

CREATE OR REPLACE FUNCTION reporting.list_server_wait_summary_legacy
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
        FROM target_state AS target
        CROSS JOIN LATERAL (SELECT reporting.find_completed_run(target.instance_id, 'waits.server', target.revision, NULL, true, $2) AS run_id OFFSET 0) AS chosen
        JOIN (SELECT
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
            ON evidence.run_id = chosen.run_id
           AND target.instance_id = evidence.instance_id
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
            FROM (SELECT reporting.find_completed_run(current_row.instance_id, 'waits.server', current_row.target_revision, NULL, true, NULL, current_row.completed_at, current_row.run_id) AS run_id OFFSET 0) AS chosen
            JOIN (SELECT
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
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current')) AS evidence ON evidence.run_id = chosen.run_id
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

CREATE OR REPLACE VIEW reporting.collector_health_projection_legacy
WITH (security_barrier = true)
AS
SELECT
    schedule.instance_id,
    schedule.collector_id,
    schedule.collector_version,
    contract.output_schema_version,
    schedule.schedule_revision,
    schedule.target_revision AS schedule_target_revision,
    schedule.enabled,
    schedule.collection_interval,
    schedule.next_due_at,
    CASE
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until <= repository_clock.value
            THEN 'half_open'
        ELSE schedule.circuit_state
    END AS circuit_state,
    schedule.consecutive_failure_count,
    CASE
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until <= repository_clock.value
            THEN NULL::timestamptz
        ELSE schedule.circuit_open_until
    END AS circuit_open_until,
    schedule.last_started_at,
    schedule.last_completed_at,
    schedule.last_succeeded_at,
    latest_data_run.run_id AS latest_data_run_id,
    latest_run.run_id,
    latest_run.target_revision,
    latest_run.scheduled_for,
    latest_run.started_at,
    latest_outcome.outcome,
    latest_outcome.reason_code,
    latest_outcome.attempt_count,
    latest_outcome.retry_count,
    latest_outcome.duration_ms,
    latest_outcome.source_row_count,
    latest_outcome.output_item_count,
    latest_outcome.inserted_item_count,
    latest_outcome.duplicate_item_count,
    latest_outcome.rejected_item_count,
    latest_outcome.response_bytes,
    latest_outcome.output_bytes,
    latest_outcome.persisted_bytes,
    latest_outcome.loss_kind,
    latest_outcome.loss_count_exact,
    latest_outcome.lost_row_count,
    latest_outcome.lost_byte_count,
    latest_outcome.completed_at,
    CASE
        WHEN NOT schedule.enabled THEN 'disabled'
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until > repository_clock.value THEN 'unavailable'
        WHEN latest_outcome.run_id IS NULL THEN 'pending'
        WHEN latest_outcome.outcome = 'succeeded'
            AND latest_outcome.completed_at + schedule.collection_interval * 2 >= repository_clock.value
            THEN 'current'
        WHEN latest_outcome.outcome = 'succeeded' THEN 'stale'
        WHEN latest_outcome.outcome IN ('partial', 'output_invalid') THEN 'degraded'
        WHEN latest_outcome.outcome = 'unsupported' THEN 'unsupported'
        ELSE 'unavailable'
    END AS health_state,
    CASE
        WHEN NOT schedule.enabled THEN 'none'
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until > repository_clock.value THEN 'circuit_open'
        WHEN latest_outcome.run_id IS NULL THEN 'never_collected'
        WHEN latest_outcome.outcome = 'succeeded'
            AND latest_outcome.completed_at + schedule.collection_interval * 2 >= repository_clock.value
            THEN 'none'
        WHEN latest_outcome.outcome = 'succeeded' THEN 'evidence_stale'
        WHEN latest_outcome.outcome = 'partial' THEN 'sample_loss'
        WHEN latest_outcome.outcome = 'permission_denied' THEN 'permission_denied'
        WHEN latest_outcome.outcome = 'timed_out' THEN 'timed_out'
        WHEN latest_outcome.outcome = 'output_invalid' THEN 'output_invalid'
        WHEN latest_outcome.reason_code = 'capability_profile_missing' THEN 'capability_profile_missing'
        WHEN latest_outcome.reason_code = 'capability_profile_stale' THEN 'capability_profile_stale'
        WHEN latest_outcome.reason_code = 'capability_missing' THEN 'capability_missing'
        WHEN latest_outcome.reason_code = 'target_version_unsupported' THEN 'version_unsupported'
        WHEN latest_outcome.reason_code = 'target_platform_unsupported' THEN 'platform_unsupported'
        WHEN latest_outcome.reason_code = 'target_edition_unsupported' THEN 'edition_unsupported'
        ELSE 'collection_failed'
    END AS health_reason,
    repository_clock.value AS repository_time
FROM control.collector_schedule AS schedule
INNER JOIN control.collector_contract AS contract
    ON contract.collector_id = schedule.collector_id
   AND contract.collector_version = schedule.collector_version
INNER JOIN control.observation_target AS target
    ON target.instance_id = schedule.instance_id
   AND target.revision = schedule.target_revision
   AND target.lifecycle_state = 'active'
   AND target.host_name IS NOT NULL
CROSS JOIN LATERAL (SELECT statement_timestamp() AS value) AS repository_clock
LEFT JOIN LATERAL
(
    SELECT run.*
    FROM (SELECT reporting.find_completed_run(schedule.instance_id, schedule.collector_id, schedule.target_revision, schedule.schedule_revision, false) AS run_id OFFSET 0) AS chosen
            JOIN telemetry.collection_run AS run ON run.run_id = chosen.run_id
    INNER JOIN telemetry.collection_run_outcome AS outcome
        ON outcome.run_id = run.run_id
    WHERE run.instance_id = schedule.instance_id
      AND run.collector_id = schedule.collector_id
      AND run.target_revision = schedule.target_revision
      AND run.schedule_revision = schedule.schedule_revision
          ORDER BY outcome.completed_at DESC, run.run_id DESC
    LIMIT 1
) AS latest_run ON true
LEFT JOIN telemetry.collection_run_outcome AS latest_outcome
    ON latest_outcome.run_id = latest_run.run_id
LEFT JOIN LATERAL
(
    SELECT run.run_id
    FROM (SELECT reporting.find_completed_run(schedule.instance_id, schedule.collector_id, schedule.target_revision, schedule.schedule_revision, true) AS run_id OFFSET 0) AS chosen
            JOIN telemetry.collection_run AS run ON run.run_id = chosen.run_id
    INNER JOIN telemetry.collection_run_outcome AS outcome
        ON outcome.run_id = run.run_id
       AND outcome.outcome IN ('succeeded', 'partial')
    WHERE run.instance_id = schedule.instance_id
      AND run.collector_id = schedule.collector_id
      AND run.target_revision = schedule.target_revision
      AND run.schedule_revision = schedule.schedule_revision
          ORDER BY outcome.completed_at DESC, run.run_id DESC
    LIMIT 1
) AS latest_data_run ON true;

-- Evaluate the protected route flag once before entering either expensive view.
-- A missing singleton still yields no rows; the rollout gate and flag are unchanged.
CREATE OR REPLACE VIEW reporting.collector_health_projection
WITH (security_barrier = true)
AS
SELECT source.*
FROM reporting.collector_health_projection_legacy AS source
WHERE (SELECT NOT route.use_v2
       FROM system.collection_run_v2_health_read_route AS route
       WHERE route.singleton)
UNION ALL
SELECT successor.*
FROM reporting.collector_health_projection_v2_shadow AS successor
WHERE (SELECT route.use_v2
       FROM system.collection_run_v2_health_read_route AS route
       WHERE route.singleton);
