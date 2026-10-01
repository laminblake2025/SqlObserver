-- Staged cache-entry baselines and additive deltas. No collector is activated.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.plan_cache_runtime_watermark
(
    observed_day timestamptz NOT NULL,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL CHECK(target_revision>0),
    database_id integer NOT NULL CHECK(database_id BETWEEN 5 AND 32766),
    database_incarnation bytea NOT NULL CHECK(octet_length(database_incarnation)=32),
    source_epoch bytea NOT NULL CHECK(octet_length(source_epoch)=32),
    query_fingerprint bytea NOT NULL CHECK(octet_length(query_fingerprint)=32),
    plan_fingerprint bytea NOT NULL CHECK(octet_length(plan_fingerprint)=32),
    cache_entry_fingerprint bytea NOT NULL CHECK(octet_length(cache_entry_fingerprint)=32),
    cpu_us bigint NOT NULL CHECK(cpu_us>=0),
    duration_us bigint NOT NULL CHECK(duration_us>=0),
    execution_count bigint NOT NULL CHECK(execution_count>=0),
    logical_reads bigint NOT NULL CHECK(logical_reads>=0),
    writes bigint NOT NULL CHECK(writes>=0),
    rows_processed bigint NOT NULL CHECK(rows_processed>=0),
    source_read_started_at timestamptz NOT NULL,
    source_read_completed_at timestamptz NOT NULL,
    identity_before_started_at timestamptz NOT NULL,
    identity_before_completed_at timestamptz NOT NULL,
    identity_after_started_at timestamptz NOT NULL,
    identity_after_completed_at timestamptz NOT NULL,
    last_run_id uuid NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(observed_day,instance_id,target_revision,database_id,query_fingerprint,cache_entry_fingerprint),
    CHECK(isfinite(observed_day) AND isfinite(updated_at)
        AND isfinite(source_read_started_at) AND isfinite(source_read_completed_at)
        AND isfinite(identity_before_started_at) AND isfinite(identity_before_completed_at)
        AND isfinite(identity_after_started_at) AND isfinite(identity_after_completed_at)
        AND observed_day=date_trunc('day',source_read_completed_at,'UTC')
        AND identity_before_completed_at>=identity_before_started_at
        AND identity_before_completed_at-identity_before_started_at<=interval '5 seconds'
        AND identity_before_completed_at<=source_read_started_at
        AND source_read_completed_at>=source_read_started_at
        AND source_read_completed_at-source_read_started_at<=interval '20 seconds'
        AND identity_after_started_at>=source_read_completed_at
        AND identity_after_completed_at>=identity_after_started_at
        AND identity_after_completed_at-identity_after_started_at<=interval '5 seconds'
        AND identity_after_completed_at-identity_before_started_at<=interval '30 seconds')
) PARTITION BY RANGE(observed_day);
CREATE INDEX ix_plan_cache_runtime_watermark_entry ON events.plan_cache_runtime_watermark
    (instance_id,target_revision,database_id,query_fingerprint,cache_entry_fingerprint,source_read_completed_at DESC);

CREATE TABLE events.plan_cache_runtime_delta
(
    committed_at timestamptz NOT NULL CHECK(isfinite(committed_at)),
    run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL CHECK(target_revision>0),
    database_id integer NOT NULL CHECK(database_id BETWEEN 5 AND 32766),
    database_incarnation bytea NOT NULL CHECK(octet_length(database_incarnation)=32),
    source_epoch bytea NOT NULL CHECK(octet_length(source_epoch)=32),
    query_fingerprint bytea NOT NULL CHECK(octet_length(query_fingerprint)=32),
    plan_fingerprint bytea NOT NULL CHECK(octet_length(plan_fingerprint)=32),
    cache_entry_fingerprint bytea NOT NULL CHECK(octet_length(cache_entry_fingerprint)=32),
    source_read_started_at timestamptz NOT NULL CHECK(isfinite(source_read_started_at)),
    source_read_completed_at timestamptz NOT NULL CHECK(isfinite(source_read_completed_at)),
    identity_before_started_at timestamptz NOT NULL CHECK(isfinite(identity_before_started_at)),
    identity_before_completed_at timestamptz NOT NULL CHECK(isfinite(identity_before_completed_at)),
    identity_after_started_at timestamptz NOT NULL CHECK(isfinite(identity_after_started_at)),
    identity_after_completed_at timestamptz NOT NULL CHECK(isfinite(identity_after_completed_at)),
    transition text NOT NULL CHECK(transition IN
        ('baseline_unavailable','comparable','reset','stale','comparison_window_expired')),
    delta_from timestamptz,
    delta_to timestamptz,
    cpu_us bigint,
    duration_us bigint,
    execution_count bigint,
    logical_reads bigint,
    writes bigint,
    rows_processed bigint,
    PRIMARY KEY(committed_at,run_id,database_id,cache_entry_fingerprint),
    CHECK((transition='comparable' AND delta_from IS NOT NULL AND delta_to IS NOT NULL
        AND isfinite(delta_from) AND isfinite(delta_to) AND delta_to>delta_from
        AND delta_to-delta_from<=interval '7 days' AND delta_to=source_read_completed_at
        AND cpu_us IS NOT NULL AND duration_us IS NOT NULL AND execution_count IS NOT NULL
        AND logical_reads IS NOT NULL AND writes IS NOT NULL AND rows_processed IS NOT NULL
        AND cpu_us>=0 AND duration_us>=0 AND execution_count>=0
        AND logical_reads>=0 AND writes>=0 AND rows_processed>=0)
        OR (transition<>'comparable' AND delta_from IS NULL AND delta_to IS NULL
            AND cpu_us IS NULL AND duration_us IS NULL AND execution_count IS NULL
            AND logical_reads IS NULL AND writes IS NULL AND rows_processed IS NULL)),
    CHECK(identity_before_completed_at>=identity_before_started_at
        AND identity_before_completed_at-identity_before_started_at<=interval '5 seconds'
        AND identity_before_completed_at<=source_read_started_at
        AND source_read_completed_at>=source_read_started_at
        AND source_read_completed_at-source_read_started_at<=interval '20 seconds'
        AND identity_after_started_at>=source_read_completed_at
        AND identity_after_completed_at>=identity_after_started_at
        AND identity_after_completed_at-identity_after_started_at<=interval '5 seconds'
        AND identity_after_completed_at-identity_before_started_at<=interval '30 seconds')
) PARTITION BY RANGE(committed_at);
CREATE INDEX ix_plan_cache_runtime_delta_target_time ON events.plan_cache_runtime_delta
    (instance_id,source_read_completed_at DESC,database_id,query_fingerprint);

CREATE TABLE events.plan_cache_runtime_receipt
(
    committed_at timestamptz NOT NULL CHECK(isfinite(committed_at)),
    run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL CHECK(target_revision>0),
    payload_digest bytea NOT NULL CHECK(octet_length(payload_digest)=32),
    source_epoch bytea NOT NULL CHECK(octet_length(source_epoch)=32),
    source_read_started_at timestamptz NOT NULL CHECK(isfinite(source_read_started_at)),
    source_read_completed_at timestamptz NOT NULL CHECK(isfinite(source_read_completed_at)),
    candidate_limit integer NOT NULL CHECK(candidate_limit BETWEEN 1 AND 2000),
    candidate_rows integer NOT NULL CHECK(candidate_rows BETWEEN 0 AND candidate_limit+1),
    unresolved_rows integer NOT NULL CHECK(unresolved_rows>=0),
    invisible_database_rows integer NOT NULL CHECK(invisible_database_rows>=0),
    excluded_rows integer NOT NULL CHECK(excluded_rows>=0),
    emitted_rows integer NOT NULL CHECK(emitted_rows>=0),
    unqualified_rows integer NOT NULL CHECK(unqualified_rows>=0),
    statement_rows integer NOT NULL CHECK(statement_rows BETWEEN 0 AND 2000),
    source_exhaustive boolean NOT NULL,
    qualified_exhaustive boolean NOT NULL,
    PRIMARY KEY(committed_at,run_id),
    CHECK(source_read_completed_at>=source_read_started_at
        AND source_read_completed_at-source_read_started_at<=interval '20 seconds'
        AND unresolved_rows::bigint+invisible_database_rows+excluded_rows+emitted_rows=candidate_rows
        AND statement_rows::bigint+unqualified_rows=least(emitted_rows,candidate_limit)
        AND source_exhaustive=(candidate_rows<=candidate_limit AND unresolved_rows=0 AND invisible_database_rows=0)
        AND qualified_exhaustive=(source_exhaustive AND unqualified_rows=0))
) PARTITION BY RANGE(committed_at);
CREATE INDEX ix_plan_cache_runtime_receipt_run ON events.plan_cache_runtime_receipt(instance_id,run_id);

ALTER TABLE events.plan_cache_runtime_watermark ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.plan_cache_runtime_watermark FORCE ROW LEVEL SECURITY;
CREATE POLICY plan_cache_runtime_watermark_scope ON events.plan_cache_runtime_watermark
    FOR ALL TO sqlobserver_migrator
    USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
ALTER TABLE events.plan_cache_runtime_delta ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.plan_cache_runtime_delta FORCE ROW LEVEL SECURITY;
CREATE POLICY plan_cache_runtime_delta_scope ON events.plan_cache_runtime_delta
    FOR ALL TO sqlobserver_migrator
    USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
ALTER TABLE events.plan_cache_runtime_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.plan_cache_runtime_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY plan_cache_runtime_receipt_scope ON events.plan_cache_runtime_receipt
    FOR ALL TO sqlobserver_migrator
    USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
REVOKE ALL ON events.plan_cache_runtime_watermark,events.plan_cache_runtime_delta,events.plan_cache_runtime_receipt
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER plan_cache_runtime_delta_append_only BEFORE UPDATE OR DELETE ON events.plan_cache_runtime_delta
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER plan_cache_runtime_receipt_append_only BEFORE UPDATE OR DELETE ON events.plan_cache_runtime_receipt
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

-- Registry and retention-policy extensions are explicit closed sets below.

CREATE FUNCTION control.ensure_plan_cache_runtime_partitions(p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET row_security=on
AS $partitions$
DECLARE v_parent text; v_day date; v_start timestamptz; v_end timestamptz;
    v_name text; v_child oid; v_created integer:=0;
BEGIN
    IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
        RAISE EXCEPTION 'Cache partition anchor must be the current UTC date' USING ERRCODE='22023'; END IF;
    FOREACH v_parent IN ARRAY ARRAY['plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt'] LOOP
        FOR v_day IN SELECT p_anchor+s.i FROM generate_series(-8,1) AS s(i) LOOP
            v_start:=v_day::timestamptz; v_end:=(v_day+1)::timestamptz;
            v_name:=v_parent||'_p'||to_char(v_day,'YYYYMMDD');
            v_child:=to_regclass(format('events.%I',v_name));
            IF v_child IS NULL THEN
                PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:cache-runtime:partition:'||v_name,0));
                IF to_regclass(format('events.%I',v_name)) IS NULL THEN
                    EXECUTE format('CREATE TABLE events.%I PARTITION OF events.%I FOR VALUES FROM (%L) TO (%L)',
                        v_name,v_parent,v_start,v_end);
                    EXECUTE format('ALTER TABLE events.%I ENABLE ROW LEVEL SECURITY',v_name);
                    EXECUTE format('ALTER TABLE events.%I FORCE ROW LEVEL SECURITY',v_name);
                    EXECUTE format('CREATE POLICY cache_runtime_scope ON events.%I FOR ALL TO sqlobserver_migrator
                        USING(instance_id::text=current_setting(%L,true)) WITH CHECK(instance_id::text=current_setting(%L,true))',
                        v_name,'sqlobserver.target_scope','sqlobserver.target_scope');
                    EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_name);
                    INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,
                        partition_granularity,range_start,range_end)
                    VALUES('events',v_parent,'events',v_name,'day',v_start,v_end);
                    v_created:=v_created+1;
                END IF;
                v_child:=to_regclass(format('events.%I',v_name));
            END IF;
            IF NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=v_child AND inhparent=format('events.%I',v_parent)::regclass)
                OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid=v_child AND relkind='r' AND relrowsecurity AND relforcerowsecurity
                    AND pg_get_userbyid(relowner)='sqlobserver_migrator'
                    AND pg_get_expr(relpartbound,oid)=format('FOR VALUES FROM (%L) TO (%L)',v_start,v_end))
                OR NOT EXISTS(SELECT 1 FROM system.partition_registry WHERE parent_schema='events' AND parent_table=v_parent
                    AND partition_schema='events' AND partition_name=v_name AND partition_granularity='day'
                    AND lifecycle_state='attached' AND range_start=v_start AND range_end=v_end)
                OR (SELECT count(*) FROM pg_policy WHERE polrelid=v_child)<>1
                OR NOT EXISTS(SELECT 1 FROM pg_policy child JOIN pg_policy parent
                    ON parent.polrelid=format('events.%I',v_parent)::regclass
                    WHERE child.polrelid=v_child AND child.polname='cache_runtime_scope'
                        AND child.polcmd='*' AND child.polpermissive
                        AND child.polroles=ARRAY['sqlobserver_migrator'::regrole::oid]
                        AND pg_get_expr(child.polqual,child.polrelid)=pg_get_expr(parent.polqual,parent.polrelid)
                        AND pg_get_expr(child.polwithcheck,child.polrelid)=pg_get_expr(parent.polwithcheck,parent.polrelid)) THEN
                RAISE EXCEPTION 'Cache partition identity or registry differs' USING ERRCODE='55000'; END IF;
        END LOOP;
    END LOOP;
    RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_plan_cache_runtime_partitions(date)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

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
   'availability_replica_evidence','availability_database_replica_evidence',
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline','deadlock_read_receipt','plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry' AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events' AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events' AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR (data_class='m7_query_performance' AND parent_schema='events' AND parent_table='query_performance_observation_v3' AND partition_granularity='day')
   OR (data_class='collection_run_original_copy' AND parent_schema='telemetry' AND parent_table='collection_run' AND partition_granularity='none')
   OR (data_class='m9_ag_replica_evidence' AND parent_schema='telemetry' AND parent_table='availability_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_ag_database_evidence' AND parent_schema='telemetry' AND parent_table='availability_database_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_backup_evidence' AND parent_schema='telemetry' AND parent_table='backup_v2_scan' AND partition_granularity='day')
   OR (data_class='sql_metric_baselines' AND parent_schema='analytics' AND parent_table='sql_metric_baseline' AND partition_granularity='day')
   OR (data_class='m6_deadlock_read_receipts' AND parent_schema='events' AND parent_table='deadlock_read_receipt' AND partition_granularity='month')
   OR (data_class='m7_cache_watermarks' AND parent_schema='events' AND parent_table='plan_cache_runtime_watermark' AND partition_granularity='day')
   OR (data_class='m7_cache_deltas' AND parent_schema='events' AND parent_table='plan_cache_runtime_delta' AND partition_granularity='day')
   OR (data_class='m7_cache_receipts' AND parent_schema='events' AND parent_table='plan_cache_runtime_receipt' AND partition_granularity='day')
   OR data_class IN ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking','m6_deadlocks',
     'm8_alert_history','m8_alert_delivery','m9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases','m10_host_metrics','m10_replication','m10_rollups','m10_evidence')));

INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('m7_cache_watermarks','events','plan_cache_runtime_watermark','day',true,interval '8 days',3,'migration-0316'),
('m7_cache_deltas','events','plan_cache_runtime_delta','day',true,interval '30 days',3,'migration-0316'),
('m7_cache_receipts','events','plan_cache_runtime_receipt','day',true,interval '30 days',3,'migration-0316');

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
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
    OR (p_data_class='m6_deadlock_read_receipts' AND (p_minimum_partitions<2 OR (p_enabled AND p_retain_for<interval '30 days')))
    OR (p_data_class='m7_cache_watermarks' AND p_enabled AND p_retain_for<interval '8 days')
    OR (p_data_class IN ('m7_cache_deltas','m7_cache_receipts') AND p_enabled AND p_retain_for<interval '30 days')
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

SELECT control.ensure_plan_cache_runtime_partitions(current_date);
