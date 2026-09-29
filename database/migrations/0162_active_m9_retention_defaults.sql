-- The M9 backup, Agent, and availability snapshots are written to the
-- registered daily parents. Keep the TempDB parent/file pair disabled until
-- their incoming foreign key can be retired as one dependency-aware unit.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $m9_defaults$
BEGIN
 PERFORM set_config('sqlobserver.retention_changed_by','migration-0162',true);
 PERFORM set_config('sqlobserver.retention_change_reason',
  'active_m9_default_retention',true);

 UPDATE system.retention_policy AS policy
 SET enabled=true,retain_for=defaults.retain_for,
     policy_revision=policy.policy_revision+1,
     updated_by='migration-0162',updated_at=clock_timestamp()
 FROM (VALUES
   ('m9_backup_status','telemetry'::name,'backup_status_snapshot'::name,'day',interval '90 days'),
   ('m9_agent_history','telemetry'::name,'sql_agent_failure_scan_snapshot'::name,'day',interval '30 days'),
   ('m9_agent_occurrences','telemetry'::name,'sql_agent_failure_occurrence'::name,'day',interval '90 days'),
   ('m9_ag_replicas','telemetry'::name,'availability_group_replica_snapshot'::name,'day',interval '30 days'),
   ('m9_ag_databases','telemetry'::name,'availability_group_database_snapshot'::name,'day',interval '30 days')
  ) AS defaults(data_class,parent_schema,parent_table,granularity,retain_for)
 WHERE policy.data_class=defaults.data_class
   AND policy.parent_schema=defaults.parent_schema
   AND policy.parent_table=defaults.parent_table
   AND policy.partition_granularity=defaults.granularity
   AND policy.policy_revision=1
   AND NOT policy.enabled AND policy.retain_for IS NULL;
END $m9_defaults$;

-- Match the SecurityAdministrator policy mutation surface to the newly
-- enabled classes. Keep disabled Agent identity and TempDB policies out.
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
     'm9_ag_replicas','m9_ag_databases',
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
