-- Route begin/replay and abandoned-run identity reads before changing writers.
-- The original run table remains the only writer at installation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- The scheduler's separately gated switch carries the same active-run source
-- into begin_collection_run. The identity bridge supplies the v2 partition key.
CREATE FUNCTION telemetry.lookup_collection_run_runtime(p_run_id uuid)
RETURNS SETOF telemetry.collection_run
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,system SET TimeZone='UTC'
AS $lookup$
BEGIN
 IF p_run_id IS NULL THEN RETURN; END IF;
 IF (SELECT route.use_v2
     FROM system.collection_run_v2_scheduler_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY
  SELECT run.*
  FROM telemetry.collection_run_identity AS identity
  JOIN telemetry.collection_run_v2 AS run
    ON run.run_id=identity.run_id AND run.started_at=identity.started_at
  WHERE identity.run_id=p_run_id;
 ELSE
  RETURN QUERY SELECT run.*
  FROM telemetry.collection_run AS run
  WHERE run.run_id=p_run_id;
 END IF;
END $lookup$;
REVOKE ALL ON FUNCTION telemetry.lookup_collection_run_runtime(uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION telemetry.lookup_collection_run_runtime(uuid)
 TO sqlobserver_migrator;

-- Preserve lease, replay, schedule and outcome logic; change only run reads.
CREATE OR REPLACE FUNCTION control.begin_collection_run
(
    p_run_id uuid,
    p_instance_id uuid,
    p_target_revision bigint,
    p_collector_id text,
    p_collector_version integer,
    p_output_schema_version integer,
    p_schedule_revision bigint,
    p_scheduled_at timestamptz,
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint,
    p_request_digest bytea
)
RETURNS TABLE
(
    result_status text,
    started_at timestamptz,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_run telemetry.collection_run%ROWTYPE;
    abandoned_run telemetry.collection_run%ROWTYPE;
    selected_schedule control.collector_schedule%ROWTYPE;
    selected_target control.observation_target%ROWTYPE;
    captured_repository_time timestamptz;
    expected_work_key text;
    has_outcome boolean;
BEGIN
    IF p_run_id IS NULL
       OR p_run_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_instance_id IS NULL
       OR p_instance_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_owner_execution_id IS NULL
       OR p_owner_execution_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_target_revision IS NULL
       OR p_target_revision <= 0
       OR p_collector_id IS NULL
       OR p_collector_version IS NULL
       OR p_collector_version <= 0
       OR p_output_schema_version IS NULL
       OR p_output_schema_version <= 0
       OR p_schedule_revision IS NULL
       OR p_schedule_revision <= 0
       OR p_scheduled_at IS NULL
       OR NOT isfinite(p_scheduled_at)
       OR octet_length(p_request_digest) IS DISTINCT FROM 32 THEN
        RAISE EXCEPTION 'invalid bounded collection-run identity'
            USING ERRCODE = '22023';
    END IF;

    expected_work_key := format(
        'collector/run/%s/%s',
        p_collector_id,
        replace(p_instance_id::text, '-', ''));
    IF p_work_key IS DISTINCT FROM expected_work_key THEN
        RAISE EXCEPTION 'collection-run lease key does not match the target and collector'
            USING ERRCODE = '22023';
    END IF;

    SELECT run.*
    INTO selected_run
    FROM telemetry.lookup_collection_run_runtime(p_run_id) AS run;

    IF FOUND THEN
        IF selected_run.instance_id <> p_instance_id
           OR selected_run.target_revision <> p_target_revision
           OR selected_run.collector_id <> p_collector_id
           OR selected_run.collector_version <> p_collector_version
           OR selected_run.output_schema_version <> p_output_schema_version
           OR selected_run.schedule_revision <> p_schedule_revision
           OR selected_run.scheduled_for <> p_scheduled_at
           OR selected_run.work_key <> p_work_key
           OR selected_run.owner_execution_id <> p_owner_execution_id
           OR selected_run.fencing_token <> p_fencing_token
           OR selected_run.request_digest <> p_request_digest THEN
            RAISE EXCEPTION 'collector run identifier was replayed with divergent content'
                USING ERRCODE = '22023';
        END IF;

        SELECT EXISTS
        (
            SELECT 1
            FROM telemetry.collection_run_outcome AS outcome
            WHERE outcome.run_id = p_run_id
        )
        INTO has_outcome;

        IF has_outcome THEN
            RETURN QUERY SELECT 'replayed', selected_run.started_at, clock_timestamp();
            RETURN;
        END IF;
    END IF;

    BEGIN
        PERFORM control.assert_worker_lease(
            p_work_key,
            p_owner_execution_id,
            p_fencing_token);
    EXCEPTION
        WHEN SQLSTATE '55000' THEN
            RETURN QUERY SELECT 'lease_lost', NULL::timestamptz, clock_timestamp();
            RETURN;
    END;

    captured_repository_time := clock_timestamp();

    SELECT target.*
    INTO selected_target
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'target_not_found', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    IF selected_target.lifecycle_state <> 'active' OR selected_target.host_name IS NULL THEN
        RETURN QUERY SELECT 'target_inactive', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    IF selected_target.revision <> p_target_revision THEN
        RETURN QUERY SELECT 'target_revision_conflict', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    SELECT schedule.*
    INTO selected_schedule
    FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id
    FOR UPDATE;

    IF NOT FOUND
       OR NOT selected_schedule.enabled
       OR selected_schedule.collector_version <> p_collector_version
       OR selected_schedule.target_revision <> p_target_revision
       OR selected_schedule.schedule_revision <> p_schedule_revision
       OR selected_schedule.next_due_at <> p_scheduled_at
       OR selected_schedule.next_due_at > captured_repository_time
       OR
       (
           selected_schedule.circuit_state = 'open'
           AND selected_schedule.circuit_open_until > captured_repository_time
       ) THEN
        RETURN QUERY SELECT 'schedule_conflict', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    IF selected_schedule.active_run_id IS NOT NULL
       AND selected_schedule.active_run_id <> p_run_id THEN
        SELECT run.*
        INTO abandoned_run
        FROM telemetry.lookup_collection_run_runtime(selected_schedule.active_run_id) AS run;

        IF NOT FOUND
           OR abandoned_run.instance_id <> p_instance_id
           OR abandoned_run.collector_id <> p_collector_id THEN
            RAISE EXCEPTION 'collector schedule references an invalid active run'
                USING ERRCODE = '55000';
        END IF;

        IF NOT EXISTS
        (
            SELECT 1
            FROM telemetry.collection_run_outcome AS outcome
            WHERE outcome.run_id = abandoned_run.run_id
        ) THEN
            INSERT INTO telemetry.collection_run_outcome
            (
                run_id, outcome, reason_code, attempt_count, retry_count, duration_ms,
                source_row_count, output_item_count, inserted_item_count,
                duplicate_item_count, rejected_item_count, response_bytes, output_bytes,
                persisted_bytes, truncated, loss_detected, loss_kind, loss_count_exact,
                lost_row_count, lost_byte_count, completion_digest, completed_at
            )
            VALUES
            (
                abandoned_run.run_id, 'lease_lost', 'lease_ownership_lost', 1, 0,
                least(
                    3600000,
                    greatest(
                        0,
                        floor(extract(epoch FROM
                            (captured_repository_time - abandoned_run.started_at)) * 1000)::bigint)),
                0, 0, 0, 0, 0, 0, 0, 0, false, false, 'none', true, 0, 0,
                abandoned_run.request_digest, captured_repository_time
            );

            INSERT INTO telemetry.visibility_gap
            (
                gap_id, run_id, instance_id, collector_id, reason_code,
                gap_started_at, gap_ended_at, lost_row_count, lost_byte_count,
                count_is_exact, recorded_at
            )
            VALUES
            (
                gen_random_uuid(), abandoned_run.run_id, p_instance_id, p_collector_id,
                'lease_ownership_lost', abandoned_run.scheduled_for,
                captured_repository_time, 1, 0, false, captured_repository_time
            );
        END IF;

        UPDATE control.collector_schedule AS schedule
        SET
            active_run_id = NULL,
            circuit_state = 'closed',
            consecutive_failure_count = 0,
            circuit_open_until = NULL,
            last_completed_at = captured_repository_time,
            last_outcome = 'lease_lost',
            contention_count = schedule.contention_count + 1,
            last_contention_at = captured_repository_time,
            updated_at = captured_repository_time
        WHERE schedule.instance_id = p_instance_id
          AND schedule.collector_id = p_collector_id;
    END IF;

    IF selected_run.run_id IS NOT NULL THEN
        RETURN QUERY SELECT 'running_replay', selected_run.started_at, clock_timestamp();
        RETURN;
    END IF;

    INSERT INTO telemetry.collection_run
    (
        run_id,
        instance_id,
        collector_id,
        collector_version,
        output_schema_version,
        target_revision,
        schedule_revision,
        work_key,
        owner_execution_id,
        fencing_token,
        request_digest,
        scheduled_for,
        started_at
    )
    VALUES
    (
        p_run_id,
        p_instance_id,
        p_collector_id,
        p_collector_version,
        p_output_schema_version,
        p_target_revision,
        p_schedule_revision,
        p_work_key,
        p_owner_execution_id,
        p_fencing_token,
        p_request_digest,
        p_scheduled_at,
        captured_repository_time
    );

    UPDATE control.collector_schedule AS schedule
    SET
        active_run_id = p_run_id,
        circuit_state = CASE
            WHEN schedule.circuit_state = 'open' THEN 'half_open'
            ELSE schedule.circuit_state
        END,
        circuit_open_until = NULL,
        last_started_at = captured_repository_time,
        updated_at = captured_repository_time
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id;

    RETURN QUERY SELECT 'started', captured_repository_time, captured_repository_time;
END
$sqlobserver$;
