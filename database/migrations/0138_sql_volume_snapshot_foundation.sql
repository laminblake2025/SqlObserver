-- Dedicated, path-free SQL target volume capacity evidence. Collection stays
-- disabled until the fenced commit and read contracts are installed.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.sql_volume_snapshot
(
    observed_at timestamptz NOT NULL,
    run_id uuid NOT NULL REFERENCES telemetry.collection_run(run_id),
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    target_revision bigint NOT NULL,
    volume_key bytea NOT NULL,
    identity_kind text NOT NULL,
    mapped_file_count integer NOT NULL,
    total_bytes bigint,
    available_bytes bigint,
    collected_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT pk_sql_volume_snapshot PRIMARY KEY (observed_at, run_id, volume_key),
    CONSTRAINT ck_sql_volume_snapshot_identity CHECK
      (target_revision > 0 AND octet_length(volume_key) = 32
       AND identity_kind IN ('volume_id', 'mount_point', 'file_scoped_unknown')
       AND mapped_file_count BETWEEN 1 AND 1000),
    CONSTRAINT ck_sql_volume_snapshot_capacity CHECK
      ((total_bytes IS NULL AND available_bytes IS NULL)
       OR (total_bytes > 0 AND available_bytes BETWEEN 0 AND total_bytes)),
    CONSTRAINT ck_sql_volume_snapshot_time CHECK
      (isfinite(observed_at) AND isfinite(collected_at)
       AND observed_at <= collected_at + interval '5 minutes')
) PARTITION BY RANGE (observed_at);

COMMENT ON TABLE telemetry.sql_volume_snapshot IS
    'One keyed, deduplicated SQL-reported capacity row per visible volume and fenced run. Raw volume and file paths are never stored.';
CREATE INDEX ix_sql_volume_snapshot_target_time
    ON telemetry.sql_volume_snapshot(instance_id, observed_at DESC, run_id, volume_key);

ALTER TABLE telemetry.sql_volume_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.sql_volume_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY sql_volume_snapshot_scope ON telemetry.sql_volume_snapshot
    USING (instance_id::text = current_setting('sqlobserver.target_scope', true))
    WITH CHECK (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY sql_volume_snapshot_owner ON telemetry.sql_volume_snapshot
    FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
REVOKE ALL ON telemetry.sql_volume_snapshot FROM PUBLIC, sqlobserver_server,
    sqlobserver_collector, sqlobserver_auditor;
CREATE TRIGGER sql_volume_snapshot_append_only
    BEFORE UPDATE OR DELETE ON telemetry.sql_volume_snapshot
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

-- The existing retention worker accepts only registered, allowlisted parents.
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
   'availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2','sql_volume_snapshot'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry'
       AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR data_class IN
   ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking',
    'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
    'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
    'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
    'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')));
INSERT INTO system.retention_policy
 (data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep)
VALUES ('sql_volume_capacity','telemetry','sql_volume_snapshot','day',true,interval '30 days',3);

-- Keep the new policy adjustable through the existing SecurityAdministrator API.
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
     'sql_volume_capacity')
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

CREATE FUNCTION control.ensure_sql_volume_partitions(p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $fn$
DECLARE partition_day date; partition_name text; created integer := 0;
BEGIN
 IF p_anchor IS NULL OR p_anchor < current_date - 1 OR p_anchor > current_date + 1 THEN
  RAISE EXCEPTION 'SQL volume partition anchor outside UTC maintenance window'
   USING ERRCODE = '22023';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:sql-volume:partition-set',0));
 FOR partition_day IN SELECT p_anchor+i FROM generate_series(-1,7) AS i LOOP
  partition_name := format('sql_volume_snapshot_p%s',to_char(partition_day,'YYYYMMDD'));
  EXECUTE format('CREATE TABLE IF NOT EXISTS telemetry.%I PARTITION OF telemetry.sql_volume_snapshot FOR VALUES FROM (%L) TO (%L)',
    partition_name,partition_day::timestamptz,(partition_day+1)::timestamptz);
  EXECUTE format('REVOKE ALL ON TABLE telemetry.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',partition_name);
  INSERT INTO system.partition_registry
   (parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
  VALUES ('telemetry','sql_volume_snapshot','telemetry',partition_name,'day',
    partition_day::timestamptz,(partition_day+1)::timestamptz)
  ON CONFLICT DO NOTHING;
  created := created + 1;
 END LOOP;
 RETURN created;
END;
$fn$;
REVOKE ALL ON FUNCTION control.ensure_sql_volume_partitions(date)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_sql_volume_partitions(date)
    TO sqlobserver_collector;
SELECT control.ensure_sql_volume_partitions(current_date);
