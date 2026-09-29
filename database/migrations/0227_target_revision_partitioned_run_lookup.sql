-- Keep target-revision invalidation bound to the active collection-run source.
-- A sealed v2-only run must receive its outcome and visibility gap.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.ensure_target_collector_schedules()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    captured_repository_time timestamptz;
    selected_schedule control.collector_schedule%ROWTYPE;
    invalidated_run telemetry.collection_run%ROWTYPE;
    invalidated_duration_ms bigint;
    invalidates_evidence boolean;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.revision = NEW.revision
           AND OLD.lifecycle_state = NEW.lifecycle_state
           AND OLD.host_name IS NOT DISTINCT FROM NEW.host_name
           AND OLD.instance_name IS NOT DISTINCT FROM NEW.instance_name
           AND OLD.tcp_port IS NOT DISTINCT FROM NEW.tcp_port
           AND OLD.certificate_host_name IS NOT DISTINCT FROM NEW.certificate_host_name
           AND OLD.connect_timeout = NEW.connect_timeout
           AND OLD.authentication_mode = NEW.authentication_mode
           AND OLD.transport_security_mode = NEW.transport_security_mode THEN
            RETURN NEW;
        END IF;

        invalidates_evidence :=
            OLD.revision <> NEW.revision
            OR OLD.host_name IS DISTINCT FROM NEW.host_name
            OR OLD.instance_name IS DISTINCT FROM NEW.instance_name
            OR OLD.tcp_port IS DISTINCT FROM NEW.tcp_port
            OR OLD.certificate_host_name IS DISTINCT FROM NEW.certificate_host_name
            OR OLD.connect_timeout <> NEW.connect_timeout
            OR OLD.authentication_mode <> NEW.authentication_mode
            OR OLD.transport_security_mode <> NEW.transport_security_mode
            OR (OLD.lifecycle_state = 'active' AND NEW.lifecycle_state <> 'active');
    ELSE
        invalidates_evidence := false;
    END IF;

    captured_repository_time := clock_timestamp();

    IF invalidates_evidence THEN
        FOR selected_schedule IN
            SELECT schedule.*
            FROM control.collector_schedule AS schedule
            WHERE schedule.instance_id = NEW.instance_id
            FOR UPDATE
        LOOP
            IF selected_schedule.active_run_id IS NOT NULL THEN
                SELECT run.*
                INTO invalidated_run
                FROM telemetry.lookup_collection_run_runtime(selected_schedule.active_run_id) AS run;

                IF FOUND AND NOT EXISTS
                (
                    SELECT 1
                    FROM telemetry.collection_run_outcome AS outcome
                    WHERE outcome.run_id = invalidated_run.run_id
                ) THEN
                    invalidated_duration_ms := least(
                        3600000,
                        greatest(
                            0,
                            floor(extract(epoch FROM
                                (captured_repository_time - invalidated_run.started_at)) * 1000)::bigint));
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
                        invalidated_run.run_id, 'permanent_failure', 'target_revision_changed',
                        1, 0, invalidated_duration_ms, 0, 0, 0, 0, 0, 0, 0, 0,
                        false, false, 'none', true, 0, 0,
                        invalidated_run.request_digest, captured_repository_time
                    );

                    INSERT INTO telemetry.visibility_gap
                    (
                        gap_id, run_id, instance_id, collector_id, reason_code,
                        gap_started_at, gap_ended_at, lost_row_count, lost_byte_count,
                        count_is_exact, recorded_at
                    )
                    VALUES
                    (
                        gen_random_uuid(), invalidated_run.run_id, NEW.instance_id,
                        selected_schedule.collector_id, 'target_revision_changed',
                        invalidated_run.scheduled_for, captured_repository_time,
                        1, 0, false, captured_repository_time
                    );
                END IF;
            END IF;

            UPDATE control.collector_schedule AS schedule
            SET
                target_revision = NEW.revision,
                schedule_revision = schedule.schedule_revision + 1,
                enabled = NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL,
                next_due_at = captured_repository_time,
                active_run_id = NULL,
                circuit_state = 'closed',
                consecutive_failure_count = 0,
                circuit_open_until = NULL,
                last_started_at = NULL,
                last_completed_at = NULL,
                last_succeeded_at = NULL,
                last_outcome = NULL,
                contention_count = 0,
                last_contention_at = NULL,
                updated_at = captured_repository_time
            WHERE schedule.instance_id = selected_schedule.instance_id
              AND schedule.collector_id = selected_schedule.collector_id;
        END LOOP;
    ELSIF TG_OP = 'UPDATE' THEN
        UPDATE control.collector_schedule AS schedule
        SET
            enabled = NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL,
            next_due_at = CASE
                WHEN NOT schedule.enabled
                     AND NEW.lifecycle_state = 'active'
                     AND NEW.host_name IS NOT NULL
                    THEN captured_repository_time
                ELSE schedule.next_due_at
            END,
            updated_at = captured_repository_time
        WHERE schedule.instance_id = NEW.instance_id
          AND schedule.target_revision = NEW.revision
          AND schedule.enabled IS DISTINCT FROM
              (NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL);
    END IF;

    IF NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL THEN
        INSERT INTO control.collector_schedule
        (
            instance_id, collector_id, collector_version, target_revision, schedule_revision,
            enabled, collection_interval, next_due_at, circuit_state,
            consecutive_failure_count, created_at, updated_at
        )
        SELECT
            NEW.instance_id, contract.collector_id, contract.collector_version, NEW.revision, 1,
            true, contract.default_interval, captured_repository_time, 'closed',
            0, captured_repository_time, captured_repository_time
        FROM control.collector_contract AS contract
        ON CONFLICT (instance_id, collector_id) DO NOTHING;
    END IF;

    RETURN NEW;
END
$sqlobserver$;
