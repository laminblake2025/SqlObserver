-- Preliminary collection-run parity coverage. Either source or successor
-- writes invalidate the affected target/day; a cutover still needs a fence.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_day_generation (
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 utc_day date NOT NULL,
 revision bigint NOT NULL DEFAULT 0 CHECK (revision>=0),
 PRIMARY KEY(instance_id,utc_day)
);
REVOKE ALL ON system.collection_run_v2_day_generation
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE TABLE system.collection_run_v2_parity_window (
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 collector_id text NOT NULL CHECK(octet_length(collector_id) BETWEEN 1 AND 128),
 from_utc timestamptz NOT NULL,
 to_utc timestamptz NOT NULL,
 utc_day date NOT NULL,
 day_revision bigint NOT NULL CHECK(day_revision>=0),
 max_rows integer NOT NULL CHECK(max_rows BETWEEN 1 AND 100000),
 source_rows bigint NOT NULL CHECK(source_rows>=0),
 successor_rows bigint NOT NULL CHECK(successor_rows>=0),
 source_sha256 text NOT NULL CHECK(source_sha256 ~ '^[0-9a-f]{64}$'),
 successor_sha256 text NOT NULL CHECK(successor_sha256 ~ '^[0-9a-f]{64}$'),
 parity boolean NOT NULL,
 verified_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,collector_id,from_utc,to_utc),
 CONSTRAINT ck_collection_run_v2_parity_day CHECK (
  from_utc>=timestamptz '2000-01-01 00:00:00+00'
  AND to_utc>from_utc AND to_utc-from_utc<=interval '1 day'
  AND utc_day=(from_utc AT TIME ZONE 'UTC')::date
  AND utc_day=((to_utc-interval '1 microsecond') AT TIME ZONE 'UTC')::date)
);
CREATE INDEX ix_collection_run_v2_parity_day
 ON system.collection_run_v2_parity_window(instance_id,utc_day,collector_id);
REVOKE ALL ON system.collection_run_v2_parity_window
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION telemetry.bump_collection_run_v2_day_generation()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET TimeZone='UTC'
AS $bump$
DECLARE v_instance_id uuid; v_day date;
BEGIN
 FOR v_instance_id,v_day IN
  SELECT DISTINCT row.instance_id,(row.started_at AT TIME ZONE 'UTC')::date
  FROM inserted_rows AS row ORDER BY 1,2
 LOOP
  INSERT INTO system.collection_run_v2_day_generation
   (instance_id,utc_day,revision)
  VALUES(v_instance_id,v_day,1)
  ON CONFLICT(instance_id,utc_day) DO UPDATE
   SET revision=system.collection_run_v2_day_generation.revision+1;
 END LOOP;
 RETURN NULL;
END $bump$;
REVOKE ALL ON FUNCTION telemetry.bump_collection_run_v2_day_generation()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_v2_source_generation
 AFTER INSERT ON telemetry.collection_run
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION telemetry.bump_collection_run_v2_day_generation();
CREATE TRIGGER collection_run_v2_successor_generation
 AFTER INSERT ON telemetry.collection_run_v2
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION telemetry.bump_collection_run_v2_day_generation();

CREATE FUNCTION control.record_collection_run_v2_parity_window(
 p_instance_id uuid,p_collector_id text,p_from_utc timestamptz,
 p_to_utc timestamptz,p_max_rows integer)
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
 IF p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc
    OR (p_from_utc AT TIME ZONE 'UTC')::date<>
       ((p_to_utc-interval '1 microsecond') AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'collection run parity window must fit one UTC day'
   USING ERRCODE='22023';
 END IF;
 v_day:=(p_from_utc AT TIME ZONE 'UTC')::date;
 -- Read generation before verification. A concurrent insert that commits
 -- during or after verification makes this certificate stale.
 SELECT generation.revision INTO v_revision
 FROM system.collection_run_v2_day_generation AS generation
 WHERE generation.instance_id=p_instance_id AND generation.utc_day=v_day;
 v_revision:=coalesce(v_revision,0);
 SELECT * INTO v_result FROM control.verify_collection_run_v2_window(
  p_instance_id,p_collector_id,p_from_utc,p_to_utc,p_max_rows);
 INSERT INTO system.collection_run_v2_parity_window AS parity_row
  (instance_id,collector_id,from_utc,to_utc,utc_day,day_revision,
   max_rows,source_rows,successor_rows,source_sha256,successor_sha256,parity)
 VALUES(p_instance_id,p_collector_id,p_from_utc,p_to_utc,v_day,v_revision,
  p_max_rows,v_result.source_rows,v_result.successor_rows,
  v_result.source_sha256,v_result.successor_sha256,v_result.parity)
 ON CONFLICT(instance_id,collector_id,from_utc,to_utc) DO UPDATE SET
  day_revision=excluded.day_revision,max_rows=excluded.max_rows,
  source_rows=excluded.source_rows,successor_rows=excluded.successor_rows,
  source_sha256=excluded.source_sha256,
  successor_sha256=excluded.successor_sha256,parity=excluded.parity,
  verified_at=clock_timestamp();
 RETURN QUERY SELECT v_result.source_rows,v_result.successor_rows,
  v_result.source_sha256,v_result.successor_sha256,v_result.parity;
END $record$;
REVOKE ALL ON FUNCTION control.record_collection_run_v2_parity_window(
 uuid,text,timestamptz,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.record_collection_run_v2_parity_window(
 uuid,text,timestamptz,timestamptz,integer) TO sqlobserver_migrator;

CREATE FUNCTION control.collection_run_v2_parity_coverage(
 p_instance_id uuid,p_collector_id text)
RETURNS TABLE(from_utc timestamptz,to_utc timestamptz,
 covered_through_utc timestamptz,all_covered boolean,
 verified_windows bigint,failed_or_stale_windows bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,telemetry,system SET row_security=on
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
 IF p_instance_id IS NULL OR p_collector_id IS NULL
    OR octet_length(p_collector_id) NOT BETWEEN 1 AND 128
    OR NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'collection run parity coverage target scope missing'
   USING ERRCODE='42501';
 END IF;
 SELECT state.completed_at IS NOT NULL INTO v_complete
 FROM system.collection_run_v2_backfill AS state
 WHERE state.instance_id=p_instance_id;
 v_complete:=coalesce(v_complete,false);
 SELECT min(bounds.first_at),max(bounds.last_at) INTO v_first,v_last
 FROM (
  SELECT min(run.started_at) AS first_at,max(run.started_at) AS last_at
  FROM telemetry.collection_run AS run
  WHERE run.instance_id=p_instance_id AND run.collector_id=p_collector_id
  UNION ALL
  SELECT min(run.started_at),max(run.started_at)
  FROM telemetry.collection_run_v2 AS run
  WHERE run.instance_id=p_instance_id AND run.collector_id=p_collector_id
 ) AS bounds;
 SELECT count(*) FILTER(WHERE parity_row.parity AND
                         parity_row.day_revision=coalesce(generation.revision,0)),
        count(*) FILTER(WHERE NOT parity_row.parity OR
                         parity_row.day_revision<>coalesce(generation.revision,0))
 INTO v_verified,v_failed
 FROM system.collection_run_v2_parity_window AS parity_row
 LEFT JOIN system.collection_run_v2_day_generation AS generation
  ON generation.instance_id=parity_row.instance_id
 AND generation.utc_day=parity_row.utc_day
 WHERE parity_row.instance_id=p_instance_id
   AND parity_row.collector_id=p_collector_id;
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
  FROM system.collection_run_v2_parity_window AS parity_row
  LEFT JOIN system.collection_run_v2_day_generation AS generation
   ON generation.instance_id=parity_row.instance_id
  AND generation.utc_day=parity_row.utc_day
  WHERE parity_row.instance_id=p_instance_id
    AND parity_row.collector_id=p_collector_id AND parity_row.parity
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
REVOKE ALL ON FUNCTION control.collection_run_v2_parity_coverage(uuid,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.collection_run_v2_parity_coverage(uuid,text)
 TO sqlobserver_migrator;
