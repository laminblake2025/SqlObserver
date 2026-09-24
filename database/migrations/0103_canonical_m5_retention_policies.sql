-- Expose the canonical, written M5 partitions to retention. A configured
-- legacy v2 policy requires an explicit operator decision before upgrade.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $$
BEGIN
 IF EXISTS (
  SELECT 1 FROM system.retention_policy
  WHERE data_class IN ('m5_activity','m5_blocking')
    AND (enabled OR retain_for IS NOT NULL)) THEN
  RAISE EXCEPTION 'Disable configured legacy M5 retention policies before canonical cutover'
   USING ERRCODE='55000';
 END IF;
 IF NOT EXISTS (
  SELECT 1 FROM system.retention_policy
  WHERE data_class='m5_activity' AND parent_schema='telemetry'
    AND parent_table='activity_session_snapshot_v2' AND partition_granularity='day')
  OR NOT EXISTS (
  SELECT 1 FROM system.retention_policy
  WHERE data_class='m5_blocking' AND parent_schema='events'
    AND parent_table='blocking_edge_v2' AND partition_granularity='month') THEN
  RAISE EXCEPTION 'Legacy M5 retention policy mapping differs from the upgrade contract'
   USING ERRCODE='55000';
 END IF;
 PERFORM set_config('sqlobserver.retention_changed_by','migration-0103',true);
 PERFORM set_config('sqlobserver.retention_change_reason','canonical_m5_parent_cutover',true);
 UPDATE system.retention_policy SET parent_table='activity_session_snapshot',
  policy_revision=policy_revision+1,updated_at=clock_timestamp(),updated_by='migration-0103'
 WHERE data_class='m5_activity';
 UPDATE system.retention_policy SET parent_table='blocking_edge',
  policy_revision=policy_revision+1,updated_at=clock_timestamp(),updated_by='migration-0103'
 WHERE data_class='m5_blocking';
END $$;

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR data_class IN
   ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking',
    'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
    'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
    'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
    'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')));
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_m5_canonical_retention_parent CHECK
 (data_class NOT IN ('m5_activity','m5_requests','m5_waits','m5_blocking')
  OR (data_class='m5_activity' AND parent_schema='telemetry'
      AND parent_table='activity_session_snapshot' AND partition_granularity='day')
  OR (data_class='m5_requests' AND parent_schema='telemetry'
      AND parent_table='activity_request_snapshot' AND partition_granularity='day')
  OR (data_class='m5_waits' AND parent_schema='telemetry'
      AND parent_table='server_wait_snapshot' AND partition_granularity='day')
  OR (data_class='m5_blocking' AND parent_schema='events'
      AND parent_table='blocking_edge' AND partition_granularity='month'));

INSERT INTO system.retention_policy
 (data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep)
VALUES
 ('m5_requests','telemetry','activity_request_snapshot','day',false,NULL,3),
 ('m5_waits','telemetry','server_wait_snapshot','day',false,NULL,3);

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
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')
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
