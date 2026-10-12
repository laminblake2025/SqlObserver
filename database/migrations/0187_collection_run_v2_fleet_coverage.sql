-- Bounded target/collector inventory for preliminary collection-run parity.
-- Pages may observe different states; final cutover needs one fenced sweep.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.list_collection_run_v2_parity_coverage(
 p_after_instance_id uuid,p_after_collector_id text,p_limit integer)
RETURNS TABLE(instance_id uuid,collector_id text,from_utc timestamptz,
 to_utc timestamptz,covered_through_utc timestamptz,
 all_covered boolean,verified_windows bigint,
 failed_or_stale_windows bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog,control,system SET row_security=on
SET TimeZone='UTC'
AS $fleet$
DECLARE
 v_instance_id uuid;
 v_collector_id text;
 v_prior_scope text:=current_setting('sqlobserver.target_scope',true);
 v_coverage record;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run fleet coverage requires migrator'
   USING ERRCODE='42501';
 END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100
    OR (p_after_instance_id IS NULL)<>(p_after_collector_id IS NULL) THEN
  RAISE EXCEPTION 'collection run fleet coverage cursor or limit invalid'
   USING ERRCODE='22023';
 END IF;
 FOR v_instance_id,v_collector_id IN
  SELECT target.instance_id,catalog.collector_id
  FROM control.observation_target AS target
  CROSS JOIN (SELECT DISTINCT contract.collector_id
              FROM control.collector_contract AS contract) AS catalog
  WHERE p_after_instance_id IS NULL
     OR (target.instance_id,catalog.collector_id)>
        (p_after_instance_id,p_after_collector_id)
  ORDER BY target.instance_id,catalog.collector_id
  LIMIT p_limit
 LOOP
  PERFORM set_config('sqlobserver.target_scope',v_instance_id::text,true);
  SELECT * INTO v_coverage
  FROM control.collection_run_v2_parity_coverage(
   v_instance_id,v_collector_id);
  RETURN QUERY SELECT v_instance_id,v_collector_id,v_coverage.from_utc,
   v_coverage.to_utc,v_coverage.covered_through_utc,
   v_coverage.all_covered,v_coverage.verified_windows,
   v_coverage.failed_or_stale_windows;
 END LOOP;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_prior_scope,''),true);
END $fleet$;
REVOKE ALL ON FUNCTION control.list_collection_run_v2_parity_coverage(
 uuid,text,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.list_collection_run_v2_parity_coverage(
 uuid,text,integer) TO sqlobserver_migrator;
