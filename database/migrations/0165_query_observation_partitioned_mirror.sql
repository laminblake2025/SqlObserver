-- Mirror new M7 observations into an interval-end partitioned successor.
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
   'query_store_group_watermark','query_store_group_delta'));

-- The old LIKE-based v2 scaffold partitions on observed_at and is not the
-- intended successor. A new table preserves the observation shape and uses
-- interval_end as both partition and primary-key time identity.
CREATE TABLE events.query_performance_observation_v3
 (LIKE events.query_performance_observation INCLUDING DEFAULTS INCLUDING CONSTRAINTS,
  PRIMARY KEY (interval_end,collection_run_id,database_id,query_fingerprint,observation_key),
  FOREIGN KEY (collection_run_id,database_id,query_fingerprint)
   REFERENCES events.query_performance_query(collection_run_id,database_id,query_fingerprint)
   ON DELETE CASCADE)
 PARTITION BY RANGE (interval_end);
ALTER TABLE events.query_performance_observation_v3
 ALTER COLUMN instance_id SET NOT NULL;
CREATE INDEX query_observation_v3_target_time
 ON events.query_performance_observation_v3(instance_id,interval_end DESC,
  collection_run_id,database_id,query_fingerprint);
ALTER TABLE events.query_performance_observation_v3 ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.query_performance_observation_v3 FORCE ROW LEVEL SECURITY;
CREATE POLICY query_observation_v3_migrator
 ON events.query_performance_observation_v3 FOR ALL TO sqlobserver_migrator
 USING (control.query_performance_target_claim(instance_id))
 WITH CHECK (control.query_performance_target_claim(instance_id));
REVOKE ALL ON events.query_performance_observation_v3
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER query_observation_v3_target_identity
 BEFORE INSERT ON events.query_performance_observation_v3 FOR EACH ROW
 EXECUTE FUNCTION events.set_query_observation_target_identity();
CREATE TRIGGER query_observation_v3_append_only
 BEFORE UPDATE OR DELETE ON events.query_performance_observation_v3 FOR EACH ROW
 EXECUTE FUNCTION events.reject_query_performance_mutation();

-- Register only exact attached days. A spoofed relation or stale registry row
-- fails rather than silently routing future observations elsewhere.
CREATE FUNCTION control.ensure_query_observation_v3_day(p_day date)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $day$
DECLARE
 v_name text;
 v_parent oid:='events.query_performance_observation_v3'::regclass;
 v_child oid;
 v_created boolean:=false;
BEGIN
 IF p_day IS NULL OR p_day<date '2000-01-01'
    OR p_day>(clock_timestamp() AT TIME ZONE 'UTC')::date+8 THEN
  RAISE EXCEPTION 'query observation partition day is outside supported history'
   USING ERRCODE='22023';
 END IF;
 v_name:=format('query_performance_observation_v3_p%s',to_char(p_day,'YYYYMMDD'));
 v_child:=to_regclass(format('events.%I',v_name));
 IF v_child IS NULL THEN
  PERFORM pg_advisory_xact_lock(hashtextextended(
   'sqlobserver:query-observation-v3:partition-set',0));
  v_child:=to_regclass(format('events.%I',v_name));
  IF v_child IS NULL THEN
   EXECUTE format('CREATE TABLE events.%I PARTITION OF events.query_performance_observation_v3 FOR VALUES FROM (%L) TO (%L)',
    v_name,p_day::timestamptz,(p_day+1)::timestamptz);
   EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_name);
   v_child:=to_regclass(format('events.%I',v_name));
   v_created:=true;
  END IF;
 END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_inherits
                WHERE inhrelid=v_child AND inhparent=v_parent) THEN
  RAISE EXCEPTION 'query observation partition name is occupied'
   USING ERRCODE='55000';
 END IF;
 INSERT INTO system.partition_registry
  (parent_schema,parent_table,partition_schema,partition_name,
   partition_granularity,range_start,range_end)
 VALUES('events','query_performance_observation_v3','events',v_name,
  'day',p_day::timestamptz,(p_day+1)::timestamptz)
 ON CONFLICT DO NOTHING;
 IF NOT EXISTS (SELECT 1 FROM system.partition_registry registry
   WHERE registry.parent_schema='events'
     AND registry.parent_table='query_performance_observation_v3'
     AND registry.partition_schema='events' AND registry.partition_name=v_name
     AND registry.partition_granularity='day'
     AND registry.range_start=p_day::timestamptz
     AND registry.range_end=(p_day+1)::timestamptz
     AND registry.lifecycle_state='attached') THEN
  RAISE EXCEPTION 'query observation registry differs from attached partition'
   USING ERRCODE='55000';
 END IF;
 RETURN v_created;
END $day$;
REVOKE ALL ON FUNCTION control.ensure_query_observation_v3_day(date)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.ensure_query_observation_v3_partitions(
 p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $partitions$
DECLARE
 v_day date;
 v_created integer:=0;
BEGIN
 IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'query observation partition anchor must be current UTC date'
   USING ERRCODE='22023';
 END IF;
 FOR v_day IN SELECT p_anchor+i FROM generate_series(-8,8) AS i LOOP
  IF control.ensure_query_observation_v3_day(v_day) THEN
   v_created:=v_created+1;
  END IF;
 END LOOP;
 RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_query_observation_v3_partitions(date)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_query_observation_v3_partitions(date)
 TO sqlobserver_collector;
SELECT control.ensure_query_observation_v3_partitions(current_date);

-- Mirror and canonical insert share one transaction. If the successor cannot
-- accept a row, the original run also rolls back with no hidden sample loss.
CREATE FUNCTION events.mirror_query_observation_v3()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,events,control SET TimeZone='UTC'
AS $mirror$
DECLARE
 v_instance_id uuid;
 v_day date;
 v_prior_scope text:=current_setting('sqlobserver.target_scope',true);
BEGIN
 IF EXISTS(SELECT 1 FROM inserted_rows WHERE instance_id IS NULL) THEN
  RAISE EXCEPTION 'query observation target identity is missing'
   USING ERRCODE='23514';
 END IF;
 -- Normal windows are pre-created. An older bounded source window receives
 -- its exact day here, so historical writes never fall into a catchall table.
 FOR v_day IN SELECT DISTINCT (interval_end AT TIME ZONE 'UTC')::date
              FROM inserted_rows ORDER BY 1 LOOP
  PERFORM control.ensure_query_observation_v3_day(v_day);
 END LOOP;
 -- Each canonical row was already checked by its target-identity trigger and
 -- the caller's RLS policy. Grouping keeps the mirror one insert per target,
 -- including for a migrator backfill statement spanning several targets.
 FOR v_instance_id IN SELECT DISTINCT instance_id FROM inserted_rows LOOP
  PERFORM set_config('sqlobserver.target_scope',v_instance_id::text,true);
  INSERT INTO events.query_performance_observation_v3
   (instance_id,collection_run_id,database_id,query_fingerprint,plan_fingerprint,
    observation_key,source,source_state,interval_start,interval_end,observed_at,
    semantics,cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed)
  SELECT row.instance_id,row.collection_run_id,row.database_id,
   row.query_fingerprint,row.plan_fingerprint,row.observation_key,
   row.source,row.source_state,row.interval_start,row.interval_end,row.observed_at,
   row.semantics,row.cpu_ms,row.duration_ms,row.execution_count,
   row.logical_reads,row.writes,row.rows_processed
  FROM inserted_rows AS row WHERE row.instance_id=v_instance_id;
 END LOOP;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_prior_scope,''),true);
 RETURN NULL;
END $mirror$;
REVOKE ALL ON FUNCTION events.mirror_query_observation_v3() FROM PUBLIC;
CREATE TRIGGER query_observation_v3_mirror
 AFTER INSERT ON events.query_performance_observation
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION events.mirror_query_observation_v3();
