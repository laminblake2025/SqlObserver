-- Catalog-v2 SQL baselines preserve the pinned v1 catalog and its foreign keys.
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
   'availability_replica_evidence','availability_database_replica_evidence',
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines'));
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
   OR (data_class='m9_backup_evidence' AND parent_schema='telemetry'
       AND parent_table='backup_v2_scan' AND partition_granularity='day')
   OR (data_class='sql_metric_baselines' AND parent_schema='analytics'
       AND parent_table='sql_metric_baseline' AND partition_granularity='day')
   OR data_class IN
    ('raw_metric_samples','m5_activity','m5_requests','m5_waits',
     'm5_blocking','m6_deadlocks','m8_alert_history','m8_alert_delivery',
     'm9_backup_status','m9_agent_history','m9_agent_failures',
     'm9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence')));


ALTER TABLE control.analytics_job DROP CONSTRAINT ck_sql_rollup_job_version;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_sql_rollup_job_version CHECK
 (source_catalog_version=1 OR source_catalog_version=2 AND coalesce(
   work_key='analytics/derivation' AND metric_key IN ('engine.user_connections','engine.process_physical_memory_bytes',
    'engine.os_available_memory_bytes','engine.memory_grants_pending','engine.scheduler_runnable_tasks',
    'engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')
   AND dimensions_hash=sha256(convert_to('{}','UTF8')) AND from_utc IS NOT NULL AND to_utc IS NOT NULL
   AND (job_kind='rollup' AND source_cadence_seconds BETWEEN 1 AND 86400
     AND source_cadence_seconds NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)
     AND to_utc>from_utc AND to_utc-from_utc<=interval '1 day'
    OR job_kind='baseline' AND source_cadence_seconds IS NULL AND rollup_interval IS NULL
     AND to_utc=date_trunc('day',to_utc) AND to_utc-from_utc=interval '28 days'),false));

CREATE TABLE analytics.sql_metric_baseline
 (
  instance_id uuid NOT NULL,target_revision bigint NOT NULL,metric_key text NOT NULL,
  window_end timestamptz NOT NULL,generation bigint NOT NULL,hour_of_week integer NOT NULL,
  catalog_version integer NOT NULL DEFAULT 2,catalog_checksum text NOT NULL,
  algorithm_version text NOT NULL DEFAULT 'sql-baseline-v2',source_algorithm_version text NOT NULL DEFAULT 'sql-rollup-v2',
  source_cutoff_utc timestamptz NOT NULL,result jsonb NOT NULL,computed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  PRIMARY KEY(window_end,instance_id,target_revision,metric_key,generation,hour_of_week,catalog_version),
  FOREIGN KEY(instance_id,target_revision) REFERENCES control.observation_target(instance_id,revision),
  CHECK(generation>0 AND target_revision>0 AND hour_of_week BETWEEN 0 AND 167 AND source_cutoff_utc>=window_end),
  CHECK(catalog_version=2 AND catalog_checksum='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
   AND algorithm_version='sql-baseline-v2' AND source_algorithm_version='sql-rollup-v2'),
  CHECK(metric_key IN ('engine.user_connections','engine.process_physical_memory_bytes','engine.os_available_memory_bytes',
   'engine.memory_grants_pending','engine.scheduler_runnable_tasks','engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')),
  CHECK(jsonb_typeof(result)='object' AND octet_length(result::text)<=16384)
 ) PARTITION BY RANGE(window_end);
ALTER TABLE analytics.sql_metric_baseline ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.sql_metric_baseline FORCE ROW LEVEL SECURITY;
CREATE POLICY sql_baseline_scope ON analytics.sql_metric_baseline
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
 WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY sql_baseline_owner ON analytics.sql_metric_baseline TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER sql_baseline_append_only BEFORE UPDATE OR DELETE ON analytics.sql_metric_baseline
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON analytics.sql_metric_baseline FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
 VALUES('sql_metric_baselines','analytics','sql_metric_baseline','day',true,interval '90 days',3,'migration-0294');

CREATE FUNCTION reporting.read_sql_baseline_inputs(p_instance uuid,p_revision bigint,p_metric text,
 p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,p_generation bigint)
RETURNS SETOF analytics.metric_rollup_v2
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE n integer; bytes bigint;
BEGIN
 IF p_instance IS NULL OR p_revision IS NULL OR p_revision<1 OR p_from IS NULL OR p_to IS NULL OR p_cutoff IS NULL
 OR p_generation IS NULL OR p_generation<1 OR p_to-p_from<>interval '28 days' OR p_to<>date_trunc('day',p_to) OR p_cutoff<p_to
 OR p_metric IS NULL OR p_metric NOT IN ('engine.user_connections','engine.process_physical_memory_bytes','engine.os_available_memory_bytes',
   'engine.memory_grants_pending','engine.scheduler_runnable_tasks','engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')
 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
 THEN RAISE EXCEPTION 'SQL baseline input bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.resolve_m10_target_revision(p_instance,p_revision);
 SELECT count(*),coalesce(sum(octet_length(row_to_json(r)::text)),0) INTO n,bytes FROM analytics.metric_rollup_v2 r
 WHERE r.instance_id=p_instance AND r.target_revision=p_revision AND r.metric_key=p_metric AND r.rollup_interval='hour'
  AND r.aggregation='avg' AND r.dimensions='{}'::jsonb AND r.dimension_hash=sha256(convert_to('{}','UTF8'))
  AND r.catalog_version=2 AND r.catalog_checksum='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
  AND r.algorithm_version='sql-rollup-v2' AND r.generation=p_generation AND r.bucket_start>=p_from AND r.bucket_start<p_to
  AND r.source_cutoff_utc<=p_cutoff AND r.computed_at<=p_cutoff;
 IF n>672 OR bytes>8388608 THEN RAISE EXCEPTION 'SQL baseline input exceeds its hard bound' USING ERRCODE='22023'; END IF;
 RETURN QUERY SELECT r.* FROM analytics.metric_rollup_v2 r
 WHERE r.instance_id=p_instance AND r.target_revision=p_revision AND r.metric_key=p_metric AND r.rollup_interval='hour'
  AND r.aggregation='avg' AND r.dimensions='{}'::jsonb AND r.dimension_hash=sha256(convert_to('{}','UTF8'))
  AND r.catalog_version=2 AND r.catalog_checksum='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
  AND r.algorithm_version='sql-rollup-v2' AND r.generation=p_generation AND r.bucket_start>=p_from AND r.bucket_start<p_to
  AND r.source_cutoff_utc<=p_cutoff AND r.computed_at<=p_cutoff ORDER BY r.bucket_start;
END $$;
REVOKE ALL ON FUNCTION reporting.read_sql_baseline_inputs(uuid,bigint,text,timestamptz,timestamptz,timestamptz,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_baseline_inputs(uuid,bigint,text,timestamptz,timestamptz,timestamptz,bigint) TO sqlobserver_collector;

CREATE FUNCTION control.schedule_sql_metric_baseline_jobs(p_owner uuid,p_fence bigint)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE now_utc timestamptz:=clock_timestamp(); cutoff timestamptz:=date_trunc('day',now_utc); n integer;
BEGIN
 PERFORM control.assert_worker_lease('analytics/derivation',p_owner,p_fence);
 WITH due AS (
  SELECT t.instance_id,t.revision,r.metric_key,
   left(encode(sha256(convert_to('sql-baseline-v2|'||t.instance_id::text||'|'||t.revision::text||'|'||r.metric_key||'|'||cutoff::text,'UTF8')),'hex'),32)::uuid AS id
  FROM control.observation_target t JOIN control.collector_schedule s ON s.instance_id=t.instance_id
  JOIN analytics.metric_rollup_v2 r ON r.instance_id=t.instance_id AND r.target_revision=t.revision
  WHERE t.lifecycle_state='active' AND s.collector_id='engine.core' AND s.enabled
   AND r.bucket_start=cutoff-interval '1 hour' AND r.rollup_interval='hour' AND r.aggregation='avg' AND r.generation=1
   AND r.catalog_version=2 AND r.catalog_checksum='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
   AND r.algorithm_version='sql-rollup-v2' AND r.dimensions='{}'::jsonb AND r.source_cutoff_utc<=now_utc AND r.computed_at<=now_utc
 ), bounded AS (
  SELECT d.* FROM due d WHERE NOT EXISTS(SELECT 1 FROM control.analytics_job j WHERE j.job_id=d.id)
  ORDER BY d.instance_id,d.metric_key LIMIT 896
 )
 INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,
  generation,status,work_key,dimensions_hash,source_catalog_version)
 SELECT d.id,'baseline',d.instance_id,d.revision,cutoff-interval '28 days',cutoff,d.metric_key,now_utc,
  1,'queued','analytics/derivation',sha256(convert_to('{}','UTF8')),2 FROM bounded d ON CONFLICT(job_id) DO NOTHING;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $$;
REVOKE ALL ON FUNCTION control.schedule_sql_metric_baseline_jobs(uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.schedule_sql_metric_baseline_jobs(uuid,bigint) TO sqlobserver_collector;

ALTER FUNCTION analytics.commit_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) RENAME TO commit_metric_baselines_catalog_v1;
REVOKE ALL ON FUNCTION analytics.commit_metric_baselines_catalog_v1(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION analytics.commit_metric_baselines(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,
 p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_rows jsonb,p_result_digest bytea)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.source_catalog_version=1)
 THEN RAISE EXCEPTION 'Catalog-v1 baseline job required' USING ERRCODE='40001'; END IF;
 RETURN analytics.commit_metric_baselines_catalog_v1(p_operation_id,p_job_id,p_instance_id,p_target_revision,p_work_key,
  p_owner_execution_id,p_fencing_token,p_request_digest,p_rows,p_result_digest);
END $$;
REVOKE ALL ON FUNCTION analytics.commit_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION analytics.commit_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;

CREATE FUNCTION analytics.commit_sql_metric_baselines(p_operation uuid,p_job uuid,p_instance uuid,p_revision bigint,
 p_work text,p_owner uuid,p_fence bigint,p_request bytea,p_rows jsonb,p_result bytea)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE job control.analytics_job%ROWTYPE; x jsonb; summary text; days integer; samples integer; v_partition_name text; n integer;
BEGIN
 IF p_operation IS NULL OR p_job IS NULL OR p_instance IS NULL OR p_revision IS NULL OR p_revision<1
 OR p_work IS DISTINCT FROM 'analytics/derivation' OR p_owner IS NULL OR p_fence IS NULL OR p_fence<1
 OR p_request IS NULL OR octet_length(p_request)<>32 OR p_result IS NULL OR octet_length(p_result)<>32
 OR p_rows IS NULL OR jsonb_typeof(p_rows)<>'array' OR jsonb_array_length(p_rows)<>168 OR octet_length(p_rows::text)>8388608
 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
 THEN RAISE EXCEPTION 'SQL baseline write bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work,p_owner,p_fence); PERFORM control.resolve_m10_target_revision(p_instance,p_revision);
 SELECT j.* INTO job FROM control.analytics_job j WHERE j.job_id=p_job AND j.instance_id=p_instance AND j.target_revision=p_revision
  AND j.job_kind='baseline' AND j.source_catalog_version=2 AND j.status='running' AND j.work_key=p_work
  AND j.owner_execution_id=p_owner AND j.fencing_token=p_fence FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'SQL baseline job fence conflict' USING ERRCODE='40001'; END IF;
 IF (SELECT count(DISTINCT (v->>'hourOfWeek')::integer) FROM jsonb_array_elements(p_rows) v)<>168
 THEN RAISE EXCEPTION 'SQL baseline hours must be unique and complete' USING ERRCODE='22023'; END IF;
 FOR x IN SELECT v FROM jsonb_array_elements(p_rows) v LOOP
  days:=(x->>'completeDays')::integer; samples:=(x->>'sampleCount')::integer;
  IF NOT coalesce(jsonb_typeof(x)='object' AND (x->>'catalogVersion')::integer=2
   AND x->>'catalogChecksum'='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
   AND x->>'algorithmVersion'='sql-baseline-v2' AND x->>'sourceAlgorithmVersion'='sql-rollup-v2'
   AND x->>'metricKey'=job.metric_key AND (x->>'generation')::bigint=job.generation
   AND (x->>'windowStartUtc')::timestamptz=job.from_utc AND (x->>'windowEndUtc')::timestamptz=job.to_utc
   AND (x->>'sourceCutoffUtc')::timestamptz=job.source_cutoff_utc AND x->'dimensions'='{}'::jsonb
   AND decode(x->>'dimensionHash','hex')=sha256(convert_to('{}','UTF8')) AND (x->>'hourOfWeek')::integer BETWEEN 0 AND 167
   AND days BETWEEN 0 AND 28 AND samples BETWEEN 0 AND 4
   AND (x->>'coverage')::double precision=days::double precision/28
   AND (x->>'confidence')::double precision=(x->>'coverage')::double precision*samples::double precision/8
   AND x->>'visibilityState'=CASE WHEN samples=0 THEN 'unavailable' WHEN days=28 THEN 'complete' ELSE 'partial' END
   AND x->>'mean' IS NULL AND x->>'stddev' IS NULL AND x->>'lowerBound' IS NULL AND x->>'upperBound' IS NULL,false)
  THEN RAISE EXCEPTION 'SQL baseline immutable identity or coverage rejected' USING ERRCODE='22023'; END IF;
  FOREACH summary IN ARRAY ARRAY['median','mad','p10','p90'] LOOP
   IF samples=0 AND x->>summary IS NOT NULL OR samples>0 AND (x->>summary IS NULL
    OR (x->>summary)::double precision<0 OR (x->>summary)::double precision IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
   THEN RAISE EXCEPTION 'SQL baseline finite summary rejected' USING ERRCODE='22023'; END IF;
  END LOOP;
  IF (x->>'p10')::double precision>(x->>'median')::double precision OR (x->>'median')::double precision>(x->>'p90')::double precision
  THEN RAISE EXCEPTION 'SQL baseline percentile order rejected' USING ERRCODE='22023'; END IF;
 END LOOP;
 IF NOT control.record_m10_analytics_replay(p_operation,'baseline',p_job,p_instance,p_revision,p_work,p_owner,p_fence,p_request,p_result,jsonb_build_object('rows',p_rows)) THEN RETURN 0; END IF;
 IF EXISTS(SELECT 1 FROM analytics.sql_metric_baseline b WHERE b.instance_id=p_instance AND b.target_revision=p_revision
   AND b.metric_key=job.metric_key AND b.window_end=job.to_utc AND b.generation=job.generation) THEN
  IF (SELECT count(*) FROM analytics.sql_metric_baseline b WHERE b.instance_id=p_instance AND b.target_revision=p_revision
    AND b.metric_key=job.metric_key AND b.window_end=job.to_utc AND b.generation=job.generation)<>168
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_rows) v JOIN analytics.sql_metric_baseline b
    ON b.instance_id=p_instance AND b.target_revision=p_revision AND b.metric_key=job.metric_key AND b.window_end=job.to_utc
     AND b.generation=job.generation AND b.hour_of_week=(v->>'hourOfWeek')::integer WHERE b.result IS DISTINCT FROM v)
  THEN RAISE EXCEPTION 'SQL baseline identity has divergent content' USING ERRCODE='40001'; END IF;
  RETURN 0;
 END IF;
 v_partition_name:='sql_metric_baseline_p'||to_char(job.to_utc,'YYYYMMDD');
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:sql-baseline-day:'||job.to_utc::date::text,0));
 EXECUTE format('CREATE TABLE IF NOT EXISTS analytics.%I PARTITION OF analytics.sql_metric_baseline FOR VALUES FROM (%L) TO (%L)',
  v_partition_name,job.to_utc,job.to_utc+interval '1 day');
 IF NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_inherits i ON i.inhrelid=c.oid
  WHERE c.oid=to_regclass(format('analytics.%I',v_partition_name)) AND i.inhparent='analytics.sql_metric_baseline'::regclass
   AND pg_get_userbyid(c.relowner)='sqlobserver_migrator' AND c.relkind='r'
   AND pg_get_expr(c.relpartbound,c.oid)=format('FOR VALUES FROM (%L) TO (%L)',job.to_utc,job.to_utc+interval '1 day'))
 THEN RAISE EXCEPTION 'SQL baseline partition identity differs' USING ERRCODE='55000'; END IF;
 EXECUTE format('REVOKE ALL ON analytics.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_partition_name);
 INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
 VALUES('analytics','sql_metric_baseline','analytics',v_partition_name,'day',job.to_utc,job.to_utc+interval '1 day') ON CONFLICT DO NOTHING;
 IF NOT EXISTS(SELECT 1 FROM system.partition_registry r WHERE r.parent_schema='analytics' AND r.parent_table='sql_metric_baseline'
  AND r.partition_schema='analytics' AND r.partition_name=v_partition_name AND r.range_start=job.to_utc
  AND r.range_end=job.to_utc+interval '1 day' AND r.lifecycle_state='attached')
 THEN RAISE EXCEPTION 'SQL baseline partition registry differs' USING ERRCODE='55000'; END IF;
 INSERT INTO analytics.sql_metric_baseline(instance_id,target_revision,metric_key,window_end,generation,hour_of_week,
  catalog_checksum,source_cutoff_utc,result)
 SELECT p_instance,p_revision,job.metric_key,job.to_utc,job.generation,(v->>'hourOfWeek')::integer,v->>'catalogChecksum',job.source_cutoff_utc,v
 FROM jsonb_array_elements(p_rows) v;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $$;
REVOKE ALL ON FUNCTION analytics.commit_sql_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION analytics.commit_sql_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;

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
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines')
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
