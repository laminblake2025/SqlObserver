-- Persist a bounded category summary beside each completed waits.server run.
-- Existing outcomes remain NULL: readers must show a gap, not invent zeroes.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE telemetry.collection_run_outcome
    ADD COLUMN server_wait_category_summary jsonb;

CREATE FUNCTION telemetry.server_wait_category_v1(p_wait_type text)
RETURNS text LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
SET search_path = pg_catalog
AS $fn$
SELECT CASE
    WHEN p_wait_type LIKE 'SLEEP\_%' ESCAPE '\'
      OR p_wait_type IN (
        'LAZYWRITER_SLEEP', 'SQLTRACE_BUFFER_FLUSH',
        'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', 'SQLTRACE_WAIT_ENTRIES',
        'FT_IFTS_SCHEDULER_IDLE_WAIT', 'XE_DISPATCHER_WAIT',
        'REQUEST_FOR_DEADLOCK_SEARCH', 'LOGMGR_QUEUE',
        'ONDEMAND_TASK_QUEUE', 'CHECKPOINT_QUEUE', 'XE_TIMER_EVENT')
      THEN 'Idle'
    WHEN p_wait_type LIKE 'LCK\_M\_%' ESCAPE '\' THEN 'Lock'
    WHEN p_wait_type IN ('WRITELOG', 'LOGBUFFER', 'LOG_RATE_GOVERNOR')
      OR p_wait_type LIKE 'LOGMGR\_%' ESCAPE '\' THEN 'Log'
    WHEN p_wait_type LIKE 'PAGEIOLATCH\_%' ESCAPE '\'
      OR p_wait_type IN ('IO_COMPLETION', 'ASYNC_IO_COMPLETION',
                         'BACKUPIO', 'WRITE_COMPLETION') THEN 'I/O'
    WHEN p_wait_type = 'SOS_SCHEDULER_YIELD' THEN 'CPU/signal'
    WHEN p_wait_type LIKE 'RESOURCE\_SEMAPHORE%' ESCAPE '\'
      OR p_wait_type = 'MEMORY_ALLOCATION_EXT' THEN 'Memory'
    WHEN p_wait_type IN ('CXPACKET', 'CXCONSUMER', 'EXCHANGE')
      OR p_wait_type LIKE 'CXSYNC\_%' ESCAPE '\' THEN 'Parallelism'
    ELSE 'Other'
END;
$fn$;

REVOKE ALL ON FUNCTION telemetry.server_wait_category_v1(text)
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

CREATE FUNCTION telemetry.capture_server_wait_category_summary_v1()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $fn$
DECLARE
    current_run telemetry.collection_run%ROWTYPE;
    prior_run_id uuid;
BEGIN
    SELECT run.* INTO current_run
    FROM telemetry.collection_run AS run WHERE run.run_id = NEW.run_id;
    IF current_run.collector_id IS DISTINCT FROM 'waits.server'
       OR NEW.outcome NOT IN ('succeeded', 'partial') THEN
        NEW.server_wait_category_summary := NULL;
        RETURN NEW;
    END IF;

    SELECT prior.run_id INTO prior_run_id
    FROM telemetry.collection_run AS prior
    JOIN telemetry.collection_run_outcome AS result ON result.run_id = prior.run_id
    WHERE prior.instance_id = current_run.instance_id
      AND prior.target_revision = current_run.target_revision
      AND prior.collector_id = 'waits.server'
      AND result.outcome IN ('succeeded', 'partial')
      AND (result.completed_at, prior.run_id) < (NEW.completed_at, NEW.run_id)
    ORDER BY result.completed_at DESC, prior.run_id DESC
    LIMIT 1;

    WITH compared AS (
        SELECT current_wait.observed_at, current_wait.wait_type,
               telemetry.server_wait_category_v1(current_wait.wait_type) AS category,
               previous.wait_type IS NOT NULL AS has_baseline,
               coalesce(previous.wait_type IS NOT NULL AND (
                   current_wait.waiting_tasks_count < previous.waiting_tasks_count
                   OR current_wait.wait_time_ms < previous.wait_time_ms
                   OR current_wait.maximum_wait_time_ms < previous.maximum_wait_time_ms
                   OR current_wait.signal_wait_time_ms < previous.signal_wait_time_ms
               ), false) AS reset_detected,
               current_wait.wait_time_ms::numeric - previous.wait_time_ms::numeric AS wait_delta
        FROM telemetry.server_wait_snapshot AS current_wait
        LEFT JOIN telemetry.server_wait_snapshot AS previous
          ON previous.instance_id = current_wait.instance_id
         AND previous.target_revision = current_wait.target_revision
         AND previous.collection_run_id = prior_run_id
         AND previous.wait_type = current_wait.wait_type
        WHERE current_wait.collection_run_id = NEW.run_id
          AND current_wait.instance_id = current_run.instance_id
          AND current_wait.target_revision = current_run.target_revision
    ), grouped AS (
        SELECT category,
               sum(wait_delta) FILTER (WHERE has_baseline AND NOT reset_detected) AS wait_ms,
               count(*) FILTER (WHERE has_baseline AND NOT reset_detected) AS comparable_types,
               count(*) FILTER (WHERE NOT has_baseline OR reset_detected) AS incomparable_types
        FROM compared WHERE category <> 'Idle' GROUP BY category
    )
    SELECT jsonb_build_object(
        'version', 1,
        'observedAtUtc', (SELECT max(observed_at) FROM compared),
        'baselineRunId', prior_run_id,
        'sourceRows', (SELECT count(*) FROM compared),
        'idleTypesOmitted', (SELECT count(*) FROM compared WHERE category = 'Idle'),
        'categories', coalesce((
            SELECT jsonb_agg(jsonb_build_object(
                'category', category,
                'waitMilliseconds', wait_ms::text,
                'comparableTypes', comparable_types,
                'incomparableTypes', incomparable_types
            ) ORDER BY category) FROM grouped
        ), '[]'::jsonb)
    ) INTO NEW.server_wait_category_summary;

    RETURN NEW;
END;
$fn$;

REVOKE ALL ON FUNCTION telemetry.capture_server_wait_category_summary_v1()
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

CREATE TRIGGER capture_server_wait_category_summary_v1
BEFORE INSERT ON telemetry.collection_run_outcome
FOR EACH ROW EXECUTE FUNCTION telemetry.capture_server_wait_category_summary_v1();

-- One target and at most 24 hours. All seven categories are returned for every
-- five-minute bucket, including empty buckets, so clients cannot draw a line
-- through an absence of evidence. Values with missing or partial evidence are
-- NULL; comparable totals remain available in the immutable run summaries.
CREATE FUNCTION reporting.list_server_wait_category_trend(
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
        JOIN telemetry.collection_run AS run ON run.run_id = result.run_id
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

REVOKE ALL ON FUNCTION reporting.list_server_wait_category_trend(
    uuid,timestamptz,timestamptz)
    FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_wait_category_trend(
    uuid,timestamptz,timestamptz) TO sqlobserver_server;
