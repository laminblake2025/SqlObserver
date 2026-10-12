-- Replace all reviewed child references atomically after bridge validation.
-- The original collection-run table remains the writer and read source.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_identity_fk_cutover (
 only_row boolean PRIMARY KEY DEFAULT true CHECK(only_row),
 completed_at timestamptz NOT NULL,
 removed_constraints integer NOT NULL CHECK(removed_constraints=20),
 initiated_by name NOT NULL
);
ALTER TABLE system.collection_run_identity_fk_cutover
 ENABLE ROW LEVEL SECURITY;
ALTER TABLE system.collection_run_identity_fk_cutover
 FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_identity_fk_cutover_migrator
 ON system.collection_run_identity_fk_cutover FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON system.collection_run_identity_fk_cutover
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_identity_fk_cutover_append_only
 BEFORE UPDATE OR DELETE ON system.collection_run_identity_fk_cutover
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION control.cutover_collection_run_identity_fks(
 p_max_pairs integer DEFAULT 10000)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC' SET lock_timeout='5s'
AS $cutover$
DECLARE
 v_target record;
 v_old_name name;
 v_old_count integer;
 v_new_oid oid;
 v_cutover_recorded boolean;
 v_removed integer:=0;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run FK cutover requires migrator'
   USING ERRCODE='42501';
 END IF;
 -- Serialize cutover attempts before taking the fleet's SHARE locks.
 PERFORM pg_advisory_xact_lock(hashtextextended(
  'sqlobserver:collection-run-identity-fk-cutover',0));
 PERFORM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);

 -- Lock all 20 parent relations in stable order before inspecting catalog
 -- state. The subsequent DDL recursively handles their partitions.
 FOR v_target IN
  SELECT reviewed.child_relation,reviewed.column_name,
         namespace.nspname AS schema_name,relation.relname AS table_name
  FROM control.collection_run_identity_fk_targets() AS reviewed
  JOIN pg_class AS relation ON relation.oid=reviewed.child_relation
  JOIN pg_namespace AS namespace ON namespace.oid=relation.relnamespace
  ORDER BY reviewed.child_relation::oid
 LOOP
  EXECUTE format('LOCK TABLE %I.%I IN ACCESS EXCLUSIVE MODE',
   v_target.schema_name,v_target.table_name);
 END LOOP;
 IF (SELECT count(*) FROM control.collection_run_identity_fk_targets())<>20 THEN
  RAISE EXCEPTION 'collection run reviewed FK inventory differs'
   USING ERRCODE='55000';
 END IF;
 SELECT count(*) INTO v_old_count
 FROM pg_constraint AS fk
 WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.confrelid='telemetry.collection_run'::regclass;
 SELECT EXISTS(SELECT 1 FROM system.collection_run_identity_fk_cutover)
 INTO v_cutover_recorded;
 IF NOT ((v_old_count=20 AND NOT v_cutover_recorded)
         OR (v_old_count=0 AND v_cutover_recorded)) THEN
  RAISE EXCEPTION 'collection run original FK graph is partial or unrecorded'
   USING ERRCODE='55000';
 END IF;

 FOR v_target IN
  SELECT reviewed.child_relation,reviewed.column_name,
         namespace.nspname AS schema_name,relation.relname AS table_name
  FROM control.collection_run_identity_fk_targets() AS reviewed
  JOIN pg_class AS relation ON relation.oid=reviewed.child_relation
  JOIN pg_namespace AS namespace ON namespace.oid=relation.relnamespace
  ORDER BY reviewed.child_relation::oid
 LOOP
  SELECT old_fk.conname INTO v_old_name
  FROM pg_constraint AS old_fk
  JOIN pg_attribute AS child_column
   ON child_column.attrelid=old_fk.conrelid
  AND child_column.attnum=old_fk.conkey[1]
  JOIN pg_attribute AS source_column
   ON source_column.attrelid=old_fk.confrelid
  AND source_column.attnum=old_fk.confkey[1]
  WHERE old_fk.contype='f' AND old_fk.conparentid=0
    AND old_fk.conrelid=v_target.child_relation
    AND old_fk.confrelid='telemetry.collection_run'::regclass
    AND array_length(old_fk.conkey,1)=1
    AND array_length(old_fk.confkey,1)=1
    AND child_column.attname=v_target.column_name
    AND source_column.attname='run_id'
    AND old_fk.convalidated AND NOT old_fk.condeferrable
    AND old_fk.confupdtype='a' AND old_fk.confdeltype='a'
    AND old_fk.confmatchtype='s';
  IF (v_old_count=20 AND v_old_name IS NULL)
     OR (v_old_count=0 AND v_old_name IS NOT NULL) THEN
   RAISE EXCEPTION 'collection run original FK differs on %',
    v_target.child_relation USING ERRCODE='55000';
  END IF;
  SELECT new_fk.oid INTO v_new_oid
  FROM pg_constraint AS new_fk
  JOIN pg_attribute AS child_column
   ON child_column.attrelid=new_fk.conrelid
  AND child_column.attnum=new_fk.conkey[1]
  JOIN pg_attribute AS identity_column
   ON identity_column.attrelid=new_fk.confrelid
  AND identity_column.attnum=new_fk.confkey[1]
  WHERE new_fk.contype='f' AND new_fk.conparentid=0
    AND new_fk.conrelid=v_target.child_relation
    AND new_fk.conname='fk_collection_run_identity'
    AND new_fk.confrelid='telemetry.collection_run_identity'::regclass
    AND array_length(new_fk.conkey,1)=1
    AND array_length(new_fk.confkey,1)=1
    AND child_column.attname=v_target.column_name
    AND identity_column.attname='run_id'
    AND new_fk.convalidated AND NOT new_fk.condeferrable
    AND new_fk.confupdtype='a' AND new_fk.confdeltype='a'
    AND new_fk.confmatchtype='s';
  IF v_new_oid IS NULL OR EXISTS (
   SELECT 1 FROM pg_constraint AS child_fk
   WHERE child_fk.conparentid=v_new_oid AND NOT child_fk.convalidated
  ) OR EXISTS (
   SELECT 1 FROM pg_trigger AS fk_trigger
   JOIN pg_constraint AS trigger_constraint
    ON trigger_constraint.oid=fk_trigger.tgconstraint
   WHERE (trigger_constraint.oid=v_new_oid
      OR trigger_constraint.conparentid=v_new_oid)
     AND fk_trigger.tgenabled NOT IN ('O','A')
  ) THEN
   RAISE EXCEPTION 'collection run identity FK is not valid on %',
    v_target.child_relation USING ERRCODE='55000';
  END IF;
 END LOOP;
 IF v_old_count=0 THEN
  RETURN 0;
 END IF;

 FOR v_target IN
  SELECT reviewed.child_relation,reviewed.column_name,
         namespace.nspname AS schema_name,relation.relname AS table_name
  FROM control.collection_run_identity_fk_targets() AS reviewed
  JOIN pg_class AS relation ON relation.oid=reviewed.child_relation
  JOIN pg_namespace AS namespace ON namespace.oid=relation.relnamespace
  ORDER BY reviewed.child_relation::oid
 LOOP
  SELECT fk.conname INTO v_old_name
  FROM pg_constraint AS fk
  JOIN pg_attribute AS column_row
   ON column_row.attrelid=fk.conrelid
  AND column_row.attnum=fk.conkey[1]
  WHERE fk.contype='f' AND fk.conparentid=0
    AND fk.conrelid=v_target.child_relation
    AND fk.confrelid='telemetry.collection_run'::regclass
    AND column_row.attname=v_target.column_name;
  EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I',
   v_target.schema_name,v_target.table_name,v_old_name);
  v_removed:=v_removed+1;
 END LOOP;
 IF v_removed<>20 OR EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  WHERE fk.contype='f'
    AND fk.confrelid='telemetry.collection_run'::regclass
 ) THEN
  RAISE EXCEPTION 'collection run original FK graph remains after cutover'
   USING ERRCODE='55000';
 END IF;
 INSERT INTO system.collection_run_identity_fk_cutover
  (completed_at,removed_constraints,initiated_by)
 VALUES(clock_timestamp(),v_removed,session_user);
 RETURN v_removed;
END $cutover$;
REVOKE ALL ON FUNCTION control.cutover_collection_run_identity_fks(integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.cutover_collection_run_identity_fks(integer)
 TO sqlobserver_migrator;
