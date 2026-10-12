-- Stage a reversible collection-run writer route after child-FK cutover.
-- Installation retains the original writer and its forward mirror.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_writer_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_writer_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_writer_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- The source mirror is inactive only while v2 is the writer. The original
-- still receives every new run through the reverse mirror below.
CREATE OR REPLACE FUNCTION telemetry.mirror_collection_run_v2()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,control,system SET TimeZone='UTC'
AS $mirror$
DECLARE v_day date; v_inserted integer; v_expected integer;
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_writer_route AS route
     WHERE route.singleton) THEN
  IF EXISTS (
   SELECT 1 FROM inserted_rows AS source_run
   LEFT JOIN telemetry.collection_run_v2 AS successor
    ON successor.run_id=source_run.run_id
   AND successor.started_at=source_run.started_at
   WHERE successor.run_id IS NULL
      OR to_jsonb(successor) IS DISTINCT FROM to_jsonb(source_run)
  ) THEN
   RAISE EXCEPTION 'original collection run insert lacks a matching partitioned run'
    USING ERRCODE='55000';
  END IF;
  RETURN NULL;
 END IF;
 FOR v_day IN
  SELECT DISTINCT (started_at AT TIME ZONE 'UTC')::date
  FROM inserted_rows ORDER BY 1 LOOP
  PERFORM control.ensure_collection_run_v2_day(v_day);
 END LOOP;
 INSERT INTO telemetry.collection_run_v2
  (run_id,instance_id,collector_id,collector_version,output_schema_version,
   target_revision,schedule_revision,work_key,owner_execution_id,
   fencing_token,request_digest,scheduled_for,started_at)
 SELECT run_id,instance_id,collector_id,collector_version,output_schema_version,
        target_revision,schedule_revision,work_key,owner_execution_id,
        fencing_token,request_digest,scheduled_for,started_at
 FROM inserted_rows;
 GET DIAGNOSTICS v_inserted=ROW_COUNT;
 SELECT count(*) INTO v_expected FROM inserted_rows;
 IF v_inserted<>v_expected THEN
  RAISE EXCEPTION 'collection run successor mirror count differs'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $mirror$;

CREATE FUNCTION telemetry.mirror_collection_run_original()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,system SET TimeZone='UTC'
AS $mirror$
DECLARE v_inserted integer; v_expected integer;
BEGIN
 IF NOT (SELECT route.use_v2 FROM system.collection_run_v2_writer_route AS route
         WHERE route.singleton) THEN RETURN NULL; END IF;
 INSERT INTO telemetry.collection_run
  (run_id,instance_id,collector_id,collector_version,output_schema_version,
   target_revision,schedule_revision,work_key,owner_execution_id,
   fencing_token,request_digest,scheduled_for,started_at)
 SELECT run_id,instance_id,collector_id,collector_version,output_schema_version,
        target_revision,schedule_revision,work_key,owner_execution_id,
        fencing_token,request_digest,scheduled_for,started_at
 FROM inserted_rows ORDER BY run_id;
 GET DIAGNOSTICS v_inserted=ROW_COUNT;
 SELECT count(*) INTO v_expected FROM inserted_rows;
 IF v_inserted<>v_expected THEN
  RAISE EXCEPTION 'collection run original mirror count differs'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $mirror$;
REVOKE ALL ON FUNCTION telemetry.mirror_collection_run_original()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_original_mirror
 AFTER INSERT ON telemetry.collection_run_v2
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION telemetry.mirror_collection_run_original();

CREATE FUNCTION telemetry.insert_collection_run_runtime(
 p_run_id uuid,p_instance_id uuid,p_collector_id text,
 p_collector_version integer,p_output_schema_version integer,
 p_target_revision bigint,p_schedule_revision bigint,p_work_key text,
 p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,
 p_scheduled_for timestamptz,p_started_at timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,control,system SET TimeZone='UTC'
AS $writer$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_writer_route AS route
     WHERE route.singleton) THEN
  PERFORM control.ensure_collection_run_v2_day(
   (p_started_at AT TIME ZONE 'UTC')::date);
  INSERT INTO telemetry.collection_run_v2
   (run_id,instance_id,collector_id,collector_version,output_schema_version,
    target_revision,schedule_revision,work_key,owner_execution_id,
    fencing_token,request_digest,scheduled_for,started_at)
  VALUES(p_run_id,p_instance_id,p_collector_id,p_collector_version,
   p_output_schema_version,p_target_revision,p_schedule_revision,p_work_key,
   p_owner_execution_id,p_fencing_token,p_request_digest,p_scheduled_for,p_started_at);
 ELSE
  INSERT INTO telemetry.collection_run
   (run_id,instance_id,collector_id,collector_version,output_schema_version,
    target_revision,schedule_revision,work_key,owner_execution_id,
    fencing_token,request_digest,scheduled_for,started_at)
  VALUES(p_run_id,p_instance_id,p_collector_id,p_collector_version,
   p_output_schema_version,p_target_revision,p_schedule_revision,p_work_key,
   p_owner_execution_id,p_fencing_token,p_request_digest,p_scheduled_for,p_started_at);
 END IF;
END $writer$;
REVOKE ALL ON FUNCTION telemetry.insert_collection_run_runtime(
 uuid,uuid,text,integer,integer,bigint,bigint,text,uuid,bigint,bytea,
 timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION telemetry.insert_collection_run_runtime(
 uuid,uuid,text,integer,integer,bigint,bigint,text,uuid,bigint,bytea,
 timestamptz,timestamptz) TO sqlobserver_migrator;

CREATE FUNCTION control.set_collection_run_v2_writer_route(
 p_use_v2 boolean,p_max_pairs integer DEFAULT 10000)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system SET TimeZone='UTC'
AS $cutover$
DECLARE v_current boolean; v_revision bigint;
BEGIN
 IF p_use_v2 IS NULL OR p_max_pairs IS NULL
    OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'collection run writer route bounds are invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run writer route requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_scheduler_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 LOCK TABLE system.collection_run_v2_writer_route
  IN SHARE ROW EXCLUSIVE MODE;
 SELECT route.use_v2,route.revision INTO v_current,v_revision
 FROM system.collection_run_v2_writer_route AS route WHERE route.singleton;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'collection run writer route is missing' USING ERRCODE='55000';
 END IF;
 IF v_current=p_use_v2 THEN RETURN v_revision; END IF;
 IF EXISTS (
  SELECT 1 FROM (VALUES
   ('telemetry.collection_run'::regclass,'collection_run_v2_mirror'),
   ('telemetry.collection_run_v2'::regclass,'collection_run_original_mirror'),
   ('telemetry.collection_run_v2'::regclass,'collection_run_identity_mirror')
  ) AS required(relation_id,trigger_name)
  LEFT JOIN pg_trigger AS trigger_row
   ON trigger_row.tgrelid=required.relation_id
   AND trigger_row.tgname=required.trigger_name
   AND trigger_row.tgenabled IN ('O','A')
  WHERE trigger_row.oid IS NULL
 ) THEN
  RAISE EXCEPTION 'collection run writer mirrors are disabled'
   USING ERRCODE='55000';
 END IF;
 IF p_use_v2 THEN
  IF NOT (SELECT route.use_v2
          FROM system.collection_run_v2_scheduler_read_route AS route
          WHERE route.singleton)
     OR NOT EXISTS(SELECT 1 FROM system.collection_run_identity_fk_cutover)
  THEN
   RAISE EXCEPTION 'collection run writer requires read and FK cutover'
    USING ERRCODE='55000';
  END IF;
  IF control.cutover_collection_run_identity_fks(p_max_pairs)<>0 THEN
   RAISE EXCEPTION 'collection run child FK graph changed during writer activation'
    USING ERRCODE='55000';
  END IF;
 ELSE
  PERFORM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_writer_route AS route
 SET use_v2=p_use_v2,revision=route.revision+1,
     changed_at=clock_timestamp(),changed_by=session_user::text
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_writer_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_writer_route(boolean,integer)
 TO sqlobserver_migrator;

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

    PERFORM telemetry.insert_collection_run_runtime(
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
