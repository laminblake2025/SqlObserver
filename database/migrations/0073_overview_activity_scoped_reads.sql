-- Materialize scoped observations once and reuse per-run database counts.
-- Bounded, target/revision-bound database activity history for the selected-server
-- overview. Engine performance counters remain server-wide; this function uses
-- complete activity.sessions snapshots and reports observed user-session counts.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';

CREATE OR REPLACE FUNCTION reporting.overview_database_activity_history
(
    p_instance_id uuid,
    p_revision bigint,
    p_from timestamptz,
    p_to timestamptz,
    p_cutoff timestamptz
)
RETURNS TABLE
(
    database_id integer,
    database_name text,
    bucket_at timestamptz,
    metric_value double precision,
    sample_count integer,
    is_partial boolean
)
LANGUAGE plpgsql
SECURITY DEFINER
STABLE
SET search_path = pg_catalog, control, telemetry
SET TimeZone = 'UTC'
SET plan_cache_mode = force_custom_plan
SET jit = off
AS $overview$
BEGIN
    IF coalesce(current_setting('sqlobserver.role', true), '') <> 'Viewer'
       OR coalesce(current_setting('sqlobserver.target_scope', true), '') IS DISTINCT FROM p_instance_id::text THEN
        RAISE EXCEPTION 'overview scope rejected' USING ERRCODE = '42501';
    END IF;

    IF p_instance_id IS NULL
       OR p_revision IS NULL
       OR p_revision < 1
       OR p_from IS NULL
       OR p_to IS NULL
       OR p_cutoff IS NULL
       OR p_to <= p_from
       OR p_to - p_from > interval '31 days'
       OR p_to > p_cutoff + interval '1 minute' THEN
        RAISE EXCEPTION 'overview window rejected' USING ERRCODE = '22023';
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM control.observation_target AS target
        WHERE target.instance_id = p_instance_id
          AND target.revision = p_revision
    ) THEN
        RAISE EXCEPTION 'overview revision changed' USING ERRCODE = '40001';
    END IF;

    RETURN QUERY
WITH parameters AS
(
    SELECT
        CASE
            WHEN p_to - p_from > interval '1 day' THEN 8
            ELSE 12
        END AS maximum_databases,
        CASE
            WHEN p_to - p_from > interval '1 day' THEN interval '1 hour'
            WHEN p_to - p_from > interval '6 hours' THEN interval '15 minutes'
            ELSE interval '5 minutes'
        END AS bucket_width
),
scoped_runs AS MATERIALIZED
(
    SELECT run.run_id, run.started_at, outcome.output_item_count
    FROM telemetry.collection_run_outcome AS outcome
    INNER JOIN telemetry.collection_run AS run ON run.run_id = outcome.run_id
    WHERE run.instance_id = p_instance_id AND run.target_revision = p_revision
      AND run.collector_id = 'activity.sessions'
      AND run.started_at < p_to AND run.started_at <= p_cutoff
      AND outcome.completed_at > p_from AND outcome.completed_at <= p_cutoff
      AND outcome.outcome = 'succeeded' AND NOT outcome.loss_detected
),
scoped_sessions AS MATERIALIZED
(
    SELECT snapshot.collection_run_id, snapshot.database_id,
           max(snapshot.observed_at) AS observed_at,
           count(DISTINCT snapshot.session_id) FILTER (WHERE snapshot.is_user_process)::double precision AS metric_value,
           count(*) FILTER (WHERE snapshot.is_user_process)::bigint AS weight
    FROM telemetry.activity_session_snapshot AS snapshot
    WHERE snapshot.instance_id = p_instance_id AND snapshot.target_revision = p_revision
      AND snapshot.observed_at >= p_from
      AND snapshot.observed_at <= p_cutoff AND snapshot.collected_at <= p_cutoff
    GROUP BY snapshot.collection_run_id, snapshot.database_id
),
runs AS MATERIALIZED
(
    SELECT run.run_id, coalesce(max(snapshot.observed_at), run.started_at) AS observed_at
    FROM scoped_runs AS run
    LEFT JOIN scoped_sessions AS snapshot ON snapshot.collection_run_id = run.run_id
    GROUP BY run.run_id, run.started_at, run.output_item_count
    HAVING (max(snapshot.observed_at) IS NOT NULL OR run.output_item_count = 0)
       AND coalesce(max(snapshot.observed_at), run.started_at) >= p_from
       AND coalesce(max(snapshot.observed_at), run.started_at) < p_to
),
run_counts AS MATERIALIZED
(
    SELECT snapshot.collection_run_id, snapshot.database_id,
           snapshot.metric_value, snapshot.weight
    FROM scoped_sessions AS snapshot
    INNER JOIN runs ON runs.run_id = snapshot.collection_run_id
    WHERE snapshot.weight > 0
),
session_database_ids AS
(
    SELECT counted.database_id FROM run_counts AS counted GROUP BY counted.database_id
),
inventory_ids AS MATERIALIZED
(
    SELECT DISTINCT inventory.database_id
    FROM telemetry.database_inventory_snapshot AS inventory
    WHERE inventory.instance_id = p_instance_id AND inventory.target_revision = p_revision
      AND inventory.observed_at >= p_from - interval '31 days'
      AND inventory.observed_at < p_to AND inventory.observed_at <= p_cutoff
      AND inventory.collected_at <= p_cutoff
),
inventory_latest AS MATERIALIZED
(
    SELECT ids.database_id, latest.database_name
    FROM inventory_ids AS ids
    CROSS JOIN LATERAL
    (
        SELECT inventory.database_name
        FROM telemetry.database_inventory_snapshot AS inventory
        INNER JOIN telemetry.collection_run AS inventory_run ON inventory_run.run_id = inventory.collection_run_id
        INNER JOIN telemetry.collection_run_outcome AS inventory_outcome ON inventory_outcome.run_id = inventory.collection_run_id
        WHERE inventory.instance_id = p_instance_id AND inventory.target_revision = p_revision
          AND inventory.database_id = ids.database_id
          AND inventory_run.collector_id = 'database.inventory'
          AND inventory_outcome.outcome = 'succeeded' AND NOT inventory_outcome.loss_detected
          AND inventory.observed_at >= p_from - interval '31 days'
          AND inventory.observed_at < p_to AND inventory.observed_at <= p_cutoff
          AND inventory.collected_at <= p_cutoff
        ORDER BY inventory.observed_at DESC, inventory.snapshot_id DESC
        LIMIT 1
    ) AS latest
),
database_ids AS
(
    SELECT ids.database_id FROM session_database_ids AS ids
    UNION
    SELECT ids.database_id FROM inventory_latest AS ids
),
session_weights AS
(
    SELECT counted.database_id, sum(counted.weight)::bigint AS weight FROM run_counts AS counted GROUP BY counted.database_id
),
database_candidates AS
(
    SELECT
        ids.database_id,
        CASE
            WHEN ids.database_id IS NULL THEN 'Unknown database'
            ELSE coalesce(latest.database_name, format('Database %s', ids.database_id))
        END AS database_name,
        coalesce(weights.weight, 0) AS weight
    FROM database_ids AS ids
    LEFT JOIN session_weights AS weights
        ON weights.database_id IS NOT DISTINCT FROM ids.database_id
    LEFT JOIN inventory_latest AS latest ON latest.database_id = ids.database_id
),
ranked_databases AS
(
    SELECT
        candidates.database_id,
        candidates.database_name,
        count(*) OVER () > parameters.maximum_databases AS is_partial
    FROM database_candidates AS candidates
    CROSS JOIN parameters
    ORDER BY candidates.weight DESC, candidates.database_id NULLS LAST
    LIMIT (SELECT maximum_databases FROM parameters)
),
bucket_runs AS MATERIALIZED
(
    SELECT date_bin(parameters.bucket_width, runs.observed_at, timestamptz '2000-01-01 00:00:00Z') AS bucket_at,
           count(*)::integer AS sample_count
    FROM runs CROSS JOIN parameters
    GROUP BY 1
),
bucket_values AS MATERIALIZED
(
    SELECT counted.database_id,
           date_bin(parameters.bucket_width, runs.observed_at, timestamptz '2000-01-01 00:00:00Z') AS bucket_at,
           sum(counted.metric_value)::double precision AS total_value
    FROM run_counts AS counted
    INNER JOIN runs ON runs.run_id = counted.collection_run_id
    CROSS JOIN parameters
    GROUP BY counted.database_id, 2
)
SELECT databases.database_id, databases.database_name, buckets.bucket_at,
       (coalesce(values.total_value, 0) / buckets.sample_count)::double precision AS metric_value,
       buckets.sample_count, databases.is_partial
FROM bucket_runs AS buckets
CROSS JOIN ranked_databases AS databases
LEFT JOIN bucket_values AS values ON values.bucket_at = buckets.bucket_at
    AND values.database_id IS NOT DISTINCT FROM databases.database_id
ORDER BY databases.database_id NULLS LAST, buckets.bucket_at;
END;
$overview$;

REVOKE ALL ON FUNCTION reporting.overview_database_activity_history(uuid, bigint, timestamptz, timestamptz, timestamptz)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reporting.overview_database_activity_history(uuid, bigint, timestamptz, timestamptz, timestamptz)
    TO sqlobserver_server;
