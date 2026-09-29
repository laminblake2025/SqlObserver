-- Durable preliminary M7 parity coverage. Writes invalidate only their UTC
-- day, so historical days stay checked while collection continues.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Empty windows must have the same 32-byte digest width as nonempty ones.
CREATE OR REPLACE FUNCTION control.verify_query_observation_v3_window(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
 p_max_rows integer)
RETURNS TABLE(source_rows bigint,successor_rows bigint,
 source_sha256 text,successor_sha256 text,parity boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,events,system SET row_security=on
AS $verify$
DECLARE
 v_source events.query_performance_observation%ROWTYPE;
 v_successor events.query_performance_observation_v3%ROWTYPE;
 v_source_count bigint:=0;
 v_successor_count bigint:=0;
 v_source_hash bytea:=decode(repeat('00',32),'hex');
 v_successor_hash bytea:=decode(repeat('00',32),'hex');
BEGIN
 IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL OR
    p_from_utc<timestamptz '2000-01-01 00:00:00+00' OR
    p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '1 day' OR
    p_max_rows IS NULL OR p_max_rows NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'query observation parity bounds invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') OR
    NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'query observation parity target scope missing' USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM system.query_observation_v3_backfill AS state
                WHERE state.instance_id=p_instance_id AND state.completed_at IS NOT NULL) THEN
  RAISE EXCEPTION 'query observation historical copy is not complete for target'
   USING ERRCODE='55000';
 END IF;

 -- STABLE keeps both scans on the calling statement's MVCC snapshot. Include
 -- every evidence field, not just the PK or metric counters. The old nullable
 -- target key is normalized from its immutable owning query.
 FOR v_source IN
  SELECT observation.* FROM events.query_performance_observation AS observation
  JOIN events.query_performance_query AS owner
   USING(collection_run_id,database_id,query_fingerprint)
  WHERE owner.instance_id=p_instance_id
    AND observation.interval_end>=p_from_utc
    AND observation.interval_end<p_to_utc
  ORDER BY observation.interval_end,observation.collection_run_id,
   observation.database_id,observation.query_fingerprint,
   observation.observation_key
 LOOP
  v_source_count:=v_source_count+1;
  IF v_source_count>p_max_rows THEN
   RAISE EXCEPTION 'query observation parity source window exceeds row bound'
    USING ERRCODE='54000';
  END IF;
  IF v_source.instance_id IS NOT NULL AND v_source.instance_id<>p_instance_id THEN
   RAISE EXCEPTION 'query observation source target differs from query owner'
    USING ERRCODE='23514';
  END IF;
  v_source_hash:=sha256(v_source_hash || sha256(convert_to(
   jsonb_set(to_jsonb(v_source),'{instance_id}',to_jsonb(p_instance_id))::text,
   'UTF8')));
 END LOOP;

 FOR v_successor IN
  SELECT observation.* FROM events.query_performance_observation_v3 AS observation
  WHERE observation.instance_id=p_instance_id
    AND observation.interval_end>=p_from_utc
    AND observation.interval_end<p_to_utc
  ORDER BY observation.interval_end,observation.collection_run_id,
   observation.database_id,observation.query_fingerprint,
   observation.observation_key
 LOOP
  v_successor_count:=v_successor_count+1;
  IF v_successor_count>p_max_rows THEN
   RAISE EXCEPTION 'query observation parity successor window exceeds row bound'
    USING ERRCODE='54000';
  END IF;
  v_successor_hash:=sha256(v_successor_hash || sha256(
   convert_to(to_jsonb(v_successor)::text,'UTF8')));
 END LOOP;
 RETURN QUERY SELECT v_source_count,v_successor_count,
  encode(v_source_hash,'hex'),encode(v_successor_hash,'hex'),
  v_source_count=v_successor_count AND v_source_hash=v_successor_hash;
END $verify$;

CREATE TABLE system.query_observation_v3_day_generation (
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 utc_day date NOT NULL,
 revision bigint NOT NULL DEFAULT 0 CHECK (revision>=0),
 PRIMARY KEY(instance_id,utc_day)
);
REVOKE ALL ON system.query_observation_v3_day_generation
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE TABLE system.query_observation_v3_parity_window (
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 from_utc timestamptz NOT NULL,
 to_utc timestamptz NOT NULL,
 utc_day date NOT NULL,
 day_revision bigint NOT NULL CHECK (day_revision>=0),
 max_rows integer NOT NULL CHECK (max_rows BETWEEN 1 AND 100000),
 source_rows bigint NOT NULL CHECK (source_rows>=0),
 successor_rows bigint NOT NULL CHECK (successor_rows>=0),
 source_sha256 text NOT NULL CHECK (source_sha256 ~ '^[0-9a-f]{64}$'),
 successor_sha256 text NOT NULL CHECK (successor_sha256 ~ '^[0-9a-f]{64}$'),
 parity boolean NOT NULL,
 verified_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,from_utc,to_utc),
 CONSTRAINT ck_query_observation_v3_parity_window CHECK (
  from_utc>=timestamptz '2000-01-01 00:00:00+00' AND
  to_utc>from_utc AND to_utc-from_utc<=interval '1 day' AND
  utc_day=(from_utc AT TIME ZONE 'UTC')::date AND
  utc_day=((to_utc-interval '1 microsecond') AT TIME ZONE 'UTC')::date)
);
CREATE INDEX query_observation_v3_parity_day
 ON system.query_observation_v3_parity_window(instance_id,utc_day);
REVOKE ALL ON system.query_observation_v3_parity_window
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- One change-generation write per affected target/day and source statement.
-- The global ordering avoids cross-target/day row-lock inversions here.
CREATE FUNCTION events.bump_query_observation_v3_day_generation()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET TimeZone='UTC'
AS $bump$
DECLARE v_instance_id uuid; v_day date;
BEGIN
 FOR v_instance_id,v_day IN
  SELECT DISTINCT row.instance_id,(row.interval_end AT TIME ZONE 'UTC')::date
  FROM inserted_rows AS row ORDER BY 1,2
 LOOP
  INSERT INTO system.query_observation_v3_day_generation
   (instance_id,utc_day,revision)
  VALUES(v_instance_id,v_day,1)
  ON CONFLICT(instance_id,utc_day) DO UPDATE
   SET revision=system.query_observation_v3_day_generation.revision+1;
 END LOOP;
 RETURN NULL;
END $bump$;
REVOKE ALL ON FUNCTION events.bump_query_observation_v3_day_generation()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER query_observation_v3_generation
 AFTER INSERT ON events.query_performance_observation_v3
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION events.bump_query_observation_v3_day_generation();

-- The canonical mirror groups target writes in the same order as generation
-- updates, preventing multi-target batches from taking target locks backwards.
CREATE OR REPLACE FUNCTION events.mirror_query_observation_v3()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,events,control SET TimeZone='UTC'
AS $mirror$
DECLARE
 v_instance_id uuid;
 v_day date;
 v_prior_scope text:=current_setting('sqlobserver.target_scope',true);
BEGIN
 IF EXISTS(SELECT 1 FROM inserted_rows WHERE instance_id IS NULL) THEN
  RAISE EXCEPTION 'query observation target identity is missing'
   USING ERRCODE='23514';
 END IF;
 FOR v_day IN SELECT DISTINCT (interval_end AT TIME ZONE 'UTC')::date
              FROM inserted_rows ORDER BY 1 LOOP
  PERFORM control.ensure_query_observation_v3_day(v_day);
 END LOOP;
 FOR v_instance_id IN
  SELECT DISTINCT instance_id FROM inserted_rows ORDER BY instance_id LOOP
  PERFORM set_config('sqlobserver.target_scope',v_instance_id::text,true);
  INSERT INTO events.query_performance_observation_v3
   (instance_id,collection_run_id,database_id,query_fingerprint,
    plan_fingerprint,observation_key,source,source_state,interval_start,
    interval_end,observed_at,semantics,cpu_ms,duration_ms,execution_count,
    logical_reads,writes,rows_processed)
  SELECT row.instance_id,row.collection_run_id,row.database_id,
   row.query_fingerprint,row.plan_fingerprint,row.observation_key,
   row.source,row.source_state,row.interval_start,row.interval_end,
   row.observed_at,row.semantics,row.cpu_ms,row.duration_ms,
   row.execution_count,row.logical_reads,row.writes,row.rows_processed
  FROM inserted_rows AS row WHERE row.instance_id=v_instance_id;
 END LOOP;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_prior_scope,''),true);
 RETURN NULL;
END $mirror$;
REVOKE ALL ON FUNCTION events.mirror_query_observation_v3() FROM PUBLIC;

CREATE FUNCTION control.record_query_observation_v3_parity_window(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
 p_max_rows integer)
RETURNS TABLE(source_rows bigint,successor_rows bigint,
 source_sha256 text,successor_sha256 text,parity boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,system SET row_security=on
SET TimeZone='UTC'
AS $record$
DECLARE
 v_day date;
 v_revision bigint;
 v_result record;
BEGIN
 IF p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc OR
    (p_from_utc AT TIME ZONE 'UTC')::date<>
    ((p_to_utc-interval '1 microsecond') AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'query observation parity window must fit one UTC day'
   USING ERRCODE='22023';
 END IF;
 v_day:=(p_from_utc AT TIME ZONE 'UTC')::date;
 -- Reading the generation before verification makes concurrent inserts
 -- invalidate the record whether they commit during or after this call.
 SELECT generation.revision INTO v_revision
 FROM system.query_observation_v3_day_generation AS generation
 WHERE generation.instance_id=p_instance_id AND generation.utc_day=v_day;
 v_revision:=coalesce(v_revision,0);
 SELECT * INTO v_result FROM control.verify_query_observation_v3_window(
  p_instance_id,p_from_utc,p_to_utc,p_max_rows);
 INSERT INTO system.query_observation_v3_parity_window AS parity_row
  (instance_id,from_utc,to_utc,utc_day,day_revision,max_rows,
   source_rows,successor_rows,source_sha256,successor_sha256,parity)
 VALUES(p_instance_id,p_from_utc,p_to_utc,v_day,v_revision,p_max_rows,
  v_result.source_rows,v_result.successor_rows,v_result.source_sha256,
  v_result.successor_sha256,v_result.parity)
 ON CONFLICT(instance_id,from_utc,to_utc) DO UPDATE SET
  day_revision=excluded.day_revision,max_rows=excluded.max_rows,
  source_rows=excluded.source_rows,successor_rows=excluded.successor_rows,
  source_sha256=excluded.source_sha256,
  successor_sha256=excluded.successor_sha256,parity=excluded.parity,
  verified_at=clock_timestamp();
 RETURN QUERY SELECT v_result.source_rows,v_result.successor_rows,
  v_result.source_sha256,v_result.successor_sha256,v_result.parity;
END $record$;
REVOKE ALL ON FUNCTION control.record_query_observation_v3_parity_window(
 uuid,timestamptz,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.record_query_observation_v3_parity_window(
 uuid,timestamptz,timestamptz,integer) TO sqlobserver_migrator;

CREATE FUNCTION control.query_observation_v3_parity_coverage(p_instance_id uuid)
RETURNS TABLE(from_utc timestamptz,to_utc timestamptz,
 covered_through_utc timestamptz,all_covered boolean,
 verified_windows bigint,failed_or_stale_windows bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,events,system SET row_security=on
SET TimeZone='UTC'
AS $coverage$
DECLARE
 v_first timestamptz;
 v_last timestamptz;
 v_from timestamptz;
 v_to timestamptz;
 v_cursor timestamptz;
 v_verified bigint;
 v_failed bigint;
 v_complete boolean;
 v_window record;
BEGIN
 IF p_instance_id IS NULL OR
    NOT pg_has_role(session_user,'sqlobserver_migrator','member') OR
    NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'query observation parity coverage target scope missing'
   USING ERRCODE='42501';
 END IF;
 SELECT state.completed_at IS NOT NULL INTO v_complete
 FROM system.query_observation_v3_backfill AS state
 WHERE state.instance_id=p_instance_id;
 v_complete:=coalesce(v_complete,false);
 SELECT min(bounds.first_at),max(bounds.last_at) INTO v_first,v_last
 FROM (
  SELECT min(observation.interval_end) AS first_at,
         max(observation.interval_end) AS last_at
  FROM events.query_performance_observation AS observation
  JOIN events.query_performance_query AS owner
   USING(collection_run_id,database_id,query_fingerprint)
  WHERE owner.instance_id=p_instance_id
  UNION ALL
  SELECT min(observation.interval_end),max(observation.interval_end)
  FROM events.query_performance_observation_v3 AS observation
  WHERE observation.instance_id=p_instance_id
 ) AS bounds;
 SELECT count(*) FILTER(WHERE parity_row.parity AND
                         parity_row.day_revision=coalesce(generation.revision,0)),
        count(*) FILTER(WHERE NOT parity_row.parity OR
                         parity_row.day_revision<>coalesce(generation.revision,0))
 INTO v_verified,v_failed
 FROM system.query_observation_v3_parity_window AS parity_row
 LEFT JOIN system.query_observation_v3_day_generation AS generation
  ON generation.instance_id=parity_row.instance_id
 AND generation.utc_day=parity_row.utc_day
 WHERE parity_row.instance_id=p_instance_id;
 IF v_first IS NULL THEN
  RETURN QUERY SELECT NULL::timestamptz,NULL::timestamptz,
   NULL::timestamptz,v_complete,v_verified,v_failed;
  RETURN;
 END IF;
 v_from:=date_trunc('day',v_first AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
 v_to:=(date_trunc('day',v_last AT TIME ZONE 'UTC')+
        interval '1 day') AT TIME ZONE 'UTC';
 v_cursor:=v_from;
 FOR v_window IN
  SELECT parity_row.from_utc,parity_row.to_utc
  FROM system.query_observation_v3_parity_window AS parity_row
  LEFT JOIN system.query_observation_v3_day_generation AS generation
   ON generation.instance_id=parity_row.instance_id
  AND generation.utc_day=parity_row.utc_day
  WHERE parity_row.instance_id=p_instance_id AND parity_row.parity
    AND parity_row.day_revision=coalesce(generation.revision,0)
    AND parity_row.to_utc>v_from AND parity_row.from_utc<v_to
  ORDER BY parity_row.from_utc,parity_row.to_utc
 LOOP
  EXIT WHEN v_window.from_utc>v_cursor;
  v_cursor:=greatest(v_cursor,v_window.to_utc);
  EXIT WHEN v_cursor>=v_to;
 END LOOP;
 RETURN QUERY SELECT v_from,v_to,least(v_cursor,v_to),
  v_complete AND v_cursor>=v_to,v_verified,v_failed;
END $coverage$;
REVOKE ALL ON FUNCTION control.query_observation_v3_parity_coverage(uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.query_observation_v3_parity_coverage(uuid)
 TO sqlobserver_migrator;
