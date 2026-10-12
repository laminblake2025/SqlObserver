-- Correct the staged AG v2 SQL Server 2019 metadata permission contract.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL idle_in_transaction_session_timeout = '5min';

DO $permission_contract$
DECLARE gate_active boolean;
BEGIN
 SELECT active INTO gate_active FROM control.availability_groups_v2_activation
 WHERE singleton FOR UPDATE;
 IF NOT FOUND OR gate_active THEN
  RAISE EXCEPTION 'AG v2 permission correction requires an inactive rollout gate' USING ERRCODE='55000';
 END IF;
 PERFORM 1 FROM control.collector_schedule WHERE collector_id='availability-groups.health'
 ORDER BY instance_id FOR UPDATE;
 IF EXISTS (SELECT 1 FROM control.collector_schedule
  WHERE collector_id='availability-groups.health' AND collector_version=2)
  OR EXISTS (SELECT 1 FROM control.availability_groups_v2_worker_capability) THEN
  RAISE EXCEPTION 'AG v2 permission correction requires an unactivated catalog' USING ERRCODE='55000';
 END IF;
 ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
 UPDATE control.collector_contract
 SET manifest_sha256=decode('a53b435b88a7beb14dfecc759530ec0d666fd92a069130bf175a1f947b84e009','hex'),
     asset_bundle_sha256=decode('8fe71e27b59e2d9dc0ae79786a5680853835c2f7530c85dd1fdc7af3b035d208','hex')
 WHERE collector_id='availability-groups.health' AND collector_version=2
  AND manifest_sha256=decode('07e2282bb37ad54e173b34f65e0bdd29aa4ca6e9e8b1a956a33a5a41720d2c17','hex')
  AND asset_bundle_sha256=decode('88b4d627a492dfd738f11e6b6f72016a802c5c279d6302a2b1f88c3283a313cf','hex');
 IF NOT FOUND THEN
  RAISE EXCEPTION 'AG v2 staged contract differs from approved predecessor' USING ERRCODE='55000';
 END IF;
 ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
END $permission_contract$;

CREATE OR REPLACE FUNCTION control.reconcile_collector_catalog_ag_v2
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
   decode('a53b435b88a7beb14dfecc759530ec0d666fd92a069130bf175a1f947b84e009','hex')
  OR p_asset_bundle_sha256[13] IS DISTINCT FROM
   decode('8fe71e27b59e2d9dc0ae79786a5680853835c2f7530c85dd1fdc7af3b035d208','hex')
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
