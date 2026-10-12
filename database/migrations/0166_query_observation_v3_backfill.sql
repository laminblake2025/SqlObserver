-- Explicit, target-scoped historical M7 copy. Each call commits at most 1000
-- observations; the original relation remains the read source.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.query_observation_v3_backfill (
 instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
 cursor_run_id uuid,
 cursor_database_id integer,
 cursor_query_fingerprint bytea,
 cursor_observation_key bytea,
 checked_rows bigint NOT NULL DEFAULT 0 CHECK (checked_rows >= 0),
 copied_rows bigint NOT NULL DEFAULT 0 CHECK (copied_rows >= 0),
 completed_at timestamptz,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT ck_query_observation_v3_cursor CHECK (
  (cursor_run_id IS NULL AND cursor_database_id IS NULL AND
   cursor_query_fingerprint IS NULL AND cursor_observation_key IS NULL) OR
  (cursor_run_id IS NOT NULL AND cursor_database_id IS NOT NULL AND
   cursor_query_fingerprint IS NOT NULL AND cursor_observation_key IS NOT NULL))
);
REVOKE ALL ON system.query_observation_v3_backfill FROM PUBLIC,
 sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.backfill_query_observation_v3(
 p_instance_id uuid,p_limit integer)
RETURNS TABLE(checked_rows integer,copied_rows integer,complete boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system SET row_security=on
AS $backfill$
DECLARE
 v_state system.query_observation_v3_backfill%ROWTYPE;
 v_row events.query_performance_observation%ROWTYPE;
 v_mirror events.query_performance_observation_v3%ROWTYPE;
 v_checked integer:=0;
 v_copied integer:=0;
 v_inserted integer;
BEGIN
 IF p_instance_id IS NULL OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN
  RAISE EXCEPTION 'query observation copy bounds invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') OR
    NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'query observation copy target scope missing' USING ERRCODE='42501';
 END IF;
 INSERT INTO system.query_observation_v3_backfill(instance_id)
 VALUES(p_instance_id) ON CONFLICT DO NOTHING;
 SELECT * INTO v_state FROM system.query_observation_v3_backfill
 WHERE instance_id=p_instance_id FOR UPDATE;
 IF v_state.completed_at IS NOT NULL THEN
  RETURN QUERY SELECT 0,0,true;
  RETURN;
 END IF;

 -- The old PK is the stable scan cursor. Any insert after migration 0165 is
 -- already mirrored, even if its key sorts behind this cursor.
 FOR v_row IN
  SELECT observation.*
  FROM events.query_performance_observation AS observation
  JOIN events.query_performance_query AS owner
   USING(collection_run_id,database_id,query_fingerprint)
  WHERE owner.instance_id=p_instance_id
    AND (v_state.cursor_run_id IS NULL OR
     (observation.collection_run_id,observation.database_id,
      observation.query_fingerprint,observation.observation_key) >
     (v_state.cursor_run_id,v_state.cursor_database_id,
      v_state.cursor_query_fingerprint,v_state.cursor_observation_key))
  ORDER BY observation.collection_run_id,observation.database_id,
   observation.query_fingerprint,observation.observation_key
  LIMIT p_limit
 LOOP
  IF v_row.instance_id IS NOT NULL AND v_row.instance_id<>p_instance_id THEN
   RAISE EXCEPTION 'query observation target identity differs from query owner'
    USING ERRCODE='23514';
  END IF;
  PERFORM control.ensure_query_observation_v3_day(
   (v_row.interval_end AT TIME ZONE 'UTC')::date);
  INSERT INTO events.query_performance_observation_v3
   (instance_id,collection_run_id,database_id,query_fingerprint,
    plan_fingerprint,observation_key,source,source_state,interval_start,
    interval_end,observed_at,semantics,cpu_ms,duration_ms,execution_count,
    logical_reads,writes,rows_processed)
  VALUES(p_instance_id,v_row.collection_run_id,v_row.database_id,
   v_row.query_fingerprint,v_row.plan_fingerprint,v_row.observation_key,
   v_row.source,v_row.source_state,v_row.interval_start,v_row.interval_end,
   v_row.observed_at,v_row.semantics,v_row.cpu_ms,v_row.duration_ms,
   v_row.execution_count,v_row.logical_reads,v_row.writes,v_row.rows_processed)
  ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS v_inserted=ROW_COUNT;
  v_copied:=v_copied+v_inserted;
  SELECT mirror.* INTO v_mirror FROM events.query_performance_observation_v3 AS mirror
  WHERE mirror.interval_end=v_row.interval_end
    AND mirror.collection_run_id=v_row.collection_run_id
    AND mirror.database_id=v_row.database_id
    AND mirror.query_fingerprint=v_row.query_fingerprint
    AND mirror.observation_key=v_row.observation_key;
  IF NOT FOUND OR to_jsonb(v_mirror) IS DISTINCT FROM
      jsonb_set(to_jsonb(v_row),'{instance_id}',to_jsonb(p_instance_id)) THEN
   RAISE EXCEPTION 'query observation partitioned copy differs from source'
    USING ERRCODE='55000';
  END IF;
  v_checked:=v_checked+1;
  v_state.cursor_run_id:=v_row.collection_run_id;
  v_state.cursor_database_id:=v_row.database_id;
  v_state.cursor_query_fingerprint:=v_row.query_fingerprint;
  v_state.cursor_observation_key:=v_row.observation_key;
 END LOOP;
 UPDATE system.query_observation_v3_backfill AS state
 SET cursor_run_id=v_state.cursor_run_id,
     cursor_database_id=v_state.cursor_database_id,
     cursor_query_fingerprint=v_state.cursor_query_fingerprint,
     cursor_observation_key=v_state.cursor_observation_key,
     checked_rows=state.checked_rows+v_checked,
     copied_rows=state.copied_rows+v_copied,
     completed_at=CASE WHEN v_checked<p_limit THEN clock_timestamp()
                       ELSE NULL END,
     updated_at=clock_timestamp()
 WHERE state.instance_id=p_instance_id;
 RETURN QUERY SELECT v_checked,v_copied,v_checked<p_limit;
END $backfill$;
REVOKE ALL ON FUNCTION control.backfill_query_observation_v3(uuid,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.backfill_query_observation_v3(uuid,integer)
 TO sqlobserver_migrator;
