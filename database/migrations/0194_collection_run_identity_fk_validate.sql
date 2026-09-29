-- Validate historical children one reviewed relation at a time. The staged
-- FK already enforces new writes; this scan must not hold the fleet fence.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.collection_run_identity_fk_targets()
RETURNS TABLE(child_relation regclass,column_name name)
LANGUAGE sql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,telemetry,system
AS $targets$
 SELECT * FROM (VALUES
  ('control.collector_schedule'::regclass,'active_run_id'::name),
  ('events.blocking_edge'::regclass,'collection_run_id'::name),
  ('events.deadlock_summary'::regclass,'collection_run_id'::name),
  ('events.query_performance_run'::regclass,'collection_run_id'::name),
  ('telemetry.activity_request_snapshot'::regclass,'collection_run_id'::name),
  ('telemetry.activity_session_snapshot'::regclass,'collection_run_id'::name),
  ('telemetry.availability_group_database_snapshot'::regclass,'run_id'::name),
  ('telemetry.availability_group_replica_snapshot'::regclass,'run_id'::name),
  ('telemetry.backup_status_snapshot'::regclass,'run_id'::name),
  ('telemetry.collection_run_outcome'::regclass,'run_id'::name),
  ('telemetry.database_file_snapshot'::regclass,'collection_run_id'::name),
  ('telemetry.database_inventory_snapshot'::regclass,'collection_run_id'::name),
  ('telemetry.raw_metric_sample'::regclass,'collection_run_id'::name),
  ('telemetry.server_wait_snapshot'::regclass,'collection_run_id'::name),
  ('telemetry.sql_agent_failure'::regclass,'run_id'::name),
  ('telemetry.sql_agent_failure_occurrence'::regclass,'run_id'::name),
  ('telemetry.sql_agent_failure_scan_snapshot'::regclass,'run_id'::name),
  ('telemetry.sql_volume_snapshot'::regclass,'run_id'::name),
  ('telemetry.tempdb_snapshot'::regclass,'run_id'::name),
  ('telemetry.visibility_gap'::regclass,'run_id'::name)
 ) AS reviewed(child_relation,column_name);
$targets$;
REVOKE ALL ON FUNCTION control.collection_run_identity_fk_targets()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.collection_run_identity_fk_targets()
 TO sqlobserver_migrator;

CREATE FUNCTION control.validate_collection_run_identity_fk(p_child regclass)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC' SET lock_timeout='5s'
AS $validate$
DECLARE
 v_column name;
 v_schema name;
 v_table name;
 v_old_count integer;
 v_new_oid oid;
 v_validated boolean;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run FK validation requires migrator'
   USING ERRCODE='42501';
 END IF;
 SELECT target.column_name INTO v_column
 FROM control.collection_run_identity_fk_targets() AS target
 WHERE target.child_relation=p_child;
 IF v_column IS NULL THEN
  RAISE EXCEPTION 'collection run FK child is not in reviewed inventory'
   USING ERRCODE='22023';
 END IF;
 SELECT namespace.nspname,relation.relname INTO v_schema,v_table
 FROM pg_class AS relation
 JOIN pg_namespace AS namespace ON namespace.oid=relation.relnamespace
 WHERE relation.oid=p_child AND relation.relkind IN ('r','p');
 IF v_schema IS NULL THEN
  RAISE EXCEPTION 'collection run FK child relation is unavailable'
   USING ERRCODE='55000';
 END IF;
 -- SHARE UPDATE EXCLUSIVE permits ordinary inserts while validation scans.
 EXECUTE format('LOCK TABLE %I.%I IN SHARE UPDATE EXCLUSIVE MODE',
  v_schema,v_table);
 SELECT count(*) INTO v_old_count
 FROM pg_constraint AS fk
 JOIN pg_attribute AS child_column
  ON child_column.attrelid=fk.conrelid
 AND child_column.attnum=fk.conkey[1]
 WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.conrelid=p_child
   AND fk.confrelid='telemetry.collection_run'::regclass
   AND array_length(fk.conkey,1)=1
   AND child_column.attname=v_column AND fk.convalidated;
 SELECT fk.oid,fk.convalidated INTO v_new_oid,v_validated
 FROM pg_constraint AS fk
 JOIN pg_attribute AS child_column
  ON child_column.attrelid=fk.conrelid
 AND child_column.attnum=fk.conkey[1]
 JOIN pg_attribute AS identity_column
  ON identity_column.attrelid=fk.confrelid
 AND identity_column.attnum=fk.confkey[1]
 WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.conrelid=p_child AND fk.conname='fk_collection_run_identity'
   AND fk.confrelid='telemetry.collection_run_identity'::regclass
   AND array_length(fk.conkey,1)=1 AND array_length(fk.confkey,1)=1
   AND child_column.attname=v_column
   AND identity_column.attname='run_id'
   AND NOT fk.condeferrable AND fk.confupdtype='a'
   AND fk.confdeltype='a' AND fk.confmatchtype='s';
 IF v_old_count<>1 OR v_new_oid IS NULL THEN
  RAISE EXCEPTION 'collection run staged or original FK differs'
   USING ERRCODE='55000';
 END IF;
 IF NOT v_validated THEN
  EXECUTE format('ALTER TABLE %I.%I VALIDATE CONSTRAINT fk_collection_run_identity',
   v_schema,v_table);
 END IF;
 IF NOT EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  WHERE fk.oid=v_new_oid AND fk.convalidated
 ) OR EXISTS (
  SELECT 1 FROM pg_constraint AS child_fk
  WHERE child_fk.conparentid=v_new_oid AND NOT child_fk.convalidated
 ) THEN
  RAISE EXCEPTION 'collection run identity FK validation incomplete'
   USING ERRCODE='55000';
 END IF;
 RETURN NOT v_validated;
END $validate$;
REVOKE ALL ON FUNCTION control.validate_collection_run_identity_fk(regclass)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.validate_collection_run_identity_fk(regclass)
 TO sqlobserver_migrator;
