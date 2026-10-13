-- Bind the additive, checksum-pinned SQL Server 2014 core/activity variants.
-- Preserve schedules, run identities, output schemas, limits, and historical assets.
-- The migration and ledger append execute in one transaction. Any prior drift
-- rolls back all eight bindings and restores the append-only trigger.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $compatibility$
DECLARE changed integer;
BEGIN
    UPDATE control.collector_contract AS contract
    SET manifest_sha256=decode(expected.new_manifest,'hex'),
        asset_bundle_sha256=decode(expected.new_bundle,'hex')
    FROM (VALUES
        ('engine.core',1,1,1,'f062ab816cdbb56e7ea9f77ed0042bf00df2bb9b0e6c08b907710f468d8755a1','10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17','3a9f281a29f9222ae829e16d6bda859bb13a7bd8255478d3a8b47e626ffb7fb9','65cdb23980bbae928879257ba5cc118de98595760920f77da0f08f8147b90627'),
        ('database.inventory',1,2,1,'ec1cbfea68854d111d11aab48b476addbf2416e99e639bf97ea58545d78af484','10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17','c91afd4db152abcaf182244521f836b9f4c54f5f9d487dc38007f6ec2373a4b3','65cdb23980bbae928879257ba5cc118de98595760920f77da0f08f8147b90627'),
        ('database.inventory',2,2,2,'5536227e577cd6f2c3d8bdab685fcd7c10eb915d0d1f2273c3733eebd43b4f21','e48082564bda4f8f0a06c545640832b13dd99b01db1755ec3a2a8d0b794a7311','04f0412b5e2879e2edee2f3a18ff29f0695489fc48df9df8c273b8d900154d18','1db50de37edd781cc946dd9aa5426a8f762a32c5b276143590b7bca8c59ef521'),
        ('database.files',1,3,1,'06c9353fe554f737f933c0fa4938fef19f5c6c0dd6af8832c4671160f5af0dcd','10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17','943bc8bd0b9e394e9472c0a1cdbe71c26d614b6a692a459e50002e0f197292dd','65cdb23980bbae928879257ba5cc118de98595760920f77da0f08f8147b90627'),
        ('activity.sessions',1,4,1,'f7088bc48ab289b1923ef0cc1ba9565999e7242e0eb6cb3eb85406cd747d1ffb','d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8','445d763f98a195dc71806f68cca7c0cac321492998f506087055790d51742bf2','bd28cd2f37e2e2219154124ddc1f73894f1396f0411586b5e23699389979ea8a'),
        ('activity.requests',1,5,1,'6490b8aaa503d5f96e52c4aacdb5e94a52965cc1bed0f0d0502f3e663cc88ee1','d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8','9ed2fa71612891b56558638af6ccbb125450c7c1a16da47f86106dffbee20a0c','bd28cd2f37e2e2219154124ddc1f73894f1396f0411586b5e23699389979ea8a'),
        ('waits.server',1,6,1,'aeae9d3d2a2f373b9b66b0e68a0c2d51dcc548dad3fc175a118ba3a5cd007da9','d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8','08341c6fa4192874083ad6a34db4b5489bad9b39a43f0b25db1ef16165967913','bd28cd2f37e2e2219154124ddc1f73894f1396f0411586b5e23699389979ea8a'),
        ('blocking.current',1,7,1,'2470cbe3d16e3825a8c30ed0fde6d84e0eced0124f93ef8ff268f93b4523c59b','d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8','7138b9a85f710306b20f204103b489ad8c6ce13f5f8d3e31b6f3aee52f420d7f','bd28cd2f37e2e2219154124ddc1f73894f1396f0411586b5e23699389979ea8a')
    ) AS expected(id,version,execution_order,output_schema,old_manifest,old_bundle,new_manifest,new_bundle)
    WHERE contract.collector_id=expected.id AND contract.collector_version=expected.version
      AND contract.execution_order=expected.execution_order AND contract.output_schema_version=expected.output_schema
      AND contract.manifest_sha256=decode(expected.old_manifest,'hex')
      AND contract.asset_bundle_sha256=decode(expected.old_bundle,'hex');
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed<>8 THEN
        RAISE EXCEPTION 'Unexpected prior SQL Server core/activity collector contracts' USING ERRCODE='55000';
    END IF;
END
$compatibility$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;

-- Retain the complete reviewed activation, fencing, ordering, dependency, and
-- run-drain behavior, rebinding only the exact inventory v1/v2 digests.
-- Keep the 0263 worker-capability wrapper intact by replacing only its core.
CREATE OR REPLACE FUNCTION control.reconcile_collector_catalog_m15_core
(
 p_collector_ids text[],p_collector_versions integer[],
 p_manifest_sha256 bytea[],p_asset_bundle_sha256 bytea[],
 p_execution_orders integer[],p_work_key text,
 p_owner_execution_id uuid,p_fencing_token bigint
)
RETURNS TABLE(inserted_count integer,updated_count integer,
              unchanged_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m15$
DECLARE
 legacy_versions integer[] := p_collector_versions;
 legacy_manifests bytea[] := p_manifest_sha256;
 legacy_bundles bytea[] := p_asset_bundle_sha256;
 now_utc timestamptz := clock_timestamp();
BEGIN
 IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile'
    OR cardinality(p_collector_ids) IS DISTINCT FROM 19
    OR cardinality(p_collector_versions) IS DISTINCT FROM 19
    OR cardinality(p_manifest_sha256) IS DISTINCT FROM 19
    OR cardinality(p_asset_bundle_sha256) IS DISTINCT FROM 19
    OR cardinality(p_execution_orders) IS DISTINCT FROM 19
    OR p_collector_ids[2] IS DISTINCT FROM 'database.inventory'
    OR p_collector_versions[2] IS DISTINCT FROM 2
    OR p_execution_orders[2] IS DISTINCT FROM 2
    OR p_manifest_sha256[2] IS DISTINCT FROM
       decode('04f0412b5e2879e2edee2f3a18ff29f0695489fc48df9df8c273b8d900154d18','hex')
    OR p_asset_bundle_sha256[2] IS DISTINCT FROM
       decode('1db50de37edd781cc946dd9aa5426a8f762a32c5b276143590b7bca8c59ef521','hex')
 THEN RAISE EXCEPTION 'M15 catalog requires the exact inventory-v2 contract'
      USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(
  p_work_key,p_owner_execution_id,p_fencing_token);
 PERFORM 1 FROM control.database_inventory_v2_activation
  WHERE singleton FOR UPDATE;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'Inventory v2 activation gate is missing'
   USING ERRCODE='55000';
 END IF;
 IF NOT EXISTS (
  SELECT 1 FROM control.collector_contract
  WHERE collector_id='database.inventory' AND collector_version=2
    AND execution_order=2 AND output_schema_version=2
    AND manifest_sha256=p_manifest_sha256[2]
    AND asset_bundle_sha256=p_asset_bundle_sha256[2]) THEN
  RAISE EXCEPTION 'Inventory v2 repository contract differs from pinned catalog'
   USING ERRCODE='55000';
 END IF;

 legacy_versions[2] := 1;
 legacy_manifests[2] := decode(
  'c91afd4db152abcaf182244521f836b9f4c54f5f9d487dc38007f6ec2373a4b3','hex');
 legacy_bundles[2] := decode(
  '65cdb23980bbae928879257ba5cc118de98595760920f77da0f08f8147b90627','hex');
 PERFORM control.reconcile_collector_catalog_m14(
  p_collector_ids,legacy_versions,legacy_manifests,legacy_bundles,
  p_execution_orders,p_work_key,p_owner_execution_id,p_fencing_token);

 -- Lock the legacy schedule rows in a fixed order. A concurrent old begin must
 -- finish first; a pending old run aborts the whole activation transaction.
 PERFORM 1 FROM control.collector_schedule AS schedule
 WHERE schedule.collector_id='database.inventory'
   AND schedule.collector_version=1
 ORDER BY schedule.instance_id FOR UPDATE;
 IF EXISTS (SELECT 1 FROM control.collector_schedule
            WHERE collector_id='database.inventory'
              AND collector_version=1 AND active_run_id IS NOT NULL)
 THEN RAISE EXCEPTION 'Inventory v1 runs must drain before v2 activation'
      USING ERRCODE='55000'; END IF;

 UPDATE control.collector_schedule AS schedule
 SET collector_version=2,
     schedule_revision=schedule.schedule_revision+1,
     next_due_at=now_utc,
     last_started_at=NULL,last_completed_at=NULL,
     last_succeeded_at=NULL,last_outcome=NULL,
     circuit_state='closed',consecutive_failure_count=0,
     circuit_open_until=NULL,updated_at=now_utc
 WHERE schedule.collector_id='database.inventory'
   AND schedule.collector_version=1;
 UPDATE control.database_inventory_v2_activation
 SET active=true,activated_at=coalesce(activated_at,now_utc)
 WHERE singleton;
 PERFORM control.assert_worker_lease(
  p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 0,0,19,now_utc;
END $m15$;
