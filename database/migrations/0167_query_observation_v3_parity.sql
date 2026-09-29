-- Read-only, target-scoped parity for a bounded interval-end window. Run the
-- historical copy first; do not use a successful window as a global cutover.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.verify_query_observation_v3_window(
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
 v_source_hash bytea:=decode(repeat('00',64),'hex');
 v_successor_hash bytea:=decode(repeat('00',64),'hex');
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
REVOKE ALL ON FUNCTION control.verify_query_observation_v3_window(
 uuid,timestamptz,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.verify_query_observation_v3_window(
 uuid,timestamptz,timestamptz,integer) TO sqlobserver_migrator;
