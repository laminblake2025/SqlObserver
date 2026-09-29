-- Advance one bounded M7 parity window at a time. A dense single timestamp
-- that exceeds the row cap fails visibly rather than claiming coverage.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.record_next_query_observation_v3_parity_window(
 p_instance_id uuid,p_max_rows integer)
RETURNS TABLE(from_utc timestamptz,to_utc timestamptz,
 source_rows bigint,successor_rows bigint,parity boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system SET row_security=on
SET TimeZone='UTC'
AS $next$
DECLARE
 v_coverage record;
 v_start timestamptz;
 v_day_end timestamptz;
 v_next_at timestamptz;
 v_end timestamptz;
 v_verified record;
BEGIN
 IF p_instance_id IS NULL OR p_max_rows IS NULL OR
    p_max_rows NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'query observation next parity bounds invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') OR
    NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'query observation next parity target scope missing'
   USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS (
  SELECT 1 FROM system.query_observation_v3_backfill AS state
  WHERE state.instance_id=p_instance_id AND state.completed_at IS NOT NULL
 ) THEN
  RAISE EXCEPTION 'query observation historical copy is not complete'
   USING ERRCODE='55000';
 END IF;
 SELECT * INTO v_coverage
 FROM control.query_observation_v3_parity_coverage(p_instance_id);
 IF v_coverage.all_covered THEN RETURN; END IF;
 IF v_coverage.covered_through_utc IS NULL THEN
  RAISE EXCEPTION 'query observation parity bounds are unavailable'
   USING ERRCODE='55000';
 END IF;
 v_start:=v_coverage.covered_through_utc;
 v_day_end:=(date_trunc('day',v_start AT TIME ZONE 'UTC')+
             interval '1 day') AT TIME ZONE 'UTC';

 -- The (cap+1)th row supplies an exclusive time boundary. Take the earlier
 -- source/successor boundary so neither verifier scan can exceed the cap.
 -- Legacy nullable source targets are assigned by their owning query.
 SELECT min(candidate.interval_end) INTO v_next_at
 FROM (
  (SELECT observation.interval_end
   FROM events.query_performance_observation AS observation
   JOIN events.query_performance_query AS owner
    USING(collection_run_id,database_id,query_fingerprint)
   WHERE owner.instance_id=p_instance_id
     AND observation.interval_end>=v_start
     AND observation.interval_end<v_day_end
   ORDER BY observation.interval_end
   LIMIT 1 OFFSET p_max_rows)
  UNION ALL
  (SELECT observation.interval_end
   FROM events.query_performance_observation_v3 AS observation
   WHERE observation.instance_id=p_instance_id
     AND observation.interval_end>=v_start
     AND observation.interval_end<v_day_end
   ORDER BY observation.interval_end
   LIMIT 1 OFFSET p_max_rows)
 ) AS candidate;
 v_end:=coalesce(v_next_at,v_day_end);
 IF v_end<=v_start THEN
  RAISE EXCEPTION 'query observation parity has more than % rows at one interval end',
   p_max_rows USING ERRCODE='54000';
 END IF;
 SELECT * INTO v_verified
 FROM control.record_query_observation_v3_parity_window(
  p_instance_id,v_start,v_end,p_max_rows);
 RETURN QUERY SELECT v_start,v_end,v_verified.source_rows,
  v_verified.successor_rows,v_verified.parity;
END $next$;
REVOKE ALL ON FUNCTION
 control.record_next_query_observation_v3_parity_window(uuid,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION
 control.record_next_query_observation_v3_parity_window(uuid,integer)
 TO sqlobserver_migrator;
