-- Bounded, migrator-only canonical retirement. Default policy stays disabled.
-- One ledger/audit per batch prevents replacing run history with per-run audit growth.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy','collection_run_canonical',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry' AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events' AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events' AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR (data_class='m7_query_performance' AND parent_schema='events' AND parent_table='query_performance_observation_v3' AND partition_granularity='day')
   OR (data_class='collection_run_canonical' AND parent_schema='telemetry' AND parent_table='collection_run_identity' AND partition_granularity='none')
   OR (data_class='collection_run_original_copy' AND parent_schema='telemetry' AND parent_table='collection_run' AND partition_granularity='none')
   OR (data_class='m9_ag_replica_evidence' AND parent_schema='telemetry' AND parent_table='availability_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_ag_database_evidence' AND parent_schema='telemetry' AND parent_table='availability_database_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_backup_evidence' AND parent_schema='telemetry' AND parent_table='backup_v2_scan' AND partition_granularity='day')
   OR (data_class='sql_metric_baselines' AND parent_schema='analytics' AND parent_table='sql_metric_baseline' AND partition_granularity='day')
   OR (data_class='m6_deadlock_read_receipts' AND parent_schema='events' AND parent_table='deadlock_read_receipt' AND partition_granularity='month')
   OR (data_class='m7_cache_watermarks' AND parent_schema='events' AND parent_table='plan_cache_runtime_watermark' AND partition_granularity='day')
   OR (data_class='m7_cache_deltas' AND parent_schema='events' AND parent_table='plan_cache_runtime_delta' AND partition_granularity='day')
   OR (data_class='m7_cache_receipts' AND parent_schema='events' AND parent_table='plan_cache_runtime_receipt' AND partition_granularity='day')
   OR (data_class='m7_cache_content_receipts' AND parent_schema='events' AND parent_table='plan_cache_content_receipt' AND partition_granularity='day')
   OR (data_class='m7_cache_content_links' AND parent_schema='events' AND parent_table='plan_cache_content_link' AND partition_granularity='day')
   OR data_class IN ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking','m6_deadlocks',
     'm8_alert_history','m8_alert_delivery','m9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases','m10_host_metrics','m10_replication','m10_rollups','m10_evidence')));


CREATE FUNCTION system.guard_canonical_run_retention_policy()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $policy_guard$
BEGIN
 IF NEW.data_class='collection_run_canonical' AND
   (NEW.parent_schema<>'telemetry' OR NEW.parent_table<>'collection_run_identity'
    OR NEW.partition_granularity<>'none' OR NEW.minimum_partitions_to_keep<>1
    OR (NEW.retain_for IS NOT NULL AND (NEW.retain_for<interval '90 days' OR NEW.retain_for>interval '3650 days'))
    OR (NEW.enabled AND (NEW.retain_for IS NULL OR NOT EXISTS
      (SELECT 1 FROM system.collection_run_original_rollback_seal seal WHERE seal.singleton AND seal.sealed_at IS NOT NULL)))) THEN
  RAISE EXCEPTION 'canonical retention requires sealed writer and 90-day minimum' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $policy_guard$;
REVOKE ALL ON FUNCTION system.guard_canonical_run_retention_policy() FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER guard_canonical_run_retention_policy BEFORE INSERT OR UPDATE ON system.retention_policy
 FOR EACH ROW EXECUTE FUNCTION system.guard_canonical_run_retention_policy();
INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('collection_run_canonical','telemetry','collection_run_identity','none',false,NULL,1,'migration-0347');

CREATE OR REPLACE FUNCTION system.update_m10_retention_policy
 (p_data_class text,p_enabled boolean,p_retain_for interval,p_minimum_partitions integer,
  p_expected_revision bigint,p_changed_by text,p_change_reason text)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system
AS $policy_update$
DECLARE next_revision bigint;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' THEN
  RAISE EXCEPTION 'global retention mutation requires SecurityAdministrator authorization context'
   USING ERRCODE='42501';
 END IF;
 IF p_data_class IS NULL OR p_data_class NOT IN
    ('raw_metric_samples','diagnostic_events',
     'm5_activity','m5_requests','m5_waits','m5_blocking',
     'm9_backup_status','m9_agent_history','m9_agent_occurrences',
     'm9_ag_replicas','m9_ag_databases','m9_tempdb',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence',
     'sql_volume_capacity','m7_group_watermarks','m7_group_deltas',
     'm7_query_performance','collection_run_original_copy','collection_run_canonical',
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
    OR (p_data_class='m6_deadlock_read_receipts' AND (p_minimum_partitions<2 OR (p_enabled AND p_retain_for<interval '30 days')))
    OR (p_data_class='m7_cache_watermarks' AND p_enabled AND p_retain_for<interval '8 days')
    OR (p_data_class IN ('m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links') AND p_enabled AND p_retain_for<interval '30 days')
    OR (p_data_class='collection_run_canonical' AND (p_minimum_partitions<>1
        OR (p_retain_for IS NOT NULL AND (p_retain_for<interval '90 days' OR p_retain_for>interval '3650 days'))))
    OR (p_data_class='collection_run_original_copy'
        AND (p_minimum_partitions<>1 OR (p_enabled
          AND p_retain_for<interval '30 days'))) THEN
  RAISE EXCEPTION 'retention policy bounds rejected' USING ERRCODE='22023';
 END IF;
 PERFORM set_config('sqlobserver.retention_change_reason',p_change_reason,true);
 PERFORM set_config('sqlobserver.retention_changed_by',p_changed_by,true);
 UPDATE system.retention_policy
 SET enabled=p_enabled,retain_for=p_retain_for,
     minimum_partitions_to_keep=p_minimum_partitions,
     policy_revision=policy_revision+1,updated_by=p_changed_by,
     updated_at=clock_timestamp()
 WHERE data_class=p_data_class AND policy_revision=p_expected_revision
 RETURNING policy_revision INTO next_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'retention policy revision conflict' USING ERRCODE='40001';
 END IF;
 RETURN next_revision;
END $policy_update$;

CREATE TABLE system.collection_run_retirement
(
 operation_id uuid PRIMARY KEY,instance_id uuid NOT NULL,request_digest bytea NOT NULL CHECK(octet_length(request_digest)=32),
 cutoff timestamptz NOT NULL,run_count integer NOT NULL CHECK(run_count BETWEEN 1 AND 32),gap_count integer NOT NULL CHECK(gap_count BETWEEN 0 AND 32),
 scanned_count integer NOT NULL CHECK(scanned_count BETWEEN run_count AND 32),held_count integer NOT NULL CHECK(held_count BETWEEN 0 AND 32),
 last_hold_reason text,last_started_at timestamptz NOT NULL,last_run_id uuid NOT NULL,
 run_ids_digest bytea NOT NULL CHECK(octet_length(run_ids_digest)=32),policy_revision bigint NOT NULL,
 recovery_attestation_id uuid NOT NULL,owner_execution_id uuid NOT NULL,fencing_token bigint NOT NULL,
 retired_at timestamptz NOT NULL DEFAULT clock_timestamp(),CHECK(run_count+held_count=scanned_count)
);
CREATE TABLE system.collection_run_retirement_context
(
 backend_pid integer NOT NULL,transaction_id xid8 NOT NULL,instance_id uuid NOT NULL,run_id uuid NOT NULL,
 policy_revision bigint NOT NULL,recovery_attestation_id uuid NOT NULL,owner_execution_id uuid NOT NULL,fencing_token bigint NOT NULL,
 PRIMARY KEY(backend_pid,transaction_id)
);
DO $private_tables$
DECLARE relation text;
BEGIN
 FOREACH relation IN ARRAY ARRAY['collection_run_retirement','collection_run_retirement_context'] LOOP
  EXECUTE format('ALTER TABLE system.%I ENABLE ROW LEVEL SECURITY',relation);
  EXECUTE format('ALTER TABLE system.%I FORCE ROW LEVEL SECURITY',relation);
  EXECUTE format('CREATE POLICY %I ON system.%I FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true)',relation||'_owner',relation);
  EXECUTE format('REVOKE ALL ON system.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',relation);
 END LOOP;
END $private_tables$;
CREATE TRIGGER collection_run_retirement_append_only BEFORE UPDATE OR DELETE ON system.collection_run_retirement
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION system.guard_canonical_run_delete()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $delete_guard$
DECLARE context system.collection_run_retirement_context%ROWTYPE; row_value jsonb:=to_jsonb(OLD);
BEGIN
 SELECT * INTO context FROM system.collection_run_retirement_context active
 WHERE active.backend_pid=pg_backend_pid() AND active.transaction_id=pg_current_xact_id();
 IF NOT FOUND OR TG_OP<>'DELETE' OR (row_value->>'run_id')::uuid IS DISTINCT FROM context.run_id
   OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM context.instance_id::text
   OR (row_value ? 'instance_id' AND (row_value->>'instance_id')::uuid IS DISTINCT FROM context.instance_id)
   OR NOT pg_has_role(session_user,'sqlobserver_migrator','member')
   OR NOT EXISTS(SELECT 1 FROM system.retention_policy policy WHERE policy.data_class='collection_run_canonical'
     AND policy.enabled AND policy.policy_revision=context.policy_revision)
   OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery WHERE recovery.attestation_id=context.recovery_attestation_id
     AND recovery.valid AND recovery.expires_at>clock_timestamp()) THEN
  RAISE EXCEPTION 'canonical run deletion requires private fenced context' USING ERRCODE='55000';
 END IF;
 PERFORM control.assert_worker_lease('retention/maintenance',context.owner_execution_id,context.fencing_token);
 RETURN OLD;
END $delete_guard$;
REVOKE ALL ON FUNCTION system.guard_canonical_run_delete() FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
DO $root_guards$
DECLARE relation text; trigger_row record;
BEGIN
 FOREACH relation IN ARRAY ARRAY['collection_run_v2','collection_run_identity','collection_run_outcome','visibility_gap'] LOOP
  FOR trigger_row IN SELECT trigger.tgname FROM pg_trigger trigger WHERE trigger.tgrelid=format('telemetry.%I',relation)::regclass
    AND NOT trigger.tgisinternal AND (trigger.tgtype & 8)<>0
    AND trigger.tgfoid='control.reject_collector_history_mutation()'::regprocedure LOOP
   EXECUTE format('DROP TRIGGER %I ON telemetry.%I',trigger_row.tgname,relation);
   -- Fleet cutover checks pin the append-only trigger name. Preserve that
   -- name for the unchanged UPDATE rejection; DELETE has its own row guard.
   EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE ON telemetry.%I FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation()',trigger_row.tgname,relation);
  END LOOP;
  EXECUTE format('CREATE TRIGGER canonical_run_delete_guard BEFORE DELETE ON telemetry.%I FOR EACH ROW EXECUTE FUNCTION system.guard_canonical_run_delete()',relation);
 END LOOP;
END $root_guards$;

-- The existing non-login reader gets only read access to the additional
-- non-FK evidence/reader dependencies. Runtime roles cannot assume it.
RESET ROLE;
DO $reader_membership$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='sqlobserver_run_dependency_reader'
    AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolinherit
    AND NOT rolreplication AND rolbypassrls)
    OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member='sqlobserver_run_dependency_reader'::regrole)
    OR EXISTS(SELECT 1 FROM pg_auth_members WHERE roleid='sqlobserver_run_dependency_reader'::regrole AND member<>current_user::regrole) THEN
  RAISE EXCEPTION 'safe non-login dependency reader required' USING ERRCODE='55000'; END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE roleid='sqlobserver_run_dependency_reader'::regrole AND member=current_user::regrole AND admin_option) THEN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolsuper) THEN
   RAISE EXCEPTION 'dependency reader requires temporary bootstrap membership' USING ERRCODE='42501'; END IF;
  EXECUTE format('GRANT sqlobserver_run_dependency_reader TO %I WITH ADMIN OPTION',current_user);
 END IF;
END $reader_membership$;
GRANT sqlobserver_run_dependency_reader TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;
GRANT USAGE ON SCHEMA analytics TO sqlobserver_run_dependency_reader;
GRANT SELECT ON analytics.evidence_packet_v2,system.partition_reader_lease TO sqlobserver_run_dependency_reader;
GRANT EXECUTE ON FUNCTION system.has_pending_analytics_dependency(timestamptz,timestamptz) TO sqlobserver_run_dependency_reader;
GRANT CREATE ON SCHEMA system TO sqlobserver_run_dependency_reader;
SET LOCAL ROLE sqlobserver_run_dependency_reader;

CREATE FUNCTION system.collection_run_non_fk_holds(p_instance_id uuid,p_run_id uuid,p_from timestamptz,p_to timestamptz)
RETURNS TABLE(hold_code text) LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $non_fk_holds$
DECLARE part record; relation oid; present boolean;
BEGIN
 IF p_instance_id IS NULL OR p_run_id IS NULL OR p_from IS NULL OR p_to IS NULL
   OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_to<=p_from OR p_to-p_from>interval '1 day'
   OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
   OR NOT pg_has_role(session_user,'sqlobserver_migrator','member')
   OR NOT EXISTS(SELECT 1 FROM telemetry.collection_run_v2 run WHERE run.instance_id=p_instance_id AND run.run_id=p_run_id) THEN
  RAISE EXCEPTION 'canonical dependency preview requires exact migration scope and bounded window' USING ERRCODE='42501'; END IF;
 IF EXISTS(SELECT 1 FROM system.partition_reader_lease reader JOIN system.partition_registry registry
    ON registry.parent_schema=reader.parent_schema AND registry.parent_table=reader.parent_table AND registry.partition_name=reader.partition_name
    WHERE reader.released_at IS NULL AND reader.expires_at>clock_timestamp()
      AND registry.range_start<p_to AND registry.range_end>p_from) THEN
  hold_code:='reader_lease_active'; RETURN NEXT;
 END IF;
 IF system.has_pending_analytics_dependency(p_from,p_to) THEN hold_code:='analytics_dependency'; RETURN NEXT; END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_class parent WHERE parent.oid='analytics.evidence_packet_v2'::regclass
   AND parent.relkind='p' AND parent.relowner='sqlobserver_migrator'::regrole AND parent.relrowsecurity AND parent.relforcerowsecurity) THEN
  hold_code:='packet_visibility_unverified'; RETURN NEXT; RETURN; END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_index idx JOIN pg_class index_class ON index_class.oid=idx.indexrelid JOIN pg_am method ON method.oid=index_class.relam
   JOIN pg_attribute column_row ON column_row.attrelid=idx.indrelid AND column_row.attnum=idx.indkey[0]
   WHERE idx.indrelid='analytics.evidence_packet_v2'::regclass AND idx.indisvalid AND idx.indisready
    AND idx.indpred IS NULL AND idx.indexprs IS NULL AND column_row.attname='source_run_id' AND method.amname='btree') THEN
  hold_code:='packet_index_unverified'; RETURN NEXT; RETURN;
 END IF;
 IF EXISTS(SELECT 1 FROM analytics.evidence_packet_v2 packet WHERE packet.source_run_id=p_run_id) THEN hold_code:='incident_evidence'; RETURN NEXT; END IF;
 IF (SELECT count(*) FROM system.partition_registry registry WHERE registry.parent_schema='analytics' AND registry.parent_table='evidence_packet_v2' AND registry.lifecycle_state='detached')>128 THEN
  hold_code:='packet_scan_bound'; RETURN NEXT; RETURN; END IF;
 FOR part IN SELECT registry.* FROM system.partition_registry registry
   WHERE registry.parent_schema='analytics' AND registry.parent_table='evidence_packet_v2' AND registry.lifecycle_state='detached'
   ORDER BY registry.partition_name LOOP
  relation:=to_regclass(format('%I.%I',part.partition_schema,part.partition_name));
  IF relation IS NULL OR part.partition_schema<>'analytics' OR NOT has_table_privilege(current_user,relation,'SELECT')
    OR NOT EXISTS(SELECT 1 FROM pg_class child WHERE child.oid=relation AND child.relkind='r' AND child.relowner='sqlobserver_migrator'::regrole AND child.relrowsecurity AND child.relforcerowsecurity)
    OR EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=relation)
    OR NOT EXISTS(SELECT 1 FROM pg_index idx JOIN pg_class index_class ON index_class.oid=idx.indexrelid JOIN pg_am method ON method.oid=index_class.relam
       JOIN pg_attribute col ON col.attrelid=idx.indrelid AND col.attnum=idx.indkey[0]
       WHERE idx.indrelid=relation AND idx.indisvalid AND idx.indisready AND idx.indpred IS NULL AND idx.indexprs IS NULL AND col.attname='source_run_id' AND method.amname='btree') THEN
   hold_code:='packet_visibility_unverified'; RETURN NEXT; CONTINUE;
  END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I WHERE source_run_id=$1)',part.partition_schema,part.partition_name) INTO present USING p_run_id;
  IF present THEN hold_code:='incident_evidence'; RETURN NEXT; END IF;
 END LOOP;
END $non_fk_holds$;
REVOKE ALL ON FUNCTION system.collection_run_non_fk_holds(uuid,uuid,timestamptz,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.collection_run_non_fk_holds(uuid,uuid,timestamptz,timestamptz) TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;
REVOKE CREATE ON SCHEMA system FROM sqlobserver_run_dependency_reader;
RESET ROLE;
REVOKE sqlobserver_run_dependency_reader FROM sqlobserver_migrator;
REVOKE sqlobserver_run_dependency_reader FROM CURRENT_USER;
SET LOCAL ROLE sqlobserver_migrator;

CREATE FUNCTION system.retire_collection_run_canonical_batch(
 p_instance_id uuid,p_before timestamptz,p_operation_id uuid,p_owner_execution_id uuid,p_fencing_token bigint,p_expected_policy_revision bigint,
 p_max_runs integer DEFAULT 32,p_after_started_at timestamptz DEFAULT NULL,p_after_run_id uuid DEFAULT NULL)
RETURNS TABLE(retirement_state text,hold_reason text,retired_count integer,held_count integer,next_started_at timestamptz,next_run_id uuid)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET lock_timeout='5s'
AS $retire_batch$
DECLARE
 policy system.retention_policy%ROWTYPE; prior system.collection_run_retirement%ROWTYPE;
 candidate record; outcome_row telemetry.collection_run_outcome%ROWTYPE; part record; child oid;
 request_hash bytea; v_attestation_id uuid; current_hold text; last_reason text;
 started timestamptz; last_id uuid; retired_ids uuid[]:=ARRAY[]::uuid[];
 total_scanned integer:=0; total_retired integer:=0; total_held integer:=0; total_gaps integer:=0; deleted integer;
 gap_count integer; gap_start timestamptz; gap_end timestamptz; gap_recent timestamptz;
 window_start timestamptz; window_end timestamptz;
BEGIN
 IF p_instance_id IS NULL OR p_operation_id IS NULL OR p_operation_id='00000000-0000-0000-0000-000000000000'::uuid
   OR p_before IS NULL OR NOT isfinite(p_before) OR p_before>clock_timestamp()-interval '90 days'
   OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_expected_policy_revision IS NULL OR p_expected_policy_revision<1
   OR p_max_runs IS NULL OR p_max_runs NOT BETWEEN 1 AND 32
   OR (p_after_started_at IS NULL)<>(p_after_run_id IS NULL)
   OR (p_after_started_at IS NOT NULL AND (NOT isfinite(p_after_started_at) OR p_after_started_at>=p_before)) THEN
  RAISE EXCEPTION 'canonical retirement bounds are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
   OR NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'canonical retirement requires migration role and exact target' USING ERRCODE='42501'; END IF;
 PERFORM control.assert_worker_lease('retention/maintenance',p_owner_execution_id,p_fencing_token);
 request_hash:=sha256(convert_to(jsonb_build_object('target',p_instance_id,'cutoff',p_before,'policy',p_expected_policy_revision,
   'limit',p_max_runs,'afterStarted',p_after_started_at,'afterRun',p_after_run_id)::text,'UTF8'));
 SELECT * INTO prior FROM system.collection_run_retirement ledger WHERE ledger.operation_id=p_operation_id;
 IF FOUND THEN
  IF prior.instance_id<>p_instance_id OR prior.request_digest<>request_hash THEN
   RAISE EXCEPTION 'canonical retirement replay differs' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'retired'::text,prior.last_hold_reason,prior.run_count,prior.held_count,prior.last_started_at,prior.last_run_id; RETURN;
 END IF;
 SELECT * INTO policy FROM system.retention_policy configured WHERE configured.data_class='collection_run_canonical' FOR SHARE;
 IF policy.data_class IS NULL OR NOT policy.enabled OR policy.retain_for IS NULL THEN
  RETURN QUERY SELECT 'held'::text,'policy_disabled'::text,0,0,NULL::timestamptz,NULL::uuid; RETURN; END IF;
 IF policy.policy_revision<>p_expected_policy_revision THEN RAISE EXCEPTION 'canonical policy revision changed' USING ERRCODE='40001'; END IF;
 IF policy.retain_for<interval '90 days' OR p_before>clock_timestamp()-policy.retain_for THEN
  RAISE EXCEPTION 'canonical cutoff exceeds policy window' USING ERRCODE='22023'; END IF;
 PERFORM 1 FROM system.collection_run_original_rollback_seal seal WHERE seal.singleton AND seal.sealed_at IS NOT NULL FOR SHARE;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM system.collection_run_identity_fk_cutover) THEN
  RETURN QUERY SELECT 'held'::text,'writer_unsealed'::text,0,0,NULL::timestamptz,NULL::uuid; RETURN; END IF;
 SELECT recovery.attestation_id INTO v_attestation_id FROM system.recovery_attestation recovery
   WHERE recovery.valid AND recovery.expires_at>clock_timestamp() ORDER BY recovery.expires_at DESC LIMIT 1 FOR SHARE;
 IF v_attestation_id IS NULL THEN
  RETURN QUERY SELECT 'held'::text,'recovery_attestation_missing'::text,0,0,NULL::timestamptz,NULL::uuid; RETURN; END IF;
 -- Bound detached metadata/privilege work before taking any relation locks.
 IF (SELECT count(*) FROM system.partition_registry registry WHERE registry.lifecycle_state='detached'
   AND (to_regclass(format('%I.%I',registry.parent_schema,registry.parent_table)) IN
     (SELECT reviewed.child_relation FROM system.collection_run_reviewed_references() reviewed)
     OR (registry.parent_schema='analytics' AND registry.parent_table='evidence_packet_v2')))>128 THEN
  RETURN QUERY SELECT 'held'::text,'detached_scan_bound'::text,0,0,NULL::timestamptz,NULL::uuid; RETURN;
 END IF;
 -- Row-exclusive root locks allow normal inserts but prevent new FK DDL.
 -- Row locks below prevent concurrent FK writers from binding retired identities.
 LOCK TABLE telemetry.collection_run_v2,telemetry.collection_run_identity,telemetry.collection_run_outcome,telemetry.visibility_gap IN ROW EXCLUSIVE MODE;
 -- These legacy non-FK dependency writers have no per-run locking protocol.
 -- A bounded batch uses SHARE locks to exclude insert/update races. NOWAIT
 -- defers cleanup when one is writing; ordinary reads remain available.
 LOCK TABLE system.partition_reader_lease,control.analytics_job,analytics.evidence_packet_v2 IN SHARE MODE NOWAIT;
 -- Lock only detached reviewed registry entries; parent SELECT locks below
 -- prevent concurrent detach of attached references.
 PERFORM 1 FROM system.partition_registry registry WHERE registry.lifecycle_state='detached'
   AND (to_regclass(format('%I.%I',registry.parent_schema,registry.parent_table)) IN
     (SELECT reviewed.child_relation FROM system.collection_run_reviewed_references() reviewed)
     OR (registry.parent_schema='analytics' AND registry.parent_table='evidence_packet_v2')) FOR SHARE;
 -- Establish direct visibility on authenticated, migration-owned detached
 -- relations before the non-login reader probes them. Unreviewed metadata
 -- remains a hold in the reference gate, not an authorization for deletion.
 FOR part IN SELECT registry.* FROM system.partition_registry registry WHERE registry.lifecycle_state='detached'
   AND (to_regclass(format('%I.%I',registry.parent_schema,registry.parent_table)) IN
       (SELECT reviewed.child_relation FROM system.collection_run_reviewed_references() reviewed)
     OR (registry.parent_schema='analytics' AND registry.parent_table='evidence_packet_v2'))
   ORDER BY registry.partition_schema,registry.partition_name LIMIT 129 LOOP
  child:=to_regclass(format('%I.%I',part.partition_schema,part.partition_name));
  IF child IS NOT NULL AND part.partition_schema=part.parent_schema
    AND EXISTS(SELECT 1 FROM pg_class relation WHERE relation.oid=child AND relation.relkind='r' AND relation.relowner='sqlobserver_migrator'::regrole
      AND ((relation.relrowsecurity AND relation.relforcerowsecurity)
        OR (part.parent_schema='telemetry' AND part.parent_table='raw_metric_sample' AND NOT relation.relrowsecurity AND NOT relation.relforcerowsecurity)))
    AND NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=child) THEN
   IF part.parent_schema='analytics' AND part.parent_table='evidence_packet_v2' THEN
    EXECUTE format('LOCK TABLE %I.%I IN SHARE MODE NOWAIT',part.partition_schema,part.partition_name);
   END IF;
   IF NOT has_table_privilege('sqlobserver_run_dependency_reader',child,'SELECT') THEN
    EXECUTE format('GRANT SELECT ON %I.%I TO sqlobserver_run_dependency_reader',part.partition_schema,part.partition_name);
   END IF;
  END IF;
 END LOOP;
 FOR candidate IN SELECT run.* FROM telemetry.collection_run_v2 run JOIN telemetry.collection_run_identity identity
   ON identity.run_id=run.run_id AND identity.started_at=run.started_at
   WHERE run.instance_id=p_instance_id AND run.started_at<p_before
     AND (p_after_started_at IS NULL OR (run.started_at,run.run_id)>(p_after_started_at,p_after_run_id))
   ORDER BY run.started_at,run.run_id LIMIT p_max_runs FOR UPDATE OF run,identity SKIP LOCKED LOOP
  PERFORM control.assert_worker_lease('retention/maintenance',p_owner_execution_id,p_fencing_token);
  total_scanned:=total_scanned+1; started:=candidate.started_at; last_id:=candidate.run_id; current_hold:=NULL;
  SELECT * INTO outcome_row FROM telemetry.collection_run_outcome completion WHERE completion.run_id=candidate.run_id FOR UPDATE;
  IF NOT FOUND THEN current_hold:='outcome_missing'; END IF;
  SELECT count(*),min(gap.gap_started_at),max(gap.gap_ended_at),max(greatest(gap.recorded_at,gap.gap_ended_at))
    INTO gap_count,gap_start,gap_end,gap_recent FROM telemetry.visibility_gap gap WHERE gap.run_id=candidate.run_id;
  IF gap_count>1 THEN current_hold:='gap_batch_oversize'; END IF;
  IF current_hold IS NULL AND greatest(candidate.started_at,outcome_row.completed_at,coalesce(gap_recent,outcome_row.completed_at))>=least(p_before,clock_timestamp()-policy.retain_for) THEN current_hold:='within_retention_window'; END IF;
  IF current_hold IS NULL AND EXISTS(SELECT 1 FROM control.collector_schedule schedule
       WHERE schedule.instance_id=p_instance_id AND schedule.collector_id=candidate.collector_id)
     AND NOT EXISTS(SELECT 1 FROM telemetry.collection_run_v2 newer
       JOIN telemetry.collection_run_outcome completion ON completion.run_id=newer.run_id
       WHERE newer.instance_id=p_instance_id AND newer.collector_id=candidate.collector_id
         AND (newer.started_at,newer.run_id)>(candidate.started_at,candidate.run_id)) THEN
   current_hold:='latest_collector_outcome';
  END IF;
  window_start:=least(candidate.started_at,candidate.scheduled_for,coalesce(gap_start,candidate.started_at));
  window_end:=greatest(candidate.started_at,outcome_row.completed_at,coalesce(gap_end,candidate.started_at))+interval '1 microsecond';
  IF current_hold IS NULL AND (window_end-window_start>interval '1 day' OR outcome_row.completed_at<candidate.started_at) THEN current_hold:='run_window_unverified'; END IF;
  IF current_hold IS NULL THEN
   SELECT reference.hold_code INTO current_hold FROM system.collection_run_reference_holds(p_instance_id,candidate.run_id) reference
     WHERE reference.hold_code<>'no_known_reference' LIMIT 1;
  END IF;
  IF current_hold IS NULL THEN
   SELECT dependency.hold_code INTO current_hold FROM system.collection_run_non_fk_holds(p_instance_id,candidate.run_id,window_start,window_end) dependency LIMIT 1;
  END IF;
  IF current_hold IS NOT NULL THEN total_held:=total_held+1; last_reason:=current_hold; CONTINUE; END IF;
  INSERT INTO system.collection_run_retirement_context VALUES(pg_backend_pid(),pg_current_xact_id(),p_instance_id,candidate.run_id,
    policy.policy_revision,v_attestation_id,p_owner_execution_id,p_fencing_token);
  DELETE FROM telemetry.visibility_gap gap WHERE gap.run_id=candidate.run_id;
  GET DIAGNOSTICS deleted=ROW_COUNT;
  IF deleted<>gap_count THEN RAISE EXCEPTION 'canonical gap count changed' USING ERRCODE='40001'; END IF;
  total_gaps:=total_gaps+deleted;
  DELETE FROM telemetry.collection_run_outcome completion WHERE completion.run_id=candidate.run_id;
  GET DIAGNOSTICS deleted=ROW_COUNT;
  IF deleted<>1 THEN RAISE EXCEPTION 'canonical outcome changed' USING ERRCODE='40001'; END IF;
  DELETE FROM telemetry.collection_run_identity identity WHERE identity.run_id=candidate.run_id AND identity.started_at=candidate.started_at;
  GET DIAGNOSTICS deleted=ROW_COUNT;
  IF deleted<>1 THEN RAISE EXCEPTION 'canonical identity changed' USING ERRCODE='40001'; END IF;
  DELETE FROM telemetry.collection_run_v2 run WHERE run.run_id=candidate.run_id AND run.started_at=candidate.started_at AND run.instance_id=p_instance_id;
  GET DIAGNOSTICS deleted=ROW_COUNT;
  IF deleted<>1 THEN RAISE EXCEPTION 'canonical header changed' USING ERRCODE='40001'; END IF;
  DELETE FROM system.collection_run_retirement_context context WHERE context.backend_pid=pg_backend_pid() AND context.transaction_id=pg_current_xact_id();
  total_retired:=total_retired+1; retired_ids:=array_append(retired_ids,candidate.run_id);
 END LOOP;
 PERFORM control.assert_worker_lease('retention/maintenance',p_owner_execution_id,p_fencing_token);
 IF NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery WHERE recovery.attestation_id=v_attestation_id AND recovery.valid AND recovery.expires_at>clock_timestamp()) THEN
  RAISE EXCEPTION 'canonical recovery attestation expired' USING ERRCODE='55000'; END IF;
 IF total_retired>0 THEN
  INSERT INTO system.collection_run_retirement VALUES(p_operation_id,p_instance_id,request_hash,p_before,total_retired,total_gaps,total_scanned,total_held,last_reason,started,last_id,
    sha256(convert_to(array_to_string(retired_ids,','),'UTF8')),policy.policy_revision,v_attestation_id,p_owner_execution_id,p_fencing_token,clock_timestamp());
  INSERT INTO audit.activity(activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details)
   VALUES(gen_random_uuid(),'system','migrator/canonical-retention','retention.collection_run_canonical.retire','not_applicable','succeeded','retention_batch',p_operation_id::text,p_operation_id,request_hash,
    jsonb_build_object('targetId',p_instance_id,'runCount',total_retired,'gapCount',total_gaps,'heldCount',total_held,'cutoff',p_before,
      'policyRevision',policy.policy_revision,'recoveryAttestationId',v_attestation_id,'fencingToken',p_fencing_token));
 END IF;
 RETURN QUERY SELECT CASE WHEN total_retired>0 THEN 'retired' WHEN total_scanned>0 THEN 'held' ELSE 'idle' END,last_reason,total_retired,total_held,started,last_id;
END $retire_batch$;
REVOKE ALL ON FUNCTION system.retire_collection_run_canonical_batch(uuid,timestamptz,uuid,uuid,bigint,bigint,integer,timestamptz,uuid)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.retire_collection_run_canonical_batch(uuid,timestamptz,uuid,uuid,bigint,bigint,integer,timestamptz,uuid) TO sqlobserver_migrator;
