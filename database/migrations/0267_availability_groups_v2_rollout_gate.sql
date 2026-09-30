-- Stage AG v2 catalog and rollout gates. Activation and commit remain owner-only.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL idle_in_transaction_session_timeout = '5min';

INSERT INTO control.collector_contract
 (collector_id,collector_version,execution_order,manifest_schema_version,
  output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,
  minimum_interval,execution_timeout,maximum_rows,maximum_response_bytes,
  estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
VALUES ('availability-groups.health',2,13,6,2,
 decode('07e2282bb37ad54e173b34f65e0bdd29aa4ca6e9e8b1a956a33a5a41720d2c17','hex'),
 decode('88b4d627a492dfd738f11e6b6f72016a802c5c279d6302a2b1f88c3283a313cf','hex'),
 interval '30 seconds',interval '10 seconds',interval '5 seconds',2048,2097152,
 'moderate',2,3,interval '5 minutes');
INSERT INTO control.collector_dependency
 (collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES ('availability-groups.health',2,'engine.core',1);

CREATE TABLE control.availability_groups_v2_activation
(
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 active boolean NOT NULL DEFAULT false,
 activated_at timestamptz,
 CHECK (active=(activated_at IS NOT NULL))
);
INSERT INTO control.availability_groups_v2_activation(singleton) VALUES(true);
ALTER TABLE control.availability_groups_v2_activation ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.availability_groups_v2_activation FORCE ROW LEVEL SECURITY;
CREATE POLICY availability_groups_v2_activation_owner_only
 ON control.availability_groups_v2_activation FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON control.availability_groups_v2_activation
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE TABLE control.availability_groups_v2_worker_capability
(
 owner_execution_id uuid PRIMARY KEY,
 last_reconciled_at timestamptz NOT NULL
);
ALTER TABLE control.availability_groups_v2_worker_capability ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.availability_groups_v2_worker_capability FORCE ROW LEVEL SECURITY;
CREATE POLICY availability_groups_v2_worker_capability_owner_only
 ON control.availability_groups_v2_worker_capability FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON control.availability_groups_v2_worker_capability
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- Target writers and reconciliation take the AG gate before the inventory gate.
CREATE FUNCTION control.fence_availability_groups_v2_target()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control AS $target_fence$
BEGIN
 PERFORM 1 FROM control.availability_groups_v2_activation WHERE singleton FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'AG v2 activation gate is missing' USING ERRCODE='55000'; END IF;
 RETURN NEW;
END $target_fence$;
REVOKE ALL ON FUNCTION control.fence_availability_groups_v2_target()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER availability_groups_v2_target_fence
BEFORE INSERT OR UPDATE OF revision,lifecycle_state,host_name
ON control.observation_target FOR EACH ROW
EXECUTE FUNCTION control.fence_availability_groups_v2_target();

CREATE FUNCTION control.activate_availability_groups_v2_target()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $target_activate$
DECLARE desired_version integer;
BEGIN
 SELECT CASE WHEN active THEN 2 ELSE 1 END INTO desired_version
 FROM control.availability_groups_v2_activation WHERE singleton;
 IF desired_version IS NULL THEN
  RAISE EXCEPTION 'AG v2 activation gate is missing' USING ERRCODE='55000';
 END IF;
 UPDATE control.collector_schedule AS schedule
 SET collector_version=desired_version,schedule_revision=schedule.schedule_revision+1,
  next_due_at=clock_timestamp(),last_started_at=NULL,last_completed_at=NULL,
  last_succeeded_at=NULL,last_outcome=NULL,circuit_state='closed',
  consecutive_failure_count=0,circuit_open_until=NULL,updated_at=clock_timestamp()
 WHERE schedule.instance_id=NEW.instance_id AND schedule.collector_id='availability-groups.health'
   AND schedule.collector_version<>desired_version AND schedule.active_run_id IS NULL;
 RETURN NEW;
END $target_activate$;
REVOKE ALL ON FUNCTION control.activate_availability_groups_v2_target()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER zz_availability_groups_v2_target
AFTER INSERT OR UPDATE OF revision,lifecycle_state,host_name
ON control.observation_target FOR EACH ROW
EXECUTE FUNCTION control.activate_availability_groups_v2_target();

CREATE FUNCTION control.reconcile_collector_catalog_ag_v2
(
 p_collector_ids text[],p_collector_versions integer[],
 p_manifest_sha256 bytea[],p_asset_bundle_sha256 bytea[],
 p_execution_orders integer[],p_work_key text,
 p_owner_execution_id uuid,p_fencing_token bigint
)
RETURNS TABLE(inserted_count integer,updated_count integer,
 unchanged_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $ag_v2$
DECLARE
 legacy_versions integer[] := p_collector_versions;
 legacy_manifests bytea[] := p_manifest_sha256;
 legacy_bundles bytea[] := p_asset_bundle_sha256;
 now_utc timestamptz := clock_timestamp();
 legacy_contract record;
BEGIN
 IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile'
  OR cardinality(p_collector_ids) IS DISTINCT FROM 19
  OR cardinality(p_collector_versions) IS DISTINCT FROM 19
  OR cardinality(p_manifest_sha256) IS DISTINCT FROM 19
  OR cardinality(p_asset_bundle_sha256) IS DISTINCT FROM 19
  OR cardinality(p_execution_orders) IS DISTINCT FROM 19
  OR p_collector_ids[13] IS DISTINCT FROM 'availability-groups.health'
  OR p_collector_versions[13] IS DISTINCT FROM 2
  OR p_execution_orders[13] IS DISTINCT FROM 13
  OR p_manifest_sha256[13] IS DISTINCT FROM
   decode('07e2282bb37ad54e173b34f65e0bdd29aa4ca6e9e8b1a956a33a5a41720d2c17','hex')
  OR p_asset_bundle_sha256[13] IS DISTINCT FROM
   decode('88b4d627a492dfd738f11e6b6f72016a802c5c279d6302a2b1f88c3283a313cf','hex')
 THEN RAISE EXCEPTION 'AG v2 catalog must match the exact pinned contract' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 PERFORM 1 FROM control.availability_groups_v2_activation WHERE singleton FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'AG v2 activation gate is missing' USING ERRCODE='55000'; END IF;
 IF NOT EXISTS (SELECT 1 FROM control.collector_contract
  WHERE collector_id='availability-groups.health' AND collector_version=2
   AND execution_order=13 AND manifest_schema_version=6 AND output_schema_version=2
   AND manifest_sha256=p_manifest_sha256[13] AND asset_bundle_sha256=p_asset_bundle_sha256[13]
   AND default_interval=interval '30 seconds' AND minimum_interval=interval '10 seconds'
   AND execution_timeout=interval '5 seconds' AND maximum_rows=2048 AND maximum_response_bytes=2097152
   AND maximum_attempts=2 AND circuit_failure_threshold=3 AND circuit_open_interval=interval '5 minutes')
 THEN RAISE EXCEPTION 'AG v2 repository contract differs from pinned catalog' USING ERRCODE='55000'; END IF;
 IF (SELECT count(*) FROM system.retention_policy
  WHERE enabled AND partition_granularity='day' AND minimum_partitions_to_keep>=3
   AND (data_class,parent_schema,parent_table) IN
   (('m9_ag_replica_evidence','telemetry','availability_replica_evidence'),
    ('m9_ag_database_evidence','telemetry','availability_database_replica_evidence')))<>2
 THEN RAISE EXCEPTION 'AG v2 retention policies must be installed and enabled' USING ERRCODE='55000'; END IF;
 PERFORM control.ensure_availability_evidence_partitions(current_date);

 legacy_versions[13] := 1;
 SELECT manifest_sha256,asset_bundle_sha256 INTO legacy_contract
 FROM control.collector_contract WHERE collector_id='availability-groups.health' AND collector_version=1;
 legacy_manifests[13] := legacy_contract.manifest_sha256;
 legacy_bundles[13] := legacy_contract.asset_bundle_sha256;
 PERFORM control.reconcile_collector_catalog_m15(
  p_collector_ids,legacy_versions,legacy_manifests,legacy_bundles,
  p_execution_orders,p_work_key,p_owner_execution_id,p_fencing_token);

 PERFORM 1 FROM control.collector_schedule AS schedule
 WHERE schedule.collector_id='availability-groups.health'
 ORDER BY schedule.instance_id FOR UPDATE;
 IF EXISTS (SELECT 1 FROM control.collector_schedule
  WHERE collector_id='availability-groups.health' AND collector_version=1 AND active_run_id IS NOT NULL)
 THEN RAISE EXCEPTION 'AG v1 runs must drain before v2 activation' USING ERRCODE='55000'; END IF;
 UPDATE control.collector_schedule AS schedule
 SET collector_version=2,schedule_revision=schedule.schedule_revision+1,
  next_due_at=now_utc,last_started_at=NULL,last_completed_at=NULL,
  last_succeeded_at=NULL,last_outcome=NULL,circuit_state='closed',
  consecutive_failure_count=0,circuit_open_until=NULL,updated_at=now_utc
 WHERE schedule.collector_id='availability-groups.health' AND schedule.collector_version=1;
 UPDATE control.availability_groups_v2_activation
 SET active=true,activated_at=coalesce(activated_at,now_utc) WHERE singleton;
 INSERT INTO control.availability_groups_v2_worker_capability(owner_execution_id,last_reconciled_at)
 VALUES(p_owner_execution_id,clock_timestamp())
 ON CONFLICT(owner_execution_id) DO UPDATE SET last_reconciled_at=EXCLUDED.last_reconciled_at;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 0,0,19,now_utc;
END $ag_v2$;
REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_ag_v2(
 text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

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
 WHERE (schedule.collector_id<>'database.inventory' OR schedule.collector_version<>2
  OR EXISTS (SELECT 1 FROM control.inventory_v2_worker_capability AS capability
   WHERE capability.owner_execution_id=p_owner
     AND capability.last_reconciled_at<=v_now
     AND capability.last_reconciled_at>v_now-interval '5 minutes'))
  AND (schedule.collector_id<>'availability-groups.health' OR schedule.collector_version<>2
   OR EXISTS (SELECT 1 FROM control.availability_groups_v2_worker_capability AS capability
    WHERE capability.owner_execution_id=p_owner AND capability.last_reconciled_at<=v_now
      AND capability.last_reconciled_at>v_now-interval '5 minutes'))
  AND target.host_name IS NOT NULL AND target.lifecycle_state='active'
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
    AND (prerequisite.collector_version=dependency.prerequisite_collector_version OR EXISTS (
    SELECT 1 FROM control.collector_prerequisite_compatibility AS compatible
    WHERE compatible.collector_id=dependency.collector_id
      AND compatible.collector_version=dependency.collector_version
      AND compatible.prerequisite_collector_id=dependency.prerequisite_collector_id
      AND compatible.prerequisite_collector_version=dependency.prerequisite_collector_version
      AND compatible.acceptable_collector_version=prerequisite.collector_version))
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
