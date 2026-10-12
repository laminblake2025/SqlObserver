-- Resolve active scheduler runs through a separately gated partitioned lookup.
-- Existing installations continue to read the original writer by default.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_scheduler_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_scheduler_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_scheduler_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- The bridge supplies the partition key for a bounded v2 lookup by run ID.
CREATE FUNCTION telemetry.lookup_active_collection_run(p_run_id uuid)
RETURNS TABLE(work_key text,owner_execution_id uuid,fencing_token bigint)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,system SET TimeZone='UTC'
AS $lookup$
BEGIN
 IF p_run_id IS NULL THEN RETURN; END IF;
 IF (SELECT route.use_v2
     FROM system.collection_run_v2_scheduler_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY
  SELECT run.work_key,run.owner_execution_id,run.fencing_token
  FROM telemetry.collection_run_identity AS identity
  JOIN telemetry.collection_run_v2 AS run
    ON run.run_id=identity.run_id AND run.started_at=identity.started_at
  WHERE identity.run_id=p_run_id;
 ELSE
  RETURN QUERY
  SELECT run.work_key,run.owner_execution_id,run.fencing_token
  FROM telemetry.collection_run AS run
  WHERE run.run_id=p_run_id;
 END IF;
END $lookup$;
REVOKE ALL ON FUNCTION telemetry.lookup_active_collection_run(uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION telemetry.lookup_active_collection_run(uuid)
 TO sqlobserver_migrator;

-- The verifier retains the writer fence until this route update commits.
CREATE FUNCTION control.set_collection_run_v2_scheduler_read_route(
 p_use_v2 boolean,p_max_pairs integer DEFAULT 10000)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,system SET TimeZone='UTC'
AS $cutover$
DECLARE v_revision bigint;
BEGIN
 IF p_use_v2 IS NULL OR p_max_pairs IS NULL
    OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'scheduler read route bounds are invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'scheduler read route requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_scheduler_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_scheduler_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'scheduler read route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_scheduler_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_scheduler_read_route(boolean,integer)
 TO sqlobserver_migrator;

-- Preserve all scheduling, lease and lock predicates; change only active-run lookup.
CREATE OR REPLACE FUNCTION control.list_due_collector_work(p_max_items integer)
RETURNS TABLE(target_instance_id uuid,target_revision bigint,target_host_name text,target_instance_name text,target_tcp_port integer,target_certificate_host_name text,target_connect_timeout interval,target_authentication_mode text,target_transport_security_mode text,collector_id text,collector_version integer,output_schema_version integer,schedule_revision bigint,scheduled_at timestamptz,circuit_state text,consecutive_failure_count integer,circuit_open_until timestamptz,has_more boolean,repository_time timestamptz)
LANGUAGE sql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $due$
WITH repository_clock AS MATERIALIZED (SELECT clock_timestamp() AS value), due AS MATERIALIZED (
 SELECT target.instance_id,target.revision,target.host_name,target.instance_name,target.tcp_port,target.certificate_host_name,target.connect_timeout,target.authentication_mode,target.transport_security_mode,schedule.collector_id,schedule.collector_version,contract.output_schema_version,schedule.schedule_revision,schedule.next_due_at,
 CASE WHEN schedule.circuit_state='open' AND schedule.circuit_open_until<=repository_clock.value THEN 'half_open' ELSE schedule.circuit_state END AS effective_circuit_state,schedule.consecutive_failure_count,
 CASE WHEN schedule.circuit_state='open' AND schedule.circuit_open_until<=repository_clock.value THEN NULL::timestamptz ELSE schedule.circuit_open_until END AS effective_circuit_open_until,contract.execution_order,repository_clock.value AS repository_time
 FROM control.collector_schedule AS schedule JOIN control.collector_contract AS contract ON contract.collector_id=schedule.collector_id AND contract.collector_version=schedule.collector_version JOIN control.observation_target AS target ON target.instance_id=schedule.instance_id CROSS JOIN repository_clock
 LEFT JOIN LATERAL telemetry.lookup_active_collection_run(schedule.active_run_id) AS active_run ON true LEFT JOIN control.worker_lease AS active_lease ON active_lease.work_key=active_run.work_key AND active_lease.owner_execution_id=active_run.owner_execution_id AND active_lease.fencing_token=active_run.fencing_token AND active_lease.released_at IS NULL AND active_lease.expires_at>repository_clock.value
 WHERE p_max_items BETWEEN 1 AND 16 AND target.host_name IS NOT NULL AND target.lifecycle_state='active' AND schedule.target_revision=target.revision AND schedule.enabled AND schedule.next_due_at<=repository_clock.value AND (schedule.circuit_state<>'open' OR schedule.circuit_open_until<=repository_clock.value) AND (schedule.active_run_id IS NULL OR active_lease.work_key IS NULL)
   AND NOT EXISTS (SELECT 1 FROM control.collector_dependency AS dependency LEFT JOIN control.collector_schedule AS prerequisite ON prerequisite.instance_id=schedule.instance_id AND prerequisite.collector_id=dependency.prerequisite_collector_id AND prerequisite.collector_version=dependency.prerequisite_collector_version AND prerequisite.target_revision=schedule.target_revision AND prerequisite.enabled AND prerequisite.last_outcome IN ('succeeded','partial') WHERE dependency.collector_id=schedule.collector_id AND dependency.collector_version=schedule.collector_version AND prerequisite.instance_id IS NULL)
 ORDER BY schedule.next_due_at,contract.execution_order,target.instance_id LIMIT p_max_items+1)
SELECT due.instance_id,due.revision,due.host_name,due.instance_name,due.tcp_port,due.certificate_host_name,due.connect_timeout,due.authentication_mode,due.transport_security_mode,due.collector_id,due.collector_version,due.output_schema_version,due.schedule_revision,due.next_due_at,due.effective_circuit_state,due.consecutive_failure_count,due.effective_circuit_open_until,(SELECT count(*)>p_max_items FROM due),due.repository_time FROM due ORDER BY due.next_due_at,due.execution_order,due.instance_id LIMIT p_max_items;
$due$;

CREATE OR REPLACE FUNCTION control.claim_due_collector_work(p_owner uuid,p_ttl interval)
RETURNS TABLE(
 target_instance_id uuid,target_revision bigint,target_host_name text,target_instance_name text,
 target_tcp_port integer,target_certificate_host_name text,target_connect_timeout interval,
 target_authentication_mode text,target_transport_security_mode text,collector_id text,
 collector_version integer,output_schema_version integer,schedule_revision bigint,
 scheduled_at timestamptz,circuit_state text,consecutive_failure_count integer,
 circuit_open_until timestamptz,has_more boolean,repository_time timestamptz,
 fencing_token bigint,lease_acquired_at timestamptz,lease_renewed_at timestamptz,
 lease_expires_at timestamptz,lease_repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $$
DECLARE v_now timestamptz=clock_timestamp(); v_due record; v_lease record; v_key text;
BEGIN
 IF p_owner IS NULL OR p_owner='00000000-0000-0000-0000-000000000000'::uuid THEN
  RAISE EXCEPTION 'Collector owner is required' USING ERRCODE='22023';
 END IF;
 IF p_ttl IS NULL OR p_ttl<interval '5 seconds' OR p_ttl>interval '10 minutes' THEN
  RAISE EXCEPTION 'Collector lease TTL is out of range' USING ERRCODE='22023';
 END IF;

 SELECT target.instance_id,target.revision,target.host_name,target.instance_name,
  target.tcp_port,target.certificate_host_name,target.connect_timeout,
  target.authentication_mode,target.transport_security_mode,
  schedule.collector_id,schedule.collector_version,contract.output_schema_version,
  schedule.schedule_revision,schedule.next_due_at,
  CASE WHEN schedule.circuit_state='open' AND schedule.circuit_open_until<=v_now
   THEN 'half_open' ELSE schedule.circuit_state END AS effective_circuit_state,
  schedule.consecutive_failure_count,
  CASE WHEN schedule.circuit_state='open' AND schedule.circuit_open_until<=v_now
   THEN NULL::timestamptz ELSE schedule.circuit_open_until END AS effective_circuit_open_until,
  v_now AS repository_time
 INTO v_due
 FROM control.collector_schedule AS schedule
 JOIN control.collector_contract AS contract
  ON contract.collector_id=schedule.collector_id
  AND contract.collector_version=schedule.collector_version
 JOIN control.observation_target AS target ON target.instance_id=schedule.instance_id
 LEFT JOIN LATERAL telemetry.lookup_active_collection_run(schedule.active_run_id) AS active_run ON true
 LEFT JOIN control.worker_lease AS active_run_lease
  ON active_run_lease.work_key=active_run.work_key
  AND active_run_lease.owner_execution_id=active_run.owner_execution_id
  AND active_run_lease.fencing_token=active_run.fencing_token
  AND active_run_lease.released_at IS NULL AND active_run_lease.expires_at>v_now
 LEFT JOIN control.worker_lease AS dispatch_lease
  ON dispatch_lease.work_key='collector/run/'||schedule.collector_id||'/'||replace(schedule.instance_id::text,'-','')
 WHERE target.host_name IS NOT NULL AND target.lifecycle_state='active'
  AND schedule.target_revision=target.revision AND schedule.enabled
  AND schedule.next_due_at<=v_now
  AND (schedule.circuit_state<>'open' OR schedule.circuit_open_until<=v_now)
  AND (schedule.active_run_id IS NULL OR active_run_lease.work_key IS NULL)
  AND (dispatch_lease.work_key IS NULL OR dispatch_lease.released_at IS NOT NULL
   OR dispatch_lease.expires_at<=v_now)
  AND NOT EXISTS (
   SELECT 1 FROM control.collector_dependency AS dependency
   LEFT JOIN control.collector_schedule AS prerequisite
    ON prerequisite.instance_id=schedule.instance_id
    AND prerequisite.collector_id=dependency.prerequisite_collector_id
    AND prerequisite.collector_version=dependency.prerequisite_collector_version
    AND prerequisite.target_revision=schedule.target_revision
    AND prerequisite.enabled AND prerequisite.last_outcome IN ('succeeded','partial')
   WHERE dependency.collector_id=schedule.collector_id
    AND dependency.collector_version=schedule.collector_version
    AND prerequisite.instance_id IS NULL)
 ORDER BY schedule.next_due_at,contract.execution_order,target.instance_id
 LIMIT 1 FOR UPDATE OF schedule SKIP LOCKED;

 IF NOT FOUND THEN RETURN; END IF;
 v_key='collector/run/'||v_due.collector_id||'/'||replace(v_due.instance_id::text,'-','');
 -- begin_collection_run takes the lease lock before the schedule row. Never
 -- wait on that lock while holding the schedule row in this claim path.
 IF NOT pg_try_advisory_xact_lock(hashtextextended('sqlobserver:lease:'||v_key,0))
 THEN RETURN; END IF;
 SELECT * INTO v_lease FROM control.acquire_worker_lease(v_key,p_owner,p_ttl);
 IF NOT v_lease.acquired THEN RETURN; END IF;

 RETURN QUERY SELECT
  v_due.instance_id,v_due.revision,v_due.host_name,v_due.instance_name,
  v_due.tcp_port,v_due.certificate_host_name,v_due.connect_timeout,
  v_due.authentication_mode,v_due.transport_security_mode,
  v_due.collector_id,v_due.collector_version,v_due.output_schema_version,
  v_due.schedule_revision,v_due.next_due_at,v_due.effective_circuit_state,
  v_due.consecutive_failure_count,v_due.effective_circuit_open_until,
  false,v_due.repository_time,v_lease.fencing_token,v_lease.acquired_at,
  v_lease.renewed_at,v_lease.expires_at,v_lease.repository_time;
END $$;
