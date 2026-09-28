-- The active M10 v2 parents are still written and partition-maintained.
-- Enable bounded retention only for untouched policies; preserve every
-- operator revision and the fenced worker's recovery/grace safeguards.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $m10_defaults$
BEGIN
 PERFORM set_config('sqlobserver.retention_changed_by','migration-0158',true);
 PERFORM set_config('sqlobserver.retention_change_reason',
  'active_m10_default_retention',true);

 UPDATE system.retention_policy AS policy
 SET enabled=true,retain_for=defaults.retain_for,
     policy_revision=policy.policy_revision+1,
     updated_by='migration-0158',updated_at=clock_timestamp()
 FROM (VALUES
   ('m10_host_metrics','telemetry'::name,'host_metric_snapshot_v2'::name,'day',interval '30 days'),
   ('m10_replication','telemetry'::name,'replication_snapshot_v2'::name,'day',interval '30 days'),
   ('m10_rollups','analytics'::name,'metric_rollup_v2'::name,'day',interval '180 days'),
   ('m10_evidence','analytics'::name,'evidence_packet_v2'::name,'month',interval '180 days')
  ) AS defaults(data_class,parent_schema,parent_table,granularity,retain_for)
 WHERE policy.data_class=defaults.data_class
   AND policy.parent_schema=defaults.parent_schema
   AND policy.parent_table=defaults.parent_table
   AND policy.partition_granularity=defaults.granularity
   AND policy.policy_revision=1
   AND NOT policy.enabled AND policy.retain_for IS NULL;
END $m10_defaults$;
