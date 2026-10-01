-- Activate the qualified incremental deadlock source through a fenced catalog rollout.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL idle_in_transaction_session_timeout = '5min';

INSERT INTO control.collector_contract
SELECT (jsonb_populate_record(NULL::control.collector_contract,to_jsonb(c)||jsonb_build_object(
    'collector_version',2,'output_schema_version',2,'maximum_rows',8193,
    'manifest_sha256',decode('1530586d171d1b0300a83b8451c60a1f4a5ae25df7279f3815b7d43702b03c1b','hex'),
    'asset_bundle_sha256',decode('efcd15c53d0a75b30277446730f774dc40ffe6fa4ff78c1435967de56bc954fa','hex')))).*
FROM control.collector_contract c WHERE c.collector_id='deadlocks.system-health' AND c.collector_version=1
    AND c.manifest_sha256=decode('59cfabc2a63efa7236e61170f6ad1e3afee79e5dd79e4367ed90e49fb18214b8','hex')
    AND c.asset_bundle_sha256=decode('21c5911e826f945833b74b128ae499464f494cf5f0be4a59ba042d451e1ebd8d','hex');
DO $installed$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM control.collector_contract WHERE collector_id='deadlocks.system-health' AND collector_version=2)
    THEN RAISE EXCEPTION 'The approved deadlock v1 predecessor is required' USING ERRCODE='55000'; END IF;
END $installed$;
INSERT INTO control.collector_dependency
SELECT collector_id,2,prerequisite_collector_id,prerequisite_collector_version FROM control.collector_dependency
    WHERE collector_id='deadlocks.system-health' AND collector_version=1;

CREATE TABLE control.deadlocks_v2_activation
(
    singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
    active boolean NOT NULL DEFAULT false, activated_at timestamptz,
    CHECK(active=(activated_at IS NOT NULL))
);
INSERT INTO control.deadlocks_v2_activation(singleton) VALUES(true);
CREATE TABLE control.deadlocks_v2_worker_capability
(
    owner_execution_id uuid PRIMARY KEY,
    last_reconciled_at timestamptz NOT NULL
);
ALTER TABLE control.deadlocks_v2_activation ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.deadlocks_v2_activation FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlocks_v2_activation_owner_only ON control.deadlocks_v2_activation
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
ALTER TABLE control.deadlocks_v2_worker_capability ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.deadlocks_v2_worker_capability FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlocks_v2_worker_capability_owner_only ON control.deadlocks_v2_worker_capability
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON control.deadlocks_v2_activation,control.deadlocks_v2_worker_capability
    FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.fence_deadlocks_v2_target() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog AS $fence$
BEGIN
    PERFORM 1 FROM control.deadlocks_v2_activation WHERE singleton FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock v2 activation gate is missing' USING ERRCODE='55000'; END IF;
    RETURN NEW;
END $fence$;
REVOKE ALL ON FUNCTION control.fence_deadlocks_v2_target() FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER deadlocks_v2_target_fence BEFORE INSERT OR UPDATE OF revision,lifecycle_state,host_name
    ON control.observation_target FOR EACH ROW EXECUTE FUNCTION control.fence_deadlocks_v2_target();

CREATE FUNCTION control.activate_deadlocks_v2_target() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $route$
DECLARE desired integer;
BEGIN
    SELECT CASE WHEN active THEN 2 ELSE 1 END INTO desired FROM control.deadlocks_v2_activation WHERE singleton;
    IF desired IS NULL THEN RAISE EXCEPTION 'Deadlock v2 activation gate is missing' USING ERRCODE='55000'; END IF;
    UPDATE control.collector_schedule s SET collector_version=desired,schedule_revision=s.schedule_revision+1,
        next_due_at=clock_timestamp(),last_started_at=NULL,last_completed_at=NULL,last_succeeded_at=NULL,last_outcome=NULL,
        circuit_state='closed',consecutive_failure_count=0,circuit_open_until=NULL,updated_at=clock_timestamp()
    WHERE s.instance_id=NEW.instance_id AND s.collector_id='deadlocks.system-health'
        AND s.collector_version<>desired AND s.active_run_id IS NULL;
    RETURN NEW;
END $route$;
REVOKE ALL ON FUNCTION control.activate_deadlocks_v2_target() FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER zz_deadlocks_v2_target AFTER INSERT OR UPDATE OF revision,lifecycle_state,host_name
    ON control.observation_target FOR EACH ROW EXECUTE FUNCTION control.activate_deadlocks_v2_target();

CREATE FUNCTION control.reconcile_collector_catalog_deadlocks_v2(
    p_collector_ids text[],p_collector_versions integer[],p_manifest_sha256 bytea[],p_asset_bundle_sha256 bytea[],
    p_execution_orders integer[],p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS TABLE(inserted_count integer,updated_count integer,unchanged_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $catalog$
DECLARE versions integer[]:=p_collector_versions; manifests bytea[]:=p_manifest_sha256; bundles bytea[]:=p_asset_bundle_sha256;
    legacy control.collector_contract%ROWTYPE; candidate record; abandoned record; now_utc timestamptz; key text;
BEGIN
    IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile'
        OR cardinality(p_collector_ids) IS DISTINCT FROM 19 OR array_lower(p_collector_ids,1) IS DISTINCT FROM 1 OR array_ndims(p_collector_ids) IS DISTINCT FROM 1
        OR cardinality(p_collector_versions) IS DISTINCT FROM 19 OR array_lower(p_collector_versions,1) IS DISTINCT FROM 1 OR array_ndims(p_collector_versions) IS DISTINCT FROM 1
        OR cardinality(p_manifest_sha256) IS DISTINCT FROM 19 OR array_lower(p_manifest_sha256,1) IS DISTINCT FROM 1 OR array_ndims(p_manifest_sha256) IS DISTINCT FROM 1
        OR cardinality(p_asset_bundle_sha256) IS DISTINCT FROM 19 OR array_lower(p_asset_bundle_sha256,1) IS DISTINCT FROM 1 OR array_ndims(p_asset_bundle_sha256) IS DISTINCT FROM 1
        OR cardinality(p_execution_orders) IS DISTINCT FROM 19 OR array_lower(p_execution_orders,1) IS DISTINCT FROM 1 OR array_ndims(p_execution_orders) IS DISTINCT FROM 1
        OR p_collector_ids[8] IS DISTINCT FROM 'deadlocks.system-health' OR p_collector_versions[8] IS DISTINCT FROM 2
        OR p_execution_orders[8] IS DISTINCT FROM 8
        OR p_collector_versions[10] IS DISTINCT FROM 1 OR p_collector_versions[13] IS DISTINCT FROM 1
        OR p_manifest_sha256[8] IS DISTINCT FROM decode('1530586d171d1b0300a83b8451c60a1f4a5ae25df7279f3815b7d43702b03c1b','hex')
        OR p_asset_bundle_sha256[8] IS DISTINCT FROM decode('efcd15c53d0a75b30277446730f774dc40ffe6fa4ff78c1435967de56bc954fa','hex')
    THEN RAISE EXCEPTION 'Deadlock v2 catalog must match the exact pinned contract' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    -- Match target-trigger lock order before delegating to older rollout layers.
    PERFORM 1 FROM control.availability_groups_v2_activation WHERE singleton FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'AG activation gate is missing' USING ERRCODE='55000'; END IF;
    PERFORM 1 FROM control.backups_v2_activation WHERE singleton FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Backup activation gate is missing' USING ERRCODE='55000'; END IF;
    PERFORM 1 FROM control.deadlocks_v2_activation WHERE singleton FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock v2 activation gate is missing' USING ERRCODE='55000'; END IF;
    IF NOT EXISTS(SELECT 1 FROM control.collector_contract WHERE collector_id='deadlocks.system-health' AND collector_version=2
        AND execution_order=8 AND manifest_schema_version=4 AND output_schema_version=2
        AND manifest_sha256=p_manifest_sha256[8] AND asset_bundle_sha256=p_asset_bundle_sha256[8]
        AND default_interval=interval '30 seconds' AND minimum_interval=interval '10 seconds' AND execution_timeout=interval '20 seconds'
        AND maximum_rows=8193 AND maximum_response_bytes=1048576 AND estimated_cost='moderate'
        AND maximum_attempts=2 AND circuit_failure_threshold=3 AND circuit_open_interval=interval '5 minutes')
    THEN RAISE EXCEPTION 'Deadlock v2 repository contract differs from the approved bounds' USING ERRCODE='55000'; END IF;
    IF NOT EXISTS(SELECT 1 FROM system.retention_policy WHERE data_class='m6_deadlock_read_receipts' AND enabled
        AND parent_schema='events' AND parent_table='deadlock_read_receipt' AND partition_granularity='month'
        AND retain_for>=interval '30 days' AND minimum_partitions_to_keep>=2)
    THEN RAISE EXCEPTION 'Deadlock v2 receipt retention must be installed and enabled' USING ERRCODE='55000'; END IF;
    IF EXISTS((SELECT prerequisite_collector_id,prerequisite_collector_version FROM control.collector_dependency
        WHERE collector_id='deadlocks.system-health' AND collector_version=1 EXCEPT SELECT prerequisite_collector_id,prerequisite_collector_version
        FROM control.collector_dependency WHERE collector_id='deadlocks.system-health' AND collector_version=2)
        UNION ALL (SELECT prerequisite_collector_id,prerequisite_collector_version FROM control.collector_dependency
        WHERE collector_id='deadlocks.system-health' AND collector_version=2 EXCEPT SELECT prerequisite_collector_id,prerequisite_collector_version
        FROM control.collector_dependency WHERE collector_id='deadlocks.system-health' AND collector_version=1))
    THEN RAISE EXCEPTION 'Deadlock v2 dependencies differ from the approved predecessor' USING ERRCODE='55000'; END IF;
    SELECT * INTO legacy FROM control.collector_contract WHERE collector_id='deadlocks.system-health' AND collector_version=1;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock v1 contract is missing' USING ERRCODE='55000'; END IF;
    versions[8]:=1; manifests[8]:=legacy.manifest_sha256; bundles[8]:=legacy.asset_bundle_sha256;
    -- Backup and AG v2 retain their separately restricted activation paths.
    PERFORM control.reconcile_collector_catalog_m15(p_collector_ids,versions,manifests,bundles,p_execution_orders,p_work_key,p_owner_execution_id,p_fencing_token);
    FOR candidate IN SELECT s.* FROM control.collector_schedule s WHERE s.collector_id='deadlocks.system-health'
        ORDER BY s.instance_id FOR UPDATE LOOP
        IF candidate.collector_version NOT IN (1,2) THEN RAISE EXCEPTION 'Unexpected deadlock schedule version' USING ERRCODE='55000'; END IF;
        IF candidate.collector_version=2 THEN CONTINUE; END IF;
        key:='collector/run/deadlocks.system-health/'||replace(candidate.instance_id::text,'-','');
        -- Begin takes this advisory lease lock before its schedule lock. Do not
        -- wait while holding a schedule row; abort the whole rollout instead.
        IF NOT pg_try_advisory_xact_lock(hashtextextended('sqlobserver:lease:'||key,0))
        THEN RAISE EXCEPTION 'Deadlock v1 work is concurrently acquiring its lease' USING ERRCODE='55000'; END IF;
        now_utc:=clock_timestamp();
        IF EXISTS(SELECT 1 FROM control.worker_lease l WHERE l.work_key=key AND l.released_at IS NULL AND l.expires_at>now_utc)
        THEN RAISE EXCEPTION 'Live deadlock v1 work must drain before v2 activation' USING ERRCODE='55000'; END IF;
        IF candidate.active_run_id IS NOT NULL THEN
            SELECT r.* INTO abandoned FROM telemetry.lookup_collection_run_runtime(candidate.active_run_id) r;
            IF abandoned.run_id IS DISTINCT FROM candidate.active_run_id OR abandoned.instance_id IS DISTINCT FROM candidate.instance_id
                OR abandoned.collector_id IS DISTINCT FROM 'deadlocks.system-health' OR abandoned.collector_version IS DISTINCT FROM 1
                OR abandoned.output_schema_version IS DISTINCT FROM 1 OR abandoned.target_revision IS DISTINCT FROM candidate.target_revision
                OR abandoned.schedule_revision IS DISTINCT FROM candidate.schedule_revision OR abandoned.work_key IS DISTINCT FROM key
                OR octet_length(abandoned.request_digest) IS DISTINCT FROM 32
            THEN RAISE EXCEPTION 'Deadlock schedule references an invalid abandoned v1 run' USING ERRCODE='55000'; END IF;
            IF NOT EXISTS(SELECT 1 FROM telemetry.collection_run_outcome o WHERE o.run_id=abandoned.run_id) THEN
                -- Preserve the canonical begin path's lease-loss outcome and gap.
                INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                    source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,
                    persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
                VALUES(abandoned.run_id,'lease_lost','lease_ownership_lost',1,0,
                    least(3600000,greatest(0,floor(extract(epoch FROM(now_utc-abandoned.started_at))*1000)::bigint)),
                    0,0,0,0,0,0,0,0,false,false,'none',true,0,0,abandoned.request_digest,now_utc);
                INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,
                    lost_row_count,lost_byte_count,count_is_exact,recorded_at)
                VALUES(gen_random_uuid(),abandoned.run_id,candidate.instance_id,'deadlocks.system-health','lease_ownership_lost',
                    abandoned.scheduled_for,now_utc,1,0,false,now_utc);
            END IF;
        END IF;
        UPDATE control.collector_schedule s SET collector_version=2,schedule_revision=s.schedule_revision+1,active_run_id=NULL,
            next_due_at=now_utc,last_started_at=NULL,last_completed_at=NULL,last_succeeded_at=NULL,last_outcome=NULL,
            circuit_state='closed',consecutive_failure_count=0,circuit_open_until=NULL,updated_at=now_utc
        WHERE s.instance_id=candidate.instance_id AND s.collector_id='deadlocks.system-health';
    END LOOP;
    now_utc:=clock_timestamp();
    UPDATE control.deadlocks_v2_activation SET active=true,activated_at=coalesce(activated_at,now_utc) WHERE singleton;
    INSERT INTO control.deadlocks_v2_worker_capability(owner_execution_id,last_reconciled_at) VALUES(p_owner_execution_id,now_utc)
        ON CONFLICT(owner_execution_id) DO UPDATE SET last_reconciled_at=EXCLUDED.last_reconciled_at;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    RETURN QUERY SELECT 0,0,19,now_utc;
END $catalog$;
REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_deadlocks_v2(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.reconcile_collector_catalog_deadlocks_v2(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
    TO sqlobserver_collector;

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
  AND (schedule.collector_id<>'backups.status' OR schedule.collector_version<>2
   OR EXISTS (SELECT 1 FROM control.backups_v2_worker_capability AS capability
    WHERE capability.owner_execution_id=p_owner AND capability.last_reconciled_at<=v_now
      AND capability.last_reconciled_at>v_now-interval '5 minutes'))
  AND (schedule.collector_id<>'deadlocks.system-health' OR schedule.collector_version<>2
   OR EXISTS (SELECT 1 FROM control.deadlocks_v2_worker_capability AS capability
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
