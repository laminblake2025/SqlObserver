-- Build a UTC-day successor while the original run key remains canonical for
-- every existing FK. The old insert and new mirror commit or roll back together.
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
   'query_store_group_watermark','query_store_group_delta','collection_run_v2'));

CREATE TABLE telemetry.collection_run_v2
 (LIKE telemetry.collection_run INCLUDING DEFAULTS INCLUDING CONSTRAINTS,
  PRIMARY KEY (run_id,started_at),
  CONSTRAINT fk_collection_run_v2_target FOREIGN KEY (instance_id)
   REFERENCES control.observation_target(instance_id),
  CONSTRAINT fk_collection_run_v2_contract
   FOREIGN KEY (collector_id,collector_version)
   REFERENCES control.collector_contract(collector_id,collector_version))
 PARTITION BY RANGE(started_at);
CREATE INDEX ix_collection_run_v2_target_time
 ON telemetry.collection_run_v2(instance_id,collector_id,started_at DESC,run_id DESC);
ALTER TABLE telemetry.collection_run_v2 ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.collection_run_v2 FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_v2_migrator
 ON telemetry.collection_run_v2 FOR ALL TO sqlobserver_migrator
 USING(true) WITH CHECK(true);
REVOKE ALL ON telemetry.collection_run_v2
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_v2_append_only
 BEFORE UPDATE OR DELETE ON telemetry.collection_run_v2 FOR EACH STATEMENT
 EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION control.ensure_collection_run_v2_day(p_day date)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $day$
DECLARE
 v_name text;
 v_parent oid:='telemetry.collection_run_v2'::regclass;
 v_child oid;
 v_created boolean:=false;
BEGIN
 IF p_day IS NULL OR p_day<date '2000-01-01'
    OR p_day>(clock_timestamp() AT TIME ZONE 'UTC')::date+8 THEN
  RAISE EXCEPTION 'collection run partition day is outside supported history'
   USING ERRCODE='22023';
 END IF;
 v_name:=format('collection_run_v2_p%s',to_char(p_day,'YYYYMMDD'));
 v_child:=to_regclass(format('telemetry.%I',v_name));
 IF v_child IS NULL THEN
  PERFORM pg_advisory_xact_lock(hashtextextended(
   'sqlobserver:collection-run-v2:partition-set',0));
  v_child:=to_regclass(format('telemetry.%I',v_name));
  IF v_child IS NULL THEN
   EXECUTE format('CREATE TABLE telemetry.%I PARTITION OF telemetry.collection_run_v2 FOR VALUES FROM (%L) TO (%L)',
    v_name,p_day::timestamptz,(p_day+1)::timestamptz);
   EXECUTE format('REVOKE ALL ON telemetry.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_name);
   v_child:=to_regclass(format('telemetry.%I',v_name));
   v_created:=true;
  END IF;
 END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_inherits
                WHERE inhrelid=v_child AND inhparent=v_parent) THEN
  RAISE EXCEPTION 'collection run partition name is occupied'
   USING ERRCODE='55000';
 END IF;
 INSERT INTO system.partition_registry
  (parent_schema,parent_table,partition_schema,partition_name,
   partition_granularity,range_start,range_end)
 VALUES('telemetry','collection_run_v2','telemetry',v_name,
  'day',p_day::timestamptz,(p_day+1)::timestamptz)
 ON CONFLICT DO NOTHING;
 IF NOT EXISTS (SELECT 1 FROM system.partition_registry AS registry
   WHERE registry.parent_schema='telemetry'
     AND registry.parent_table='collection_run_v2'
     AND registry.partition_schema='telemetry'
     AND registry.partition_name=v_name
     AND registry.partition_granularity='day'
     AND registry.range_start=p_day::timestamptz
     AND registry.range_end=(p_day+1)::timestamptz
     AND registry.lifecycle_state='attached') THEN
  RAISE EXCEPTION 'collection run registry differs from attached partition'
   USING ERRCODE='55000';
 END IF;
 RETURN v_created;
END $day$;
REVOKE ALL ON FUNCTION control.ensure_collection_run_v2_day(date)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.ensure_collection_run_v2_partitions(
 p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $partitions$
DECLARE
 v_day date;
 v_created integer:=0;
BEGIN
 IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'collection run partition anchor must be current UTC date'
   USING ERRCODE='22023';
 END IF;
 FOR v_day IN SELECT p_anchor+i FROM generate_series(-8,8) AS i LOOP
  IF control.ensure_collection_run_v2_day(v_day) THEN
   v_created:=v_created+1;
  END IF;
 END LOOP;
 RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_collection_run_v2_partitions(date)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_collection_run_v2_partitions(date)
 TO sqlobserver_collector;
SELECT control.ensure_collection_run_v2_partitions(current_date);

CREATE FUNCTION telemetry.mirror_collection_run_v2()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,control SET TimeZone='UTC'
AS $mirror$
DECLARE
 v_day date;
 v_inserted integer;
 v_expected integer;
BEGIN
 FOR v_day IN
  SELECT DISTINCT (started_at AT TIME ZONE 'UTC')::date
  FROM inserted_rows ORDER BY 1 LOOP
  PERFORM control.ensure_collection_run_v2_day(v_day);
 END LOOP;
 INSERT INTO telemetry.collection_run_v2
  (run_id,instance_id,collector_id,collector_version,output_schema_version,
   target_revision,schedule_revision,work_key,owner_execution_id,
   fencing_token,request_digest,scheduled_for,started_at)
 SELECT run_id,instance_id,collector_id,collector_version,output_schema_version,
        target_revision,schedule_revision,work_key,owner_execution_id,
        fencing_token,request_digest,scheduled_for,started_at
 FROM inserted_rows;
 GET DIAGNOSTICS v_inserted=ROW_COUNT;
 SELECT count(*) INTO v_expected FROM inserted_rows;
 IF v_inserted<>v_expected THEN
  RAISE EXCEPTION 'collection run successor mirror count differs'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $mirror$;
REVOKE ALL ON FUNCTION telemetry.mirror_collection_run_v2()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_v2_mirror
 AFTER INSERT ON telemetry.collection_run
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION telemetry.mirror_collection_run_v2();
