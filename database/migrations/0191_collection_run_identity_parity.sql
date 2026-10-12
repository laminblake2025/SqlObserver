-- Check identity coverage independently of the copy cursor for one bounded
-- successor window. This check alone does not authorize a foreign-key cutover.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.verify_collection_run_identity_window(
 p_instance_id uuid,p_collector_id text,p_from_utc timestamptz,
 p_to_utc timestamptz,p_max_rows integer)
RETURNS TABLE(successor_rows bigint,identity_rows bigint,
 successor_sha256 text,identity_sha256 text,parity boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC'
AS $verify$
DECLARE
 v_pair record;
 v_successor_count bigint:=0;
 v_identity_count bigint:=0;
 v_successor_hash bytea:=decode(repeat('00',32),'hex');
 v_identity_hash bytea:=decode(repeat('00',32),'hex');
 v_key bytea;
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
  RAISE EXCEPTION 'collection run identity parity bounds invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'collection run identity parity target scope missing'
   USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM system.collection_run_v2_backfill AS state
                WHERE state.instance_id=p_instance_id
                  AND state.completed_at IS NOT NULL)
    OR NOT EXISTS (
     SELECT 1 FROM system.collection_run_identity_backfill AS state
     WHERE state.instance_id=p_instance_id
       AND state.completed_at IS NOT NULL) THEN
  RAISE EXCEPTION 'collection run successor and identity copies must complete'
   USING ERRCODE='55000';
 END IF;

 -- The identity FK prevents extra bridge rows. A left join exposes missing
 -- or differently dated identities while both sides share one snapshot.
 FOR v_pair IN
  SELECT run.run_id,run.started_at,
         identity_row.run_id IS NOT NULL AS has_identity
  FROM telemetry.collection_run_v2 AS run
  LEFT JOIN telemetry.collection_run_identity AS identity_row
    ON identity_row.run_id=run.run_id
   AND identity_row.started_at=run.started_at
  WHERE run.instance_id=p_instance_id
    AND run.collector_id=p_collector_id
    AND run.started_at>=p_from_utc AND run.started_at<p_to_utc
  ORDER BY run.started_at,run.run_id
  LIMIT p_max_rows+1
 LOOP
  v_successor_count:=v_successor_count+1;
  IF v_successor_count>p_max_rows THEN
   RAISE EXCEPTION 'collection run identity parity window exceeds row bound'
    USING ERRCODE='54000';
  END IF;
  v_key:=sha256(convert_to(jsonb_build_object(
   'run_id',v_pair.run_id,'started_at',v_pair.started_at)::text,'UTF8'));
  v_successor_hash:=sha256(v_successor_hash || v_key);
  IF v_pair.has_identity THEN
   v_identity_count:=v_identity_count+1;
   v_identity_hash:=sha256(v_identity_hash || v_key);
  END IF;
 END LOOP;
 RETURN QUERY SELECT v_successor_count,v_identity_count,
  encode(v_successor_hash,'hex'),encode(v_identity_hash,'hex'),
  v_successor_count=v_identity_count
   AND v_successor_hash=v_identity_hash;
END $verify$;
REVOKE ALL ON FUNCTION control.verify_collection_run_identity_window(
 uuid,text,timestamptz,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.verify_collection_run_identity_window(
 uuid,text,timestamptz,timestamptz,integer) TO sqlobserver_migrator;
