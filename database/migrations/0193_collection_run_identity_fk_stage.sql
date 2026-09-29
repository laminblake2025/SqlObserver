-- Stage one reviewed child FK after a writer-fenced fleet proof. NOT VALID
-- enforces future writes without scanning historical children. Original FKs
-- remain until separate validation and cutover steps.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.stage_collection_run_identity_fk(
 p_child regclass,p_max_pairs integer DEFAULT 10000)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC'
AS $stage$
DECLARE
 v_column name;
 v_schema name;
 v_table name;
 v_old_count integer;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run FK staging requires migrator'
   USING ERRCODE='42501';
 END IF;
 SELECT reviewed.column_name INTO v_column
 FROM (VALUES
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
 ) AS reviewed(child_relation,column_name)
 WHERE reviewed.child_relation=p_child;
 IF v_column IS NULL THEN
  RAISE EXCEPTION 'collection run FK child is not in reviewed inventory'
   USING ERRCODE='22023';
 END IF;

 -- The proof locks run writers, target/collector registration, bridge state,
 -- partitions and parity records through this caller transaction.
 PERFORM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 SELECT namespace.nspname,relation.relname INTO v_schema,v_table
 FROM pg_class AS relation
 JOIN pg_namespace AS namespace ON namespace.oid=relation.relnamespace
 WHERE relation.oid=p_child AND relation.relkind IN ('r','p');
 IF v_schema IS NULL THEN
  RAISE EXCEPTION 'collection run FK child relation is unavailable'
   USING ERRCODE='55000';
 END IF;
 EXECUTE format('LOCK TABLE %I.%I IN SHARE ROW EXCLUSIVE MODE',
  v_schema,v_table);
 SELECT count(*) INTO v_old_count
 FROM pg_constraint AS fk
 JOIN pg_attribute AS child_column
  ON child_column.attrelid=fk.conrelid
 AND child_column.attnum=fk.conkey[1]
 JOIN pg_attribute AS source_column
  ON source_column.attrelid=fk.confrelid
 AND source_column.attnum=fk.confkey[1]
 WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.conrelid=p_child
   AND fk.confrelid='telemetry.collection_run'::regclass
   AND array_length(fk.conkey,1)=1 AND array_length(fk.confkey,1)=1
   AND child_column.attname=v_column
   AND source_column.attname='run_id'
   AND fk.convalidated AND NOT fk.condeferrable
   AND fk.confupdtype='a' AND fk.confdeltype='a'
   AND fk.confmatchtype='s';
 IF v_old_count<>1 OR EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  WHERE fk.conrelid=p_child AND fk.conname='fk_collection_run_identity'
 ) THEN
  RAISE EXCEPTION 'collection run original FK differs or identity FK exists'
   USING ERRCODE='55000';
 END IF;
 EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT fk_collection_run_identity FOREIGN KEY (%I) REFERENCES telemetry.collection_run_identity(run_id) NOT VALID',
  v_schema,v_table,v_column);
 RETURN true;
END $stage$;
REVOKE ALL ON FUNCTION control.stage_collection_run_identity_fk(regclass,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.stage_collection_run_identity_fk(regclass,integer)
 TO sqlobserver_migrator;
