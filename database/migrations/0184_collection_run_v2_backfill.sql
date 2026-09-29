-- Copy historical run starts in bounded target-scoped batches. The original
-- table remains canonical and every copied or already-mirrored row is checked.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_backfill (
 instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
 cursor_collector_id text,
 cursor_started_at timestamptz,
 cursor_run_id uuid,
 checked_rows bigint NOT NULL DEFAULT 0 CHECK (checked_rows>=0),
 copied_rows bigint NOT NULL DEFAULT 0 CHECK (copied_rows>=0),
 completed_at timestamptz,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT ck_collection_run_v2_backfill_cursor CHECK (
  (cursor_collector_id IS NULL AND cursor_started_at IS NULL
   AND cursor_run_id IS NULL) OR
  (cursor_collector_id IS NOT NULL AND cursor_started_at IS NOT NULL
   AND cursor_run_id IS NOT NULL))
);
REVOKE ALL ON system.collection_run_v2_backfill
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.backfill_collection_run_v2(
 p_instance_id uuid,p_limit integer)
RETURNS TABLE(checked_rows integer,copied_rows integer,complete boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC'
AS $backfill$
DECLARE
 v_state system.collection_run_v2_backfill%ROWTYPE;
 v_row telemetry.collection_run%ROWTYPE;
 v_mirror telemetry.collection_run_v2%ROWTYPE;
 v_checked integer:=0;
 v_copied integer:=0;
 v_inserted integer;
BEGIN
 IF p_instance_id IS NULL OR p_limit IS NULL
    OR p_limit NOT BETWEEN 1 AND 1000 THEN
  RAISE EXCEPTION 'collection run copy bounds invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'collection run copy target scope missing'
   USING ERRCODE='42501';
 END IF;
 INSERT INTO system.collection_run_v2_backfill(instance_id)
 VALUES(p_instance_id) ON CONFLICT DO NOTHING;
 SELECT * INTO v_state FROM system.collection_run_v2_backfill
 WHERE instance_id=p_instance_id FOR UPDATE;
 IF v_state.completed_at IS NOT NULL THEN
  RETURN QUERY SELECT 0,0,true;
  RETURN;
 END IF;

 -- The concurrent index from 0183 bounds this scan. New inserts are mirrored,
 -- even when their key sorts behind this cursor.
 FOR v_row IN
  SELECT run.* FROM telemetry.collection_run AS run
  WHERE run.instance_id=p_instance_id
    AND (v_state.cursor_run_id IS NULL OR
     (run.collector_id,run.started_at,run.run_id) >
     (v_state.cursor_collector_id,v_state.cursor_started_at,
      v_state.cursor_run_id))
  ORDER BY run.collector_id,run.started_at,run.run_id
  LIMIT p_limit
 LOOP
  PERFORM control.ensure_collection_run_v2_day(
   (v_row.started_at AT TIME ZONE 'UTC')::date);
  INSERT INTO telemetry.collection_run_v2
   (run_id,instance_id,collector_id,collector_version,
    output_schema_version,target_revision,schedule_revision,work_key,
    owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
  VALUES(v_row.run_id,v_row.instance_id,v_row.collector_id,
   v_row.collector_version,v_row.output_schema_version,
   v_row.target_revision,v_row.schedule_revision,v_row.work_key,
   v_row.owner_execution_id,v_row.fencing_token,v_row.request_digest,
   v_row.scheduled_for,v_row.started_at)
  ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS v_inserted=ROW_COUNT;
  v_copied:=v_copied+v_inserted;
  SELECT mirror.* INTO v_mirror
  FROM telemetry.collection_run_v2 AS mirror
  WHERE mirror.run_id=v_row.run_id AND mirror.started_at=v_row.started_at;
  IF NOT FOUND OR to_jsonb(v_mirror) IS DISTINCT FROM to_jsonb(v_row) THEN
   RAISE EXCEPTION 'collection run partitioned copy differs from source'
    USING ERRCODE='55000';
  END IF;
  v_checked:=v_checked+1;
  v_state.cursor_collector_id:=v_row.collector_id;
  v_state.cursor_started_at:=v_row.started_at;
  v_state.cursor_run_id:=v_row.run_id;
 END LOOP;
 UPDATE system.collection_run_v2_backfill AS state
 SET cursor_collector_id=v_state.cursor_collector_id,
     cursor_started_at=v_state.cursor_started_at,
     cursor_run_id=v_state.cursor_run_id,
     checked_rows=state.checked_rows+v_checked,
     copied_rows=state.copied_rows+v_copied,
     completed_at=CASE WHEN v_checked<p_limit THEN clock_timestamp()
                       ELSE NULL END,
     updated_at=clock_timestamp()
 WHERE state.instance_id=p_instance_id;
 RETURN QUERY SELECT v_checked,v_copied,v_checked<p_limit;
END $backfill$;
REVOKE ALL ON FUNCTION control.backfill_collection_run_v2(uuid,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.backfill_collection_run_v2(uuid,integer)
 TO sqlobserver_migrator;
