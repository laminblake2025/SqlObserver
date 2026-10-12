-- Register both new Query Store parents with the existing fenced retention
-- worker before a group collector can write them. Policies are enabled with
-- distinct short-lived watermark and longer diagnostic-history windows.
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
   'database_inventory_snapshot_v2','database_file_snapshot_v2','query_performance_observation_v2',
   'deadlock_summary_v2','deadlock_participant_v2','deadlock_relation_v2','state_history_v2','delivery_attempt_v2','delivery_outbox_v2',
   'backup_status_snapshot_v2','sql_agent_failure_scan_snapshot_v2','sql_agent_failure_occurrence_v2','tempdb_snapshot_v2','tempdb_file_snapshot_v2',
   'availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2','sql_volume_snapshot',
   'query_store_group_watermark','query_store_group_delta'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry'
       AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events'
       AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events'
       AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR data_class IN
   ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking',
    'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
    'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
    'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
    'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')));

INSERT INTO system.retention_policy
 (data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep)
VALUES
 ('m7_group_watermarks','events','query_store_group_watermark','day',true,interval '8 days',3),
 ('m7_group_deltas','events','query_store_group_delta','day',true,interval '30 days',3);

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
    ('m5_activity','m5_requests','m5_waits','m5_blocking',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence',
     'sql_volume_capacity','m7_group_watermarks','m7_group_deltas')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day')) THEN
  RAISE EXCEPTION 'retention policy bounds rejected' USING ERRCODE='22023';
 END IF;
 PERFORM set_config('sqlobserver.retention_change_reason',p_change_reason,true);
 PERFORM set_config('sqlobserver.retention_changed_by',p_changed_by,true);
 UPDATE system.retention_policy
 SET enabled=p_enabled,retain_for=p_retain_for,
     minimum_partitions_to_keep=p_minimum_partitions,
     policy_revision=policy_revision+1,updated_by=p_changed_by,updated_at=clock_timestamp()
 WHERE data_class=p_data_class AND policy_revision=p_expected_revision
 RETURNING policy_revision INTO next_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'retention policy revision conflict' USING ERRCODE='40001';
 END IF;
 RETURN next_revision;
END $policy_update$;

-- Replaces the preparation function from 0142. Every existing or future
-- attached partition receives an exact registry entry for fenced retention.
CREATE OR REPLACE FUNCTION control.ensure_query_store_group_partitions(p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
DECLARE v_partition_day date; v_partition_name text; v_parent_name text;
        v_parent_oid oid; v_child_oid oid; created integer := 0;
BEGIN
 IF p_anchor IS NULL OR p_anchor <> (clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'Query Store partition anchor must be current UTC date'
   USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:query-store-group:partition-set',0));
 FOREACH v_parent_name IN ARRAY ARRAY['query_store_group_watermark','query_store_group_delta'] LOOP
  v_parent_oid := format('events.%I',v_parent_name)::regclass;
  FOR v_partition_day IN SELECT p_anchor+i FROM generate_series(-7,8) AS i LOOP
   v_partition_name := format('%s_p%s',v_parent_name,to_char(v_partition_day,'YYYYMMDD'));
   v_child_oid := to_regclass(format('events.%I',v_partition_name));
   IF v_child_oid IS NULL THEN
    EXECUTE format('CREATE TABLE events.%I PARTITION OF events.%I FOR VALUES FROM (%L) TO (%L)',
      v_partition_name,v_parent_name,v_partition_day::timestamptz,(v_partition_day+1)::timestamptz);
    created := created + 1;
    v_child_oid := to_regclass(format('events.%I',v_partition_name));
   END IF;
   IF NOT EXISTS (SELECT 1 FROM pg_inherits
                  WHERE inhrelid=v_child_oid AND inhparent=v_parent_oid) THEN
    RAISE EXCEPTION 'Query Store partition name is occupied by another relation'
     USING ERRCODE='55000';
   END IF;
   EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_partition_name);
   INSERT INTO system.partition_registry
    (parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
   VALUES ('events',v_parent_name,'events',v_partition_name,'day',
           v_partition_day::timestamptz,(v_partition_day+1)::timestamptz)
   ON CONFLICT DO NOTHING;
   IF NOT EXISTS (SELECT 1 FROM system.partition_registry registry
                  WHERE registry.parent_schema='events' AND registry.parent_table=v_parent_name
                    AND registry.partition_schema='events' AND registry.partition_name=v_partition_name
                    AND registry.partition_granularity='day'
                    AND registry.range_start=v_partition_day::timestamptz
                    AND registry.range_end=(v_partition_day+1)::timestamptz
                    AND registry.lifecycle_state='attached') THEN
    RAISE EXCEPTION 'Query Store partition registry identity differs from the attached partition'
     USING ERRCODE='55000';
   END IF;
  END LOOP;
 END LOOP;
 RETURN created;
END $fn$;

SELECT control.ensure_query_store_group_partitions(current_date);
