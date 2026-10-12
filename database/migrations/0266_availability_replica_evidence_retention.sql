-- Register AG v2 evidence retention before granting its collector commit.
-- Existing v1 policy revisions and parent routes remain independently managed.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.partition_registry DROP CONSTRAINT ck_partition_registry_parent;
ALTER TABLE system.partition_registry ADD CONSTRAINT ck_partition_registry_parent CHECK
  (parent_schema IN ('telemetry'::name,'events'::name,'analytics'::name,'alerting'::name)
   AND parent_table IN
  ('raw_metric_sample','diagnostic_event','activity_session_snapshot','activity_request_snapshot','server_wait_snapshot','blocking_edge',
   'database_inventory_snapshot','database_file_snapshot','host_metric_snapshot_v2','replication_snapshot_v2',
   'backup_status_snapshot','sql_agent_failure_scan_snapshot','sql_agent_failure_occurrence','tempdb_snapshot','tempdb_file_snapshot',
   'availability_group_replica_snapshot','availability_group_database_snapshot','query_performance_observation',
   'deadlock_summary','deadlock_participant','deadlock_relation','state_history','delivery_attempt','delivery_outbox',
   'metric_rollup_v2','evidence_packet_v2',
   'activity_session_snapshot_v2','activity_request_snapshot_v2','server_wait_snapshot_v2','blocking_edge_v2',
   'database_inventory_snapshot_v2','database_file_snapshot_v2','query_performance_observation_v2','query_performance_observation_v3',
   'deadlock_summary_v2','deadlock_participant_v2','deadlock_relation_v2','state_history_v2','delivery_attempt_v2','delivery_outbox_v2',
   'backup_status_snapshot_v2','sql_agent_failure_scan_snapshot_v2','sql_agent_failure_occurrence_v2','tempdb_snapshot_v2','tempdb_file_snapshot_v2',
   'availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2','sql_volume_snapshot',
   'query_store_group_watermark','query_store_group_delta','collection_run_v2',
   'availability_replica_evidence','availability_database_replica_evidence'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy',
  'm9_ag_replica_evidence','m9_ag_database_evidence'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events'
    AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry'
    AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events'
    AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events'
    AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR (data_class='m7_query_performance' AND parent_schema='events'
    AND parent_table='query_performance_observation_v3'
    AND partition_granularity='day')
   OR (data_class='collection_run_original_copy'
       AND parent_schema='telemetry' AND parent_table='collection_run'
       AND partition_granularity='none')
   OR (data_class='m9_ag_replica_evidence' AND parent_schema='telemetry'
       AND parent_table='availability_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_ag_database_evidence' AND parent_schema='telemetry'
       AND parent_table='availability_database_replica_evidence' AND partition_granularity='day')
   OR data_class IN
    ('raw_metric_samples','m5_activity','m5_requests','m5_waits',
     'm5_blocking','m6_deadlocks','m8_alert_history','m8_alert_delivery',
     'm9_backup_status','m9_agent_history','m9_agent_failures',
     'm9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')));

INSERT INTO system.retention_policy
 (data_class,parent_schema,parent_table,partition_granularity,enabled,
  retain_for,minimum_partitions_to_keep,updated_by)
VALUES
 ('m9_ag_replica_evidence','telemetry','availability_replica_evidence','day',
  true,interval '30 days',3,'migration-0266'),
 ('m9_ag_database_evidence','telemetry','availability_database_replica_evidence','day',
  true,interval '30 days',3,'migration-0266');

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
     'm7_query_performance','collection_run_original_copy',
     'm9_ag_replica_evidence','m9_ag_database_evidence')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
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

-- This owner-only primitive is also available to migrations. Application roles
-- must use the wrapper below, which checks the catalog lease before and after DDL.
CREATE FUNCTION control.ensure_availability_evidence_partitions(p_anchor date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $partitions$
DECLARE
 v_day date;
 v_name text;
 v_parent_name text;
 v_parent oid;
 v_child oid;
 v_created integer:=0;
BEGIN
 IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date
 THEN RAISE EXCEPTION 'AG evidence partition anchor must be current UTC date'
      USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:ag-evidence:partition-set',0));
 FOREACH v_parent_name IN ARRAY ARRAY[
    'availability_replica_evidence','availability_database_replica_evidence'] LOOP
  v_parent:=format('telemetry.%I',v_parent_name)::regclass;
  IF NOT EXISTS (SELECT 1 FROM pg_class AS parent WHERE parent.oid=v_parent
       AND parent.relkind='p' AND parent.relrowsecurity AND parent.relforcerowsecurity
       AND pg_get_userbyid(parent.relowner)='sqlobserver_migrator')
  THEN RAISE EXCEPTION 'AG evidence parent security differs from the reviewed contract'
       USING ERRCODE='55000'; END IF;
  FOR v_day IN SELECT p_anchor+i FROM generate_series(-1,1) AS days(i) LOOP
   v_name:=format('%s_p%s',v_parent_name,to_char(v_day,'YYYYMMDD'));
   v_child:=to_regclass(format('telemetry.%I',v_name));
   IF v_child IS NULL THEN
    EXECUTE format('CREATE TABLE telemetry.%I PARTITION OF telemetry.%I FOR VALUES FROM (%L) TO (%L)',
      v_name,v_parent_name,v_day::timestamptz,(v_day+1)::timestamptz);
    v_created:=v_created+1;
    v_child:=to_regclass(format('telemetry.%I',v_name));
   END IF;
   IF NOT EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid=v_child AND inhparent=v_parent)
      OR NOT EXISTS (SELECT 1 FROM pg_class AS child WHERE child.oid=v_child
        AND child.relkind='r' AND pg_get_userbyid(child.relowner)='sqlobserver_migrator'
        AND pg_get_expr(child.relpartbound,child.oid)=format('FOR VALUES FROM (%L) TO (%L)',
            v_day::timestamptz,(v_day+1)::timestamptz))
   THEN RAISE EXCEPTION 'AG evidence partition identity or bounds differ'
        USING ERRCODE='55000'; END IF;
   EXECUTE format('REVOKE ALL ON telemetry.%I FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor',v_name);
   INSERT INTO system.partition_registry
     (parent_schema,parent_table,partition_schema,partition_name,
      partition_granularity,range_start,range_end)
   VALUES ('telemetry',v_parent_name,'telemetry',v_name,'day',
       v_day::timestamptz,(v_day+1)::timestamptz)
   ON CONFLICT DO NOTHING;
   IF NOT EXISTS (SELECT 1 FROM system.partition_registry AS registry
       WHERE registry.parent_schema='telemetry' AND registry.parent_table=v_parent_name
         AND registry.partition_schema='telemetry' AND registry.partition_name=v_name
         AND registry.partition_granularity='day' AND registry.lifecycle_state='attached'
         AND registry.range_start=v_day::timestamptz AND registry.range_end=(v_day+1)::timestamptz)
   THEN RAISE EXCEPTION 'AG evidence partition registry differs from the attached partition'
        USING ERRCODE='55000'; END IF;
  END LOOP;
 END LOOP;
 RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_availability_evidence_partitions(date)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.ensure_availability_evidence_partition_set(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fenced$
DECLARE v_created integer;
BEGIN
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 v_created:=control.ensure_availability_evidence_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 RETURN v_created;
END $fenced$;
REVOKE ALL ON FUNCTION control.ensure_availability_evidence_partition_set(uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_availability_evidence_partition_set(uuid,bigint)
 TO sqlobserver_collector;
