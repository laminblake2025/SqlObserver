-- Turn on bounded retention for the four written M5 evidence streams. The
-- canonical parent mapping was established by 0103; a policy changed since
-- that cutover belongs to the operator and is never reset by this migration.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $m5_defaults$
BEGIN
 PERFORM set_config('sqlobserver.retention_changed_by','migration-0156',true);
 PERFORM set_config('sqlobserver.retention_change_reason',
  'canonical_m5_default_retention',true);

 UPDATE system.retention_policy AS policy
 SET enabled=true,retain_for=defaults.retain_for,
     policy_revision=policy.policy_revision+1,
     updated_by='migration-0156',updated_at=clock_timestamp()
 FROM (VALUES
   ('m5_activity','telemetry'::name,'activity_session_snapshot'::name,2::bigint,interval '30 days'),
   ('m5_requests','telemetry'::name,'activity_request_snapshot'::name,1::bigint,interval '14 days'),
   ('m5_waits','telemetry'::name,'server_wait_snapshot'::name,1::bigint,interval '14 days'),
   ('m5_blocking','events'::name,'blocking_edge'::name,2::bigint,interval '90 days')
  ) AS defaults(data_class,parent_schema,parent_table,original_revision,retain_for)
 WHERE policy.data_class=defaults.data_class
   AND policy.parent_schema=defaults.parent_schema
   AND policy.parent_table=defaults.parent_table
   AND policy.policy_revision=defaults.original_revision
   AND NOT policy.enabled AND policy.retain_for IS NULL;
END $m5_defaults$;
