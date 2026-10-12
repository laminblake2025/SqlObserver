-- Point the M7 observation policy at the written successor, never the empty
-- v2 scaffold. The default becomes live only with the fenced writer switch.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $redirect$
BEGIN
 PERFORM set_config('sqlobserver.retention_changed_by','migration-0177',true);
 PERFORM set_config('sqlobserver.retention_change_reason',
  'canonical_m7_observation_parent',true);
 UPDATE system.retention_policy AS policy
 SET parent_table='query_performance_observation_v3',
     enabled=false,
     retain_for=CASE WHEN policy.policy_revision=1
                      AND NOT policy.enabled AND policy.retain_for IS NULL
                     THEN interval '90 days' ELSE policy.retain_for END,
     policy_revision=policy.policy_revision+1,
     updated_by='migration-0177',updated_at=clock_timestamp()
 WHERE policy.data_class='m7_query_performance'
   AND policy.parent_schema='events'
   AND policy.parent_table='query_performance_observation_v2'
   AND policy.partition_granularity='day';
 IF NOT FOUND THEN
  RAISE EXCEPTION 'M7 retention policy does not point at the expected scaffold'
   USING ERRCODE='55000';
 END IF;
END $redirect$;

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
   OR data_class IN
    ('raw_metric_samples','m5_activity','m5_requests','m5_waits',
     'm5_blocking','m6_deadlocks','m8_alert_history','m8_alert_delivery',
     'm9_backup_status','m9_agent_history','m9_agent_failures',
     'm9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')));

CREATE FUNCTION system.guard_m7_query_observation_retention()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,system
AS $guard$
BEGIN
 IF NEW.data_class='m7_query_performance' AND NEW.enabled
    AND NOT EXISTS (
     SELECT 1 FROM system.query_observation_v3_read_route AS route
     WHERE route.singleton AND route.use_v3 AND route.write_v3) THEN
  RAISE EXCEPTION 'M7 observation retention requires v3 writer activation'
   USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $guard$;
REVOKE ALL ON FUNCTION system.guard_m7_query_observation_retention()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER guard_m7_query_observation_retention
 BEFORE INSERT OR UPDATE ON system.retention_policy
 FOR EACH ROW EXECUTE FUNCTION system.guard_m7_query_observation_retention();

-- Keep the operator's policy revision if they configured the old scaffold or
-- changed the new policy. Only the untouched seed receives the live default.
CREATE OR REPLACE FUNCTION control.activate_query_observation_v3_writer()
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system SET row_security=on
SET TimeZone='UTC'
AS $activate$
DECLARE
 v_route system.query_observation_v3_read_route%ROWTYPE;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'query observation writer activation requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE control.observation_target IN SHARE MODE;
 LOCK TABLE events.query_performance_observation IN SHARE MODE;
 LOCK TABLE events.query_performance_observation_v3 IN SHARE MODE;
 SELECT * INTO STRICT v_route
 FROM system.query_observation_v3_read_route AS route
 WHERE route.singleton FOR UPDATE;
 IF NOT v_route.use_v3 THEN
  RAISE EXCEPTION 'query observation v3 reads must be active before writer activation'
   USING ERRCODE='55000';
 END IF;
 IF v_route.write_v3 THEN RETURN v_route.revision; END IF;
 PERFORM control.set_query_observation_v3_read_route(true);
 UPDATE system.query_observation_v3_read_route AS route
 SET write_v3=true,revision=route.revision+1,
     changed_at=clock_timestamp(),changed_by=session_user::text
 WHERE route.singleton RETURNING route.revision INTO v_route.revision;
 PERFORM set_config('sqlobserver.retention_changed_by','writer-activation',true);
 PERFORM set_config('sqlobserver.retention_change_reason',
  'canonical_m7_observation_writer_active',true);
 UPDATE system.retention_policy AS policy
 SET enabled=true,policy_revision=policy.policy_revision+1,
     updated_by='writer-activation',updated_at=clock_timestamp()
 WHERE policy.data_class='m7_query_performance'
   AND policy.parent_schema='events'
   AND policy.parent_table='query_performance_observation_v3'
   AND policy.partition_granularity='day'
   AND policy.policy_revision=2
   AND policy.updated_by='migration-0177'
   AND NOT policy.enabled AND policy.retain_for=interval '90 days';
 RETURN v_route.revision;
END $activate$;

-- SecurityAdministrator may change M7 retention after writer activation.
-- Eight days is the floor because accepted source windows span up to seven.
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
     'm7_query_performance')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days') THEN
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
