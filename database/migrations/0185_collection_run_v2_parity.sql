-- Compare complete run-start rows under one MVCC snapshot for one target,
-- collector and UTC-day-bounded window. A passing window is not a cutover gate.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.verify_collection_run_v2_window(
 p_instance_id uuid,p_collector_id text,p_from_utc timestamptz,
 p_to_utc timestamptz,p_max_rows integer)
RETURNS TABLE(source_rows bigint,successor_rows bigint,
 source_sha256 text,successor_sha256 text,parity boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC'
AS $verify$
DECLARE
 v_source telemetry.collection_run%ROWTYPE;
 v_successor telemetry.collection_run_v2%ROWTYPE;
 v_source_count bigint:=0;
 v_successor_count bigint:=0;
 v_source_hash bytea:=decode(repeat('00',32),'hex');
 v_successor_hash bytea:=decode(repeat('00',32),'hex');
BEGIN
 IF p_instance_id IS NULL OR p_collector_id IS NULL
    OR octet_length(p_collector_id) NOT BETWEEN 1 AND 128
    OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR p_from_utc<timestamptz '2000-01-01 00:00:00+00'
    OR p_to_utc<=p_from_utc
    OR p_to_utc-p_from_utc>interval '1 day'
    OR ((p_to_utc-interval '1 microsecond') AT TIME ZONE 'UTC')::date
       <>(p_from_utc AT TIME ZONE 'UTC')::date
    OR p_max_rows IS NULL OR p_max_rows NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'collection run parity bounds invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'collection run parity target scope missing'
   USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM system.collection_run_v2_backfill AS state
                WHERE state.instance_id=p_instance_id
                  AND state.completed_at IS NOT NULL) THEN
  RAISE EXCEPTION 'collection run historical copy is not complete for target'
   USING ERRCODE='55000';
 END IF;

 -- Both index scans use the statement snapshot. Hash all evidence fields in
 -- stable key order and stop after the first row beyond the requested bound.
 FOR v_source IN
  SELECT run.* FROM telemetry.collection_run AS run
  WHERE run.instance_id=p_instance_id
    AND run.collector_id=p_collector_id
    AND run.started_at>=p_from_utc AND run.started_at<p_to_utc
  ORDER BY run.started_at,run.run_id
  LIMIT p_max_rows+1
 LOOP
  v_source_count:=v_source_count+1;
  IF v_source_count>p_max_rows THEN
   RAISE EXCEPTION 'collection run parity source window exceeds row bound'
    USING ERRCODE='54000';
  END IF;
  v_source_hash:=sha256(v_source_hash || sha256(
   convert_to(to_jsonb(v_source)::text,'UTF8')));
 END LOOP;
 FOR v_successor IN
  SELECT run.* FROM telemetry.collection_run_v2 AS run
  WHERE run.instance_id=p_instance_id
    AND run.collector_id=p_collector_id
    AND run.started_at>=p_from_utc AND run.started_at<p_to_utc
  ORDER BY run.started_at,run.run_id
  LIMIT p_max_rows+1
 LOOP
  v_successor_count:=v_successor_count+1;
  IF v_successor_count>p_max_rows THEN
   RAISE EXCEPTION 'collection run parity successor window exceeds row bound'
    USING ERRCODE='54000';
  END IF;
  v_successor_hash:=sha256(v_successor_hash || sha256(
   convert_to(to_jsonb(v_successor)::text,'UTF8')));
 END LOOP;
 RETURN QUERY SELECT v_source_count,v_successor_count,
  encode(v_source_hash,'hex'),encode(v_successor_hash,'hex'),
  v_source_count=v_successor_count AND v_source_hash=v_successor_hash;
END $verify$;
REVOKE ALL ON FUNCTION control.verify_collection_run_v2_window(
 uuid,text,timestamptz,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.verify_collection_run_v2_window(
 uuid,text,timestamptz,timestamptz,integer) TO sqlobserver_migrator;
