-- Route outcome-time wait baselines with the scheduler's fenced run-read switch.
-- Existing installations keep the original table; writes are unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION telemetry.lookup_previous_wait_collection_run_id(
 p_instance_id uuid,p_target_revision bigint,p_completed_at timestamptz,p_run_id uuid)
RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,system SET TimeZone='UTC'
AS $lookup$
DECLARE v_prior_run_id uuid;
BEGIN
 IF p_instance_id IS NULL OR p_target_revision IS NULL
    OR p_completed_at IS NULL OR p_run_id IS NULL THEN
  RETURN NULL;
 END IF;
 IF (SELECT route.use_v2
     FROM system.collection_run_v2_scheduler_read_route AS route
     WHERE route.singleton) THEN
  SELECT prior.run_id INTO v_prior_run_id
  FROM telemetry.collection_run_v2 AS prior
  JOIN telemetry.collection_run_outcome AS result ON result.run_id=prior.run_id
  WHERE prior.instance_id=p_instance_id
    AND prior.target_revision=p_target_revision
    AND prior.collector_id='waits.server'
    AND result.outcome IN ('succeeded','partial')
    AND (result.completed_at,prior.run_id)<(p_completed_at,p_run_id)
  ORDER BY result.completed_at DESC,prior.run_id DESC
  LIMIT 1;
 ELSE
  SELECT prior.run_id INTO v_prior_run_id
  FROM telemetry.collection_run AS prior
  JOIN telemetry.collection_run_outcome AS result ON result.run_id=prior.run_id
  WHERE prior.instance_id=p_instance_id
    AND prior.target_revision=p_target_revision
    AND prior.collector_id='waits.server'
    AND result.outcome IN ('succeeded','partial')
    AND (result.completed_at,prior.run_id)<(p_completed_at,p_run_id)
  ORDER BY result.completed_at DESC,prior.run_id DESC
  LIMIT 1;
 END IF;
 RETURN v_prior_run_id;
END $lookup$;
REVOKE ALL ON FUNCTION telemetry.lookup_previous_wait_collection_run_id(
 uuid,bigint,timestamptz,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION telemetry.lookup_previous_wait_collection_run_id(
 uuid,bigint,timestamptz,uuid) TO sqlobserver_migrator;

-- Keep classification and delta accounting identical to the original trigger.
CREATE OR REPLACE FUNCTION telemetry.capture_server_wait_category_summary_v1()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $fn$
DECLARE
    current_run telemetry.collection_run%ROWTYPE;
    prior_run_id uuid;
BEGIN
    SELECT run.* INTO current_run
    FROM telemetry.lookup_collection_run_runtime(NEW.run_id) AS run;
    IF current_run.collector_id IS DISTINCT FROM 'waits.server'
       OR NEW.outcome NOT IN ('succeeded', 'partial') THEN
        NEW.server_wait_category_summary := NULL;
        RETURN NEW;
    END IF;

    SELECT telemetry.lookup_previous_wait_collection_run_id(
        current_run.instance_id,current_run.target_revision,
        NEW.completed_at,NEW.run_id) INTO prior_run_id;

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
