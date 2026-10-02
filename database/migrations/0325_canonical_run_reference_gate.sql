-- Inventory canonical run dependencies before enabling root retirement.
-- Read-only, migrator-only and target/run bounded; no policy or delete path is activated.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION system.collection_run_reviewed_references()
RETURNS TABLE(child_relation regclass,column_name name,parent_relation regclass,delete_action "char")
LANGUAGE sql STABLE PARALLEL SAFE SET search_path=pg_catalog
AS $inventory$
 VALUES
 ('control.collector_schedule'::regclass,'active_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.blocking_edge'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.deadlock_summary'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.query_performance_run'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.activity_request_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.activity_session_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_group_database_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_group_replica_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.backup_status_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.collection_run_outcome'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.database_file_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.database_inventory_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.raw_metric_sample'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.server_wait_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure_occurrence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure_scan_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_volume_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.tempdb_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.visibility_gap'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.server_configuration_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.backup_v2_scan'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_replica_evidence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_database_replica_evidence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('alerting.deadlock_source_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'c'::"char"),
 ('control.deadlock_read_checkpoint'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'c'::"char"),
 ('events.plan_cache_runtime_watermark'::regclass,'last_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_runtime_delta'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_runtime_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_content_link'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_content_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.deadlock_read_receipt'::regclass,'run_id'::name,'telemetry.collection_run_outcome'::regclass,'a'::"char");
$inventory$;
REVOKE ALL ON FUNCTION system.collection_run_reviewed_references()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- A separate non-login reader can see exact run references through FORCE RLS,
-- including malformed cross-target references. It receives SELECT only; callers
-- cannot assume this role or choose an arbitrary relation for it to read.
RESET ROLE;
DO $reader_role$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='sqlobserver_run_dependency_reader') THEN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolsuper) THEN
   RAISE EXCEPTION 'run dependency reader requires cluster administrator provisioning' USING ERRCODE='42501';
  END IF;
  CREATE ROLE sqlobserver_run_dependency_reader WITH NOLOGIN NOSUPERUSER NOCREATEDB
   NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='sqlobserver_run_dependency_reader'
   AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
   AND NOT rolinherit AND NOT rolreplication AND rolbypassrls)
   OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member='sqlobserver_run_dependency_reader'::regrole)
   OR EXISTS(SELECT 1 FROM pg_auth_members WHERE roleid='sqlobserver_run_dependency_reader'::regrole
      AND member<>current_user::regrole) THEN
  RAISE EXCEPTION 'run dependency reader has unsafe attributes or memberships' USING ERRCODE='55000';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE roleid='sqlobserver_run_dependency_reader'::regrole
   AND member=current_user::regrole AND admin_option) THEN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolsuper) THEN
   RAISE EXCEPTION 'run dependency reader requires temporary bootstrap membership' USING ERRCODE='42501';
  END IF;
  EXECUTE format('GRANT sqlobserver_run_dependency_reader TO %I WITH ADMIN OPTION',current_user);
 END IF;
END $reader_role$;
GRANT sqlobserver_run_dependency_reader TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;
GRANT USAGE ON SCHEMA control,telemetry,events,alerting,system TO sqlobserver_run_dependency_reader;
GRANT SELECT ON
 control.collector_schedule,
 control.deadlock_read_checkpoint,
 events.blocking_edge,
 alerting.deadlock_source_snapshot,
 events.deadlock_read_receipt,
 events.deadlock_summary,
 events.plan_cache_content_link,
 events.plan_cache_content_receipt,
 events.plan_cache_runtime_delta,
 events.plan_cache_runtime_receipt,
 events.plan_cache_runtime_watermark,
 events.query_performance_run,
 system.partition_registry,
 telemetry.activity_request_snapshot,
 telemetry.activity_session_snapshot,
 telemetry.availability_database_replica_evidence,
 telemetry.availability_group_database_snapshot,
 telemetry.availability_group_replica_snapshot,
 telemetry.availability_replica_evidence,
 telemetry.backup_status_snapshot,
 telemetry.backup_v2_scan,
 telemetry.collection_run,
 telemetry.collection_run_identity,
 telemetry.collection_run_outcome,
 telemetry.collection_run_v2,
 telemetry.database_file_snapshot,
 telemetry.database_inventory_snapshot,
 telemetry.raw_metric_sample,
 telemetry.server_configuration_snapshot,
 telemetry.server_wait_snapshot,
 telemetry.sql_agent_failure,
 telemetry.sql_agent_failure_occurrence,
 telemetry.sql_agent_failure_scan_snapshot,
 telemetry.sql_volume_snapshot,
 telemetry.tempdb_snapshot,
 telemetry.visibility_gap
 TO sqlobserver_run_dependency_reader;
GRANT EXECUTE ON FUNCTION system.collection_run_reviewed_references() TO sqlobserver_run_dependency_reader;
-- Detached partitions are independent relations. Grant only already-reviewed
-- relations, never tables selected by an untrusted caller or unknown registry key.
DO $detached_grants$
DECLARE part record;
BEGIN
 FOR part IN
  SELECT registry.partition_schema,registry.partition_name
  FROM system.partition_registry registry
  JOIN system.collection_run_reviewed_references() reviewed
    ON reviewed.child_relation=to_regclass(format('%I.%I',registry.parent_schema,registry.parent_table))
  JOIN pg_class relation ON relation.oid=to_regclass(format('%I.%I',registry.partition_schema,registry.partition_name))
  WHERE registry.lifecycle_state='detached' AND relation.relkind='r'
    AND relation.relowner='sqlobserver_migrator'::regrole
    AND relation.relrowsecurity AND relation.relforcerowsecurity
    AND registry.partition_schema=registry.parent_schema
 LOOP
  EXECUTE format('GRANT SELECT ON %I.%I TO sqlobserver_run_dependency_reader',part.partition_schema,part.partition_name);
 END LOOP;
END $detached_grants$;
GRANT CREATE ON SCHEMA system TO sqlobserver_run_dependency_reader;
SET LOCAL ROLE sqlobserver_run_dependency_reader;

CREATE FUNCTION system.collection_run_reference_holds(p_instance_id uuid,p_run_id uuid)
RETURNS TABLE(hold_code text,relation_name text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
SET statement_timeout='15s' SET lock_timeout='5s'
AS $references$
DECLARE
 v_fk record; v_expected record; v_relation oid; v_column name; v_present boolean;
 v_holds integer:=0; v_seen oid[]:=ARRAY[]::oid[]; v_registry record;
 v_run_key smallint; v_time_key smallint; v_identity_key smallint; v_identity_time_key smallint;
BEGIN
 IF p_instance_id IS NULL OR p_run_id IS NULL
   OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
   OR NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'run reference preview requires migrator and exact target scope' USING ERRCODE='42501';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM telemetry.collection_run_identity identity
   JOIN telemetry.collection_run_v2 run ON run.run_id=identity.run_id AND run.started_at=identity.started_at
   WHERE identity.run_id=p_run_id AND run.instance_id=p_instance_id) THEN
  hold_code:='run_missing'; relation_name:=NULL; RETURN NEXT; RETURN;
 END IF;
 IF EXISTS(SELECT 1 FROM telemetry.collection_run original WHERE original.run_id=p_run_id) THEN
  hold_code:='original_copy'; relation_name:='telemetry.collection_run'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 SELECT attnum INTO v_run_key FROM pg_attribute WHERE attrelid='telemetry.collection_run_v2'::regclass AND attname='run_id';
 SELECT attnum INTO v_time_key FROM pg_attribute WHERE attrelid='telemetry.collection_run_v2'::regclass AND attname='started_at';
 SELECT attnum INTO v_identity_key FROM pg_attribute WHERE attrelid='telemetry.collection_run_identity'::regclass AND attname='run_id';
 SELECT attnum INTO v_identity_time_key FROM pg_attribute WHERE attrelid='telemetry.collection_run_identity'::regclass AND attname='started_at';
 IF NOT EXISTS(SELECT 1 FROM pg_constraint fk WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.conrelid='telemetry.collection_run_identity'::regclass AND fk.confrelid='telemetry.collection_run_v2'::regclass
   AND fk.conkey=ARRAY[v_identity_key,v_identity_time_key] AND fk.confkey=ARRAY[v_run_key,v_time_key]
   AND fk.convalidated AND NOT fk.condeferrable AND fk.confdeltype='a' AND fk.confupdtype='a' AND fk.confmatchtype='s') THEN
  hold_code:='identity_bridge_unverified'; relation_name:=NULL; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 -- Top-level constraints cover attached children through their parent query.
 -- Detached children have their own top-level constraint and need a direct probe.
 IF (SELECT count(*) FROM pg_constraint fk WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.confrelid IN ('telemetry.collection_run'::regclass,'telemetry.collection_run_identity'::regclass,
     'telemetry.collection_run_v2'::regclass,'telemetry.collection_run_outcome'::regclass,'telemetry.visibility_gap'::regclass))>128 THEN
  hold_code:='reference_scan_bound'; relation_name:=NULL; RETURN NEXT; RETURN;
 END IF;
 FOR v_fk IN
  SELECT fk.*,child.relkind,child.relowner,child.relrowsecurity,child.relforcerowsecurity,
    namespace.nspname,child.relname,attribute.attname,attribute.atttypid
  FROM pg_constraint fk JOIN pg_class child ON child.oid=fk.conrelid
  JOIN pg_namespace namespace ON namespace.oid=child.relnamespace
  LEFT JOIN pg_attribute attribute ON attribute.attrelid=fk.conrelid AND attribute.attnum=fk.conkey[1]
  WHERE fk.contype='f' AND fk.conparentid=0 AND fk.confrelid IN
    ('telemetry.collection_run'::regclass,'telemetry.collection_run_identity'::regclass,
     'telemetry.collection_run_v2'::regclass,'telemetry.collection_run_outcome'::regclass,'telemetry.visibility_gap'::regclass)
  ORDER BY fk.conrelid,fk.oid
 LOOP
  IF v_fk.conrelid='telemetry.collection_run_identity'::regclass
    AND v_fk.confrelid='telemetry.collection_run_v2'::regclass
    AND v_fk.conkey=ARRAY[v_identity_key,v_identity_time_key]
    AND v_fk.confkey=ARRAY[v_run_key,v_time_key]
    AND v_fk.convalidated AND NOT v_fk.condeferrable
    AND v_fk.confdeltype='a' AND v_fk.confupdtype='a'
    AND v_fk.confmatchtype='s' THEN CONTINUE; END IF;
  SELECT * INTO v_expected FROM system.collection_run_reviewed_references() reviewed
   WHERE reviewed.child_relation=v_fk.conrelid;
  IF NOT FOUND THEN
   SELECT registry.* INTO v_registry FROM system.partition_registry registry
    WHERE registry.lifecycle_state='detached'
      AND registry.partition_schema=v_fk.nspname AND registry.partition_name=v_fk.relname;
   IF FOUND AND v_registry.partition_schema=v_registry.parent_schema
      AND NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=v_fk.conrelid) THEN
    SELECT * INTO v_expected FROM system.collection_run_reviewed_references() reviewed
     WHERE reviewed.child_relation=to_regclass(format('%I.%I',v_registry.parent_schema,v_registry.parent_table));
   END IF;
  END IF;
  IF v_expected.child_relation IS NULL THEN
   hold_code:='unreviewed_foreign_key'; relation_name:=NULL; RETURN NEXT; v_holds:=v_holds+1; CONTINUE;
  END IF;
  IF cardinality(v_fk.conkey)<>1 OR cardinality(v_fk.confkey)<>1
     OR v_fk.attname IS DISTINCT FROM v_expected.column_name OR v_fk.atttypid<>'uuid'::regtype
     OR v_fk.confrelid<>v_expected.parent_relation OR NOT v_fk.convalidated OR v_fk.condeferrable
     OR v_fk.confdeltype<>v_expected.delete_action OR v_fk.confupdtype<>'a' OR v_fk.confmatchtype<>'s'
     OR NOT EXISTS(SELECT 1 FROM pg_attribute parent_column WHERE parent_column.attrelid=v_fk.confrelid
       AND parent_column.attnum=v_fk.confkey[1] AND parent_column.attname='run_id')
     OR v_fk.relowner<>'sqlobserver_migrator'::regrole OR v_fk.relkind NOT IN ('r','p')
     -- The original M2 raw-metric parent is ACL-isolated, not RLS enabled.
     -- Pin that reviewed exception rather than silently changing its ingestion contract.
     OR (v_expected.child_relation='telemetry.raw_metric_sample'::regclass
         AND (v_fk.relrowsecurity OR v_fk.relforcerowsecurity))
     OR (v_expected.child_relation<>'telemetry.raw_metric_sample'::regclass
         AND (NOT v_fk.relrowsecurity OR NOT v_fk.relforcerowsecurity)) THEN
   hold_code:='reference_catalog_unverified'; relation_name:=v_expected.child_relation::text;
   RETURN NEXT; v_holds:=v_holds+1; CONTINUE;
  END IF;
  IF v_fk.conrelid=v_expected.child_relation THEN v_seen:=array_append(v_seen,v_fk.conrelid); END IF;
  -- Outcome and visibility-gap envelopes belong to the future atomic cleanup
  -- unit; their external references (including outcome receipts) still hold it.
  IF v_fk.conrelid IN ('telemetry.collection_run_outcome'::regclass,'telemetry.visibility_gap'::regclass) THEN CONTINUE; END IF;
  IF NOT EXISTS(SELECT 1 FROM pg_index index_row
     JOIN pg_class index_class ON index_class.oid=index_row.indexrelid
     JOIN pg_am method ON method.oid=index_class.relam
     WHERE index_row.indrelid=v_fk.conrelid AND index_row.indisvalid AND index_row.indisready
       AND index_row.indpred IS NULL AND index_row.indexprs IS NULL
       AND index_row.indkey[0]=v_fk.conkey[1] AND method.amname='btree') THEN
   hold_code:='reference_index_unverified'; relation_name:=v_expected.child_relation::text;
   RETURN NEXT; v_holds:=v_holds+1; CONTINUE;
  END IF;
  IF NOT has_table_privilege(current_user,v_fk.conrelid,'SELECT') THEN
   hold_code:='reference_visibility_unverified'; relation_name:=v_expected.child_relation::text;
   RETURN NEXT; v_holds:=v_holds+1; CONTINUE;
  END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I WHERE %I=$1)',v_fk.nspname,v_fk.relname,v_fk.attname)
   INTO v_present USING p_run_id;
  IF v_present THEN
   hold_code:=CASE WHEN v_fk.conrelid=v_expected.child_relation THEN 'retained_reference' ELSE 'detached_reference' END;
   relation_name:=v_expected.child_relation::text; RETURN NEXT; v_holds:=v_holds+1;
  END IF;
 END LOOP;
 FOR v_expected IN SELECT * FROM system.collection_run_reviewed_references() LOOP
  IF NOT v_expected.child_relation::oid=ANY(v_seen) THEN
   hold_code:='reference_catalog_unverified'; relation_name:=v_expected.child_relation::text;
   RETURN NEXT; v_holds:=v_holds+1;
  END IF;
 END LOOP;
 IF v_holds=0 THEN hold_code:='no_known_reference'; relation_name:=NULL; RETURN NEXT; END IF;
END $references$;
REVOKE ALL ON FUNCTION system.collection_run_reference_holds(uuid,uuid)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.collection_run_reference_holds(uuid,uuid) TO sqlobserver_migrator;

SET LOCAL ROLE sqlobserver_migrator;
REVOKE CREATE ON SCHEMA system FROM sqlobserver_run_dependency_reader;
RESET ROLE;
REVOKE sqlobserver_run_dependency_reader FROM sqlobserver_migrator;
REVOKE sqlobserver_run_dependency_reader FROM CURRENT_USER;
