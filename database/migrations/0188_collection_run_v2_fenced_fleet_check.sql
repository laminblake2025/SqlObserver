-- Verify the whole collection-run successor under one database writer fence.
-- This returns a transient proof; a future cutover must call it while holding
-- the same transaction, then change routes and foreign keys before commit.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.verify_collection_run_v2_fleet_fenced(
 p_max_pairs integer DEFAULT 10000)
RETURNS TABLE(target_count bigint,collector_count bigint,pair_count bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system SET row_security=on
SET TimeZone='UTC'
AS $fence$
DECLARE
 v_target_count bigint;
 v_collector_count bigint;
 v_pairs bigint:=0;
 v_instance_id uuid;
 v_collector_id text;
 v_coverage record;
 v_prior_scope text:=current_setting('sqlobserver.target_scope',true);
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run fleet check requires migrator'
   USING ERRCODE='42501';
 END IF;
 IF p_max_pairs IS NULL OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'collection run fleet check pair bound invalid'
   USING ERRCODE='22023';
 END IF;

 -- SHARE conflicts with the ROW EXCLUSIVE locks taken by every relevant
 -- insert or ledger update. These locks last through the caller transaction.
 LOCK TABLE control.observation_target IN SHARE MODE;
 LOCK TABLE control.collector_contract IN SHARE MODE;
 LOCK TABLE telemetry.collection_run IN SHARE MODE;
 LOCK TABLE telemetry.collection_run_v2 IN SHARE MODE;
 LOCK TABLE system.partition_registry IN SHARE MODE;
 LOCK TABLE system.collection_run_v2_backfill IN SHARE MODE;
 LOCK TABLE system.collection_run_v2_day_generation IN SHARE MODE;
 LOCK TABLE system.collection_run_v2_parity_window IN SHARE MODE;

 IF EXISTS (
  SELECT 1 FROM (VALUES
   ('telemetry.collection_run'::regclass,'collection_run_append_only'),
   ('telemetry.collection_run'::regclass,'collection_run_v2_mirror'),
   ('telemetry.collection_run'::regclass,
    'collection_run_v2_source_generation'),
   ('telemetry.collection_run_v2'::regclass,
    'collection_run_v2_append_only'),
   ('telemetry.collection_run_v2'::regclass,
    'collection_run_v2_successor_generation')
  ) AS required(relation_id,trigger_name)
  LEFT JOIN pg_trigger AS trigger_row
   ON trigger_row.tgrelid=required.relation_id
  AND trigger_row.tgname=required.trigger_name
  AND trigger_row.tgenabled IN ('O','A')
  WHERE trigger_row.oid IS NULL
 ) OR EXISTS (
  SELECT 1 FROM (VALUES
   ('telemetry.collection_run'::regclass),
   ('telemetry.collection_run_v2'::regclass)
  ) AS required(relation_id)
  LEFT JOIN pg_class AS relation ON relation.oid=required.relation_id
  WHERE relation.oid IS NULL OR NOT relation.relrowsecurity
     OR NOT relation.relforcerowsecurity
 ) OR EXISTS (
  SELECT 1 FROM system.partition_registry AS registry
  WHERE registry.parent_schema='telemetry'
    AND registry.parent_table='collection_run_v2'
    AND registry.lifecycle_state<>'attached'
 ) THEN
  RAISE EXCEPTION 'collection run mirror, generation, scope, or partition gate is disabled'
   USING ERRCODE='55000';
 END IF;

 SELECT count(*) INTO v_target_count FROM control.observation_target;
 SELECT count(*) INTO v_collector_count FROM (
  SELECT DISTINCT contract.collector_id
  FROM control.collector_contract AS contract) AS catalog;
 IF v_collector_count=0 THEN
  RAISE EXCEPTION 'collection run collector catalog is empty'
   USING ERRCODE='55000';
 END IF;
 IF v_target_count*v_collector_count>p_max_pairs THEN
  RAISE EXCEPTION 'collection run fleet check exceeds pair bound'
   USING ERRCODE='54000';
 END IF;
 FOR v_instance_id,v_collector_id IN
  SELECT target.instance_id,catalog.collector_id
  FROM control.observation_target AS target
  CROSS JOIN (SELECT DISTINCT contract.collector_id
              FROM control.collector_contract AS contract) AS catalog
  ORDER BY target.instance_id,catalog.collector_id
 LOOP
  PERFORM set_config('sqlobserver.target_scope',v_instance_id::text,true);
  SELECT * INTO v_coverage
  FROM control.collection_run_v2_parity_coverage(
   v_instance_id,v_collector_id);
  IF NOT coalesce(v_coverage.all_covered,false) THEN
   RAISE EXCEPTION 'collection run parity incomplete for target %, collector %',
    v_instance_id,v_collector_id USING ERRCODE='55000';
  END IF;
  v_pairs:=v_pairs+1;
 END LOOP;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_prior_scope,''),true);
 RETURN QUERY SELECT v_target_count,v_collector_count,v_pairs;
END $fence$;
REVOKE ALL ON FUNCTION control.verify_collection_run_v2_fleet_fenced(integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.verify_collection_run_v2_fleet_fenced(integer)
 TO sqlobserver_migrator;
