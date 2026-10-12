-- Keep the proven M7 validation and accounting path, then retire each new
-- original observation in the same transaction after its v3 mirror exists.
-- The operator gate is off until the read route and fleet parity are proven.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.query_observation_v3_read_route
 ADD COLUMN write_v3 boolean NOT NULL DEFAULT false;

CREATE FUNCTION system.guard_query_observation_v3_route()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog
AS $guard$
BEGIN
 IF OLD.write_v3 AND (NOT NEW.write_v3 OR NOT NEW.use_v3) THEN
  RAISE EXCEPTION 'query observation v3 writer cannot return to original reads or writes'
   USING ERRCODE='55000';
 END IF;
 IF NEW.write_v3 AND NOT NEW.use_v3 THEN
  RAISE EXCEPTION 'query observation v3 writer requires v3 reads'
   USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $guard$;
REVOKE ALL ON FUNCTION system.guard_query_observation_v3_route() FROM PUBLIC;
CREATE TRIGGER guard_query_observation_v3_route
 BEFORE UPDATE ON system.query_observation_v3_read_route
 FOR EACH ROW EXECUTE FUNCTION system.guard_query_observation_v3_route();

-- The wrapper takes a writer-compatible table lock before reading the gate.
-- Activation takes SHARE on the same table, draining all pre-switch calls.
ALTER FUNCTION control.commit_query_performance_with_text(
 uuid,uuid,bigint,integer,integer,bigint,timestamptz,text,uuid,bigint,
 bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,
 integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,
 events.query_performance_source,text,text,boolean,boolean,jsonb,jsonb)
 RENAME TO commit_query_performance_with_text_original;
REVOKE ALL ON FUNCTION control.commit_query_performance_with_text_original(
 uuid,uuid,bigint,integer,integer,bigint,timestamptz,text,uuid,bigint,
 bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,
 integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,
 events.query_performance_source,text,text,boolean,boolean,jsonb,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.commit_query_performance_with_text(
 p_run_id uuid,p_instance_id uuid,p_target_revision bigint,
 p_collector_version integer,p_output_schema_version integer,
 p_schedule_revision bigint,p_scheduled_at timestamptz,p_work_key text,
 p_owner_execution_id uuid,p_fencing_token bigint,
 p_request_digest bytea,p_outcome text,p_reason_code text,
 p_duration_ms bigint,p_attempt_count integer,p_source_row_count integer,
 p_output_item_count integer,p_response_bytes bigint,p_output_bytes bigint,
 p_loss_kind text,p_minimum_lost_items integer,p_loss_count_is_exact boolean,
 p_minimum_lost_bytes integer,p_next_circuit_state text,
 p_next_consecutive_failures integer,p_completion_digest bytea,
 p_window_start timestamptz,p_window_end timestamptz,
 p_source events.query_performance_source,p_source_state text,
 p_coverage text,p_freshness boolean,p_truncated boolean,
 p_payload jsonb,p_links jsonb)
RETURNS TABLE(result_status text,inserted_count integer,
 duplicate_count integer,rejected_count integer,persisted_bytes integer,
 committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system,telemetry,security
SET row_security=on
AS $writer$
DECLARE
 v_write_v3 boolean;
 v_result record;
 v_mirror_count integer;
 v_deleted integer;
 v_prior_context text:=current_setting('sqlobserver.retention_context',true);
BEGIN
 LOCK TABLE events.query_performance_observation IN ROW EXCLUSIVE MODE;
 SELECT route.write_v3 INTO STRICT v_write_v3
 FROM system.query_observation_v3_read_route AS route
 WHERE route.singleton;
 SELECT * INTO v_result
 FROM control.commit_query_performance_with_text_original(
  p_run_id,p_instance_id,p_target_revision,p_collector_version,
  p_output_schema_version,p_schedule_revision,p_scheduled_at,p_work_key,
  p_owner_execution_id,p_fencing_token,p_request_digest,p_outcome,
  p_reason_code,p_duration_ms,p_attempt_count,p_source_row_count,
  p_output_item_count,p_response_bytes,p_output_bytes,p_loss_kind,
  p_minimum_lost_items,p_loss_count_is_exact,p_minimum_lost_bytes,
  p_next_circuit_state,p_next_consecutive_failures,p_completion_digest,
  p_window_start,p_window_end,p_source,p_source_state,p_coverage,
  p_freshness,p_truncated,p_payload,p_links);
 IF NOT FOUND THEN
  RAISE EXCEPTION 'query performance commit returned no result'
   USING ERRCODE='55000';
 END IF;
 IF v_write_v3 AND v_result.result_status='committed' THEN
  SELECT count(*)::integer INTO v_mirror_count
  FROM events.query_performance_observation_v3 AS observation
  WHERE observation.instance_id=p_instance_id
    AND observation.collection_run_id=p_run_id;
  IF v_mirror_count<>v_result.inserted_count THEN
   RAISE EXCEPTION 'query observation v3 committed count differs from original accounting'
    USING ERRCODE='55000';
  END IF;
  PERFORM set_config('sqlobserver.retention_context','envelope_cascade',true);
  DELETE FROM events.query_performance_observation AS observation
  WHERE observation.instance_id=p_instance_id
    AND observation.collection_run_id=p_run_id;
  GET DIAGNOSTICS v_deleted=ROW_COUNT;
  PERFORM set_config('sqlobserver.retention_context',
   coalesce(v_prior_context,''),true);
  IF v_deleted<>v_result.inserted_count THEN
   RAISE EXCEPTION 'query observation original retirement count differs from accounting'
    USING ERRCODE='55000';
  END IF;
 END IF;
 RETURN QUERY SELECT v_result.result_status,v_result.inserted_count,
  v_result.duplicate_count,v_result.rejected_count,v_result.persisted_bytes,
  v_result.committed_at;
END $writer$;
REVOKE ALL ON FUNCTION control.commit_query_performance_with_text(
 uuid,uuid,bigint,integer,integer,bigint,timestamptz,text,uuid,bigint,
 bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,
 integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,
 events.query_performance_source,text,text,boolean,boolean,jsonb,jsonb)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_query_performance_with_text(
 uuid,uuid,bigint,integer,integer,bigint,timestamptz,text,uuid,bigint,
 bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,
 integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,
 events.query_performance_source,text,text,boolean,boolean,jsonb,jsonb)
 TO sqlobserver_collector;

CREATE FUNCTION control.activate_query_observation_v3_writer()
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system SET row_security=on
SET TimeZone='UTC'
AS $activate$
DECLARE
 v_route system.query_observation_v3_read_route%ROWTYPE;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'query observation writer activation requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE control.observation_target IN SHARE MODE;
 LOCK TABLE events.query_performance_observation IN SHARE MODE;
 LOCK TABLE events.query_performance_observation_v3 IN SHARE MODE;
 SELECT * INTO STRICT v_route
 FROM system.query_observation_v3_read_route AS route
 WHERE route.singleton FOR UPDATE;
 IF NOT v_route.use_v3 THEN
  RAISE EXCEPTION 'query observation v3 reads must be active before writer activation'
   USING ERRCODE='55000';
 END IF;
 IF v_route.write_v3 THEN RETURN v_route.revision; END IF;
 -- Re-run the all-target certificate and trigger/RLS checks under the locks.
 PERFORM control.set_query_observation_v3_read_route(true);
 UPDATE system.query_observation_v3_read_route AS route
 SET write_v3=true,revision=route.revision+1,
     changed_at=clock_timestamp(),changed_by=session_user::text
 WHERE route.singleton RETURNING route.revision INTO v_route.revision;
 RETURN v_route.revision;
END $activate$;
REVOKE ALL ON FUNCTION control.activate_query_observation_v3_writer()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.activate_query_observation_v3_writer()
 TO sqlobserver_migrator;
