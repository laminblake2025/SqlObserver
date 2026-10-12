-- Explicit target-scoped copy for successor rows predating the identity
-- mirror. Every run must already exist unchanged in the partitioned parent.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_identity_backfill (
 instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
 cursor_collector_id text,
 cursor_started_at timestamptz,
 cursor_run_id uuid,
 checked_rows bigint NOT NULL DEFAULT 0 CHECK(checked_rows>=0),
 copied_rows bigint NOT NULL DEFAULT 0 CHECK(copied_rows>=0),
 completed_at timestamptz,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT ck_collection_run_identity_backfill_cursor CHECK (
  (cursor_collector_id IS NULL AND cursor_started_at IS NULL
   AND cursor_run_id IS NULL) OR
  (cursor_collector_id IS NOT NULL AND cursor_started_at IS NOT NULL
   AND cursor_run_id IS NOT NULL))
);
REVOKE ALL ON system.collection_run_identity_backfill
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.backfill_collection_run_identity(
 p_instance_id uuid,p_limit integer)
RETURNS TABLE(checked_rows integer,copied_rows integer,complete boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC'
AS $backfill$
DECLARE
 v_state system.collection_run_identity_backfill%ROWTYPE;
 v_row telemetry.collection_run%ROWTYPE;
 v_successor telemetry.collection_run_v2%ROWTYPE;
 v_identity telemetry.collection_run_identity%ROWTYPE;
 v_checked integer:=0;
 v_copied integer:=0;
 v_inserted integer;
BEGIN
 IF p_instance_id IS NULL OR p_limit IS NULL
    OR p_limit NOT BETWEEN 1 AND 1000 THEN
  RAISE EXCEPTION 'collection run identity copy bounds invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'collection run identity copy target scope missing'
   USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM system.collection_run_v2_backfill AS state
                WHERE state.instance_id=p_instance_id
                  AND state.completed_at IS NOT NULL) THEN
  RAISE EXCEPTION 'collection run successor copy must complete first'
   USING ERRCODE='55000';
 END IF;
 INSERT INTO system.collection_run_identity_backfill(instance_id)
 VALUES(p_instance_id) ON CONFLICT DO NOTHING;
 SELECT * INTO v_state FROM system.collection_run_identity_backfill
 WHERE instance_id=p_instance_id FOR UPDATE;
 IF v_state.completed_at IS NOT NULL THEN
  RETURN QUERY SELECT 0,0,true;
  RETURN;
 END IF;

 -- The original target/collector/time/run index provides a stable keyset.
 -- New successor inserts create identity rows even behind this cursor.
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
  SELECT successor.* INTO v_successor
  FROM telemetry.collection_run_v2 AS successor
  WHERE successor.run_id=v_row.run_id
    AND successor.started_at=v_row.started_at;
  IF NOT FOUND OR to_jsonb(v_successor) IS DISTINCT FROM to_jsonb(v_row) THEN
   RAISE EXCEPTION 'collection run identity source differs from successor'
    USING ERRCODE='55000';
  END IF;
  INSERT INTO telemetry.collection_run_identity(run_id,started_at)
  VALUES(v_row.run_id,v_row.started_at)
  ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS v_inserted=ROW_COUNT;
  v_copied:=v_copied+v_inserted;
  SELECT identity_row.* INTO v_identity
  FROM telemetry.collection_run_identity AS identity_row
  WHERE identity_row.run_id=v_row.run_id;
  IF NOT FOUND OR v_identity.started_at IS DISTINCT FROM v_row.started_at THEN
   RAISE EXCEPTION 'collection run identity differs from successor'
    USING ERRCODE='55000';
  END IF;
  v_checked:=v_checked+1;
  v_state.cursor_collector_id:=v_row.collector_id;
  v_state.cursor_started_at:=v_row.started_at;
  v_state.cursor_run_id:=v_row.run_id;
 END LOOP;
 UPDATE system.collection_run_identity_backfill AS state
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
REVOKE ALL ON FUNCTION control.backfill_collection_run_identity(uuid,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.backfill_collection_run_identity(uuid,integer)
 TO sqlobserver_migrator;
