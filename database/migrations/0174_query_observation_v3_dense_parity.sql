-- Verify a single dense interval_end in bounded, ordered key chunks. Each
-- side has its own cursor so missing or extra keys cannot be skipped.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.query_observation_v3_parity_window
 ADD COLUMN verification_method text NOT NULL DEFAULT 'time_window'
 CHECK (verification_method IN ('time_window','key_chunks'));

CREATE TABLE system.query_observation_v3_dense_parity (
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 interval_end timestamptz NOT NULL,
 utc_day date NOT NULL,
 day_revision bigint NOT NULL CHECK(day_revision>=0),
 source_cursor_run_id uuid,
 source_cursor_database_id integer,
 source_cursor_query_fingerprint bytea,
 source_cursor_observation_key bytea,
 successor_cursor_run_id uuid,
 successor_cursor_database_id integer,
 successor_cursor_query_fingerprint bytea,
 successor_cursor_observation_key bytea,
 source_rows bigint NOT NULL DEFAULT 0 CHECK(source_rows>=0),
 successor_rows bigint NOT NULL DEFAULT 0 CHECK(successor_rows>=0),
 source_hash bytea NOT NULL DEFAULT decode(repeat('00',32),'hex')
  CHECK(octet_length(source_hash)=32),
 successor_hash bytea NOT NULL DEFAULT decode(repeat('00',32),'hex')
  CHECK(octet_length(successor_hash)=32),
 complete boolean NOT NULL DEFAULT false,
 parity boolean,
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,interval_end),
 CONSTRAINT ck_query_observation_v3_dense_day CHECK(
  utc_day=(interval_end AT TIME ZONE 'UTC')::date),
 CONSTRAINT ck_query_observation_v3_dense_source_cursor CHECK(
  (source_cursor_run_id IS NULL AND source_cursor_database_id IS NULL AND
   source_cursor_query_fingerprint IS NULL AND
   source_cursor_observation_key IS NULL) OR
  (source_cursor_run_id IS NOT NULL AND source_cursor_database_id IS NOT NULL AND
   source_cursor_query_fingerprint IS NOT NULL AND
   source_cursor_observation_key IS NOT NULL)),
 CONSTRAINT ck_query_observation_v3_dense_successor_cursor CHECK(
  (successor_cursor_run_id IS NULL AND successor_cursor_database_id IS NULL AND
   successor_cursor_query_fingerprint IS NULL AND
   successor_cursor_observation_key IS NULL) OR
  (successor_cursor_run_id IS NOT NULL AND successor_cursor_database_id IS NOT NULL AND
   successor_cursor_query_fingerprint IS NOT NULL AND
   successor_cursor_observation_key IS NOT NULL)),
 CONSTRAINT ck_query_observation_v3_dense_result CHECK(
  complete=(parity IS NOT NULL))
);
REVOKE ALL ON system.query_observation_v3_dense_parity
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- A source-only insert must stale parity even if mirroring is temporarily
-- unavailable. Normal canonical inserts now advance a day twice (once per
-- relation), which is harmless; replay advances neither generation.
CREATE TRIGGER query_observation_v3_source_generation
 AFTER INSERT ON events.query_performance_observation
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION events.bump_query_observation_v3_day_generation();

-- Keep this extra integrity check at the route row. The earlier activation
-- function already checks the mirror and successor generation triggers.
CREATE FUNCTION control.require_query_observation_v3_source_generation()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,events,system
AS $source_gate$
BEGIN
 IF NEW.use_v3 AND NOT EXISTS (
  SELECT 1 FROM pg_trigger AS trigger_row
  WHERE trigger_row.tgrelid='events.query_performance_observation'::regclass
    AND trigger_row.tgname='query_observation_v3_source_generation'
    AND trigger_row.tgenabled IN ('O','A')
 ) THEN
  RAISE EXCEPTION 'query observation source generation trigger is disabled'
   USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $source_gate$;
REVOKE ALL ON FUNCTION control.require_query_observation_v3_source_generation()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER query_observation_v3_source_generation_gate
 BEFORE UPDATE OF use_v3 ON system.query_observation_v3_read_route
 FOR EACH ROW EXECUTE FUNCTION
 control.require_query_observation_v3_source_generation();

CREATE FUNCTION control.record_query_observation_v3_dense_point(
 p_instance_id uuid,p_interval_end timestamptz,p_max_rows integer)
RETURNS TABLE(source_rows bigint,successor_rows bigint,
 complete boolean,parity boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system SET row_security=on
SET TimeZone='UTC'
AS $dense$
DECLARE
 v_day date;
 v_revision bigint;
 v_state system.query_observation_v3_dense_parity%ROWTYPE;
 v_source events.query_performance_observation%ROWTYPE;
 v_successor events.query_performance_observation_v3%ROWTYPE;
 v_source_more boolean;
 v_successor_more boolean;
BEGIN
 IF p_instance_id IS NULL OR p_interval_end IS NULL OR
    p_interval_end<timestamptz '2000-01-01 00:00:00+00' OR
    p_max_rows IS NULL OR p_max_rows NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'query observation dense parity bounds invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') OR
    NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'query observation dense parity target scope missing'
   USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS (
  SELECT 1 FROM system.query_observation_v3_backfill AS state
  WHERE state.instance_id=p_instance_id AND state.completed_at IS NOT NULL
 ) THEN
  RAISE EXCEPTION 'query observation historical copy is not complete'
   USING ERRCODE='55000';
 END IF;
 v_day:=(p_interval_end AT TIME ZONE 'UTC')::date;
 INSERT INTO system.query_observation_v3_day_generation
  (instance_id,utc_day,revision) VALUES(p_instance_id,v_day,0)
 ON CONFLICT DO NOTHING;
 SELECT generation.revision INTO v_revision
 FROM system.query_observation_v3_day_generation AS generation
 WHERE generation.instance_id=p_instance_id AND generation.utc_day=v_day
 FOR UPDATE;
 INSERT INTO system.query_observation_v3_dense_parity
  (instance_id,interval_end,utc_day,day_revision)
 VALUES(p_instance_id,p_interval_end,v_day,v_revision)
 ON CONFLICT DO NOTHING;
 SELECT * INTO v_state FROM system.query_observation_v3_dense_parity AS state
 WHERE state.instance_id=p_instance_id AND state.interval_end=p_interval_end
 FOR UPDATE;
 IF v_state.day_revision<>v_revision THEN
  UPDATE system.query_observation_v3_dense_parity AS state
  SET day_revision=v_revision,
      source_cursor_run_id=NULL,source_cursor_database_id=NULL,
      source_cursor_query_fingerprint=NULL,source_cursor_observation_key=NULL,
      successor_cursor_run_id=NULL,successor_cursor_database_id=NULL,
      successor_cursor_query_fingerprint=NULL,
      successor_cursor_observation_key=NULL,
      source_rows=0,successor_rows=0,
      source_hash=decode(repeat('00',32),'hex'),
      successor_hash=decode(repeat('00',32),'hex'),
      complete=false,parity=NULL,updated_at=clock_timestamp()
  WHERE state.instance_id=p_instance_id AND state.interval_end=p_interval_end;
  SELECT * INTO v_state FROM system.query_observation_v3_dense_parity AS state
  WHERE state.instance_id=p_instance_id AND state.interval_end=p_interval_end;
 END IF;
 IF v_state.complete THEN
  RETURN QUERY SELECT v_state.source_rows,v_state.successor_rows,
   v_state.complete,v_state.parity;
  RETURN;
 END IF;

 FOR v_source IN
  SELECT observation.* FROM events.query_performance_observation AS observation
  JOIN events.query_performance_query AS owner
   USING(collection_run_id,database_id,query_fingerprint)
  WHERE owner.instance_id=p_instance_id
    AND observation.interval_end=p_interval_end
    AND (v_state.source_cursor_run_id IS NULL OR
     (observation.collection_run_id,observation.database_id,
      observation.query_fingerprint,observation.observation_key) >
     (v_state.source_cursor_run_id,v_state.source_cursor_database_id,
      v_state.source_cursor_query_fingerprint,
      v_state.source_cursor_observation_key))
  ORDER BY observation.collection_run_id,observation.database_id,
   observation.query_fingerprint,observation.observation_key
  LIMIT p_max_rows
 LOOP
  IF v_source.instance_id IS NOT NULL AND
     v_source.instance_id<>p_instance_id THEN
   RAISE EXCEPTION 'query observation dense source target differs from owner'
    USING ERRCODE='23514';
  END IF;
  v_state.source_hash:=sha256(v_state.source_hash || sha256(convert_to(
   jsonb_set(to_jsonb(v_source),'{instance_id}',to_jsonb(p_instance_id))::text,
   'UTF8')));
  v_state.source_rows:=v_state.source_rows+1;
  v_state.source_cursor_run_id:=v_source.collection_run_id;
  v_state.source_cursor_database_id:=v_source.database_id;
  v_state.source_cursor_query_fingerprint:=v_source.query_fingerprint;
  v_state.source_cursor_observation_key:=v_source.observation_key;
 END LOOP;
 FOR v_successor IN
  SELECT observation.* FROM events.query_performance_observation_v3 AS observation
  WHERE observation.instance_id=p_instance_id
    AND observation.interval_end=p_interval_end
    AND (v_state.successor_cursor_run_id IS NULL OR
     (observation.collection_run_id,observation.database_id,
      observation.query_fingerprint,observation.observation_key) >
     (v_state.successor_cursor_run_id,v_state.successor_cursor_database_id,
      v_state.successor_cursor_query_fingerprint,
      v_state.successor_cursor_observation_key))
  ORDER BY observation.collection_run_id,observation.database_id,
   observation.query_fingerprint,observation.observation_key
  LIMIT p_max_rows
 LOOP
  v_state.successor_hash:=sha256(v_state.successor_hash || sha256(
   convert_to(to_jsonb(v_successor)::text,'UTF8')));
  v_state.successor_rows:=v_state.successor_rows+1;
  v_state.successor_cursor_run_id:=v_successor.collection_run_id;
  v_state.successor_cursor_database_id:=v_successor.database_id;
  v_state.successor_cursor_query_fingerprint:=v_successor.query_fingerprint;
  v_state.successor_cursor_observation_key:=v_successor.observation_key;
 END LOOP;
 SELECT EXISTS(
  SELECT 1 FROM events.query_performance_observation AS observation
  JOIN events.query_performance_query AS owner
   USING(collection_run_id,database_id,query_fingerprint)
  WHERE owner.instance_id=p_instance_id
    AND observation.interval_end=p_interval_end
    AND (v_state.source_cursor_run_id IS NULL OR
     (observation.collection_run_id,observation.database_id,
      observation.query_fingerprint,observation.observation_key) >
     (v_state.source_cursor_run_id,v_state.source_cursor_database_id,
      v_state.source_cursor_query_fingerprint,
      v_state.source_cursor_observation_key))) INTO v_source_more;
 SELECT EXISTS(
  SELECT 1 FROM events.query_performance_observation_v3 AS observation
  WHERE observation.instance_id=p_instance_id
    AND observation.interval_end=p_interval_end
    AND (v_state.successor_cursor_run_id IS NULL OR
     (observation.collection_run_id,observation.database_id,
      observation.query_fingerprint,observation.observation_key) >
     (v_state.successor_cursor_run_id,v_state.successor_cursor_database_id,
      v_state.successor_cursor_query_fingerprint,
      v_state.successor_cursor_observation_key))) INTO v_successor_more;
 v_state.complete:=NOT v_source_more AND NOT v_successor_more;
 v_state.parity:=CASE WHEN v_state.complete THEN
  v_state.source_rows=v_state.successor_rows AND
  v_state.source_hash=v_state.successor_hash ELSE NULL END;
 UPDATE system.query_observation_v3_dense_parity AS state
 SET source_cursor_run_id=v_state.source_cursor_run_id,
     source_cursor_database_id=v_state.source_cursor_database_id,
     source_cursor_query_fingerprint=v_state.source_cursor_query_fingerprint,
     source_cursor_observation_key=v_state.source_cursor_observation_key,
     successor_cursor_run_id=v_state.successor_cursor_run_id,
     successor_cursor_database_id=v_state.successor_cursor_database_id,
     successor_cursor_query_fingerprint=v_state.successor_cursor_query_fingerprint,
     successor_cursor_observation_key=v_state.successor_cursor_observation_key,
     source_rows=v_state.source_rows,successor_rows=v_state.successor_rows,
     source_hash=v_state.source_hash,successor_hash=v_state.successor_hash,
     complete=v_state.complete,parity=v_state.parity,
     updated_at=clock_timestamp()
 WHERE state.instance_id=p_instance_id AND state.interval_end=p_interval_end;
 IF v_state.complete THEN
  INSERT INTO system.query_observation_v3_parity_window AS parity_row
   (instance_id,from_utc,to_utc,utc_day,day_revision,max_rows,
    source_rows,successor_rows,source_sha256,successor_sha256,parity,
    verification_method)
  VALUES(p_instance_id,p_interval_end,p_interval_end+interval '1 microsecond',
   v_day,v_revision,p_max_rows,v_state.source_rows,v_state.successor_rows,
   encode(v_state.source_hash,'hex'),encode(v_state.successor_hash,'hex'),
   v_state.parity,'key_chunks')
  ON CONFLICT(instance_id,from_utc,to_utc) DO UPDATE SET
   day_revision=excluded.day_revision,max_rows=excluded.max_rows,
   source_rows=excluded.source_rows,successor_rows=excluded.successor_rows,
   source_sha256=excluded.source_sha256,
   successor_sha256=excluded.successor_sha256,parity=excluded.parity,
   verification_method='key_chunks',verified_at=clock_timestamp();
 END IF;
 RETURN QUERY SELECT v_state.source_rows,v_state.successor_rows,
  v_state.complete,v_state.parity;
END $dense$;
REVOKE ALL ON FUNCTION
 control.record_query_observation_v3_dense_point(uuid,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION
 control.record_query_observation_v3_dense_point(uuid,timestamptz,integer)
 TO sqlobserver_migrator;
