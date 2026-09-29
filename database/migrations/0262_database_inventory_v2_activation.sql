-- Register inventory v2 without changing schedules until a v2 worker holds the
-- catalog lease and proves that no version-1 inventory run remains active.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract
    DROP CONSTRAINT uq_collector_contract_execution_order;
ALTER TABLE control.collector_contract
    ADD CONSTRAINT uq_collector_contract_execution_order_version
    UNIQUE (execution_order, collector_version);

INSERT INTO control.collector_contract
 (collector_id,collector_version,execution_order,manifest_schema_version,
  output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,
  minimum_interval,execution_timeout,maximum_rows,maximum_response_bytes,
  estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
VALUES
 ('database.inventory',2,2,2,2,
  decode('5536227e577cd6f2c3d8bdab685fcd7c10eb915d0d1f2273c3733eebd43b4f21','hex'),
  decode('e48082564bda4f8f0a06c545640832b13dd99b01db1755ec3a2a8d0b794a7311','hex'),
  interval '1 minute',interval '30 seconds',interval '5 seconds',1000,1048576,
  'moderate',2,3,interval '5 minutes');

INSERT INTO control.collector_dependency
 (collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES ('database.inventory',2,'engine.core',1);

DO $dependency_gate$
BEGIN
 IF (SELECT count(*) FROM control.collector_dependency
     WHERE prerequisite_collector_id='database.inventory'
       AND prerequisite_collector_version=1) <> 3
    OR EXISTS (
      SELECT 1 FROM control.collector_dependency
      WHERE prerequisite_collector_id='database.inventory'
        AND prerequisite_collector_version=1
        AND (collector_id,collector_version) NOT IN
          (('database.files',1),('queries.performance',1),
           ('queries.performance-groups',1)))
 THEN RAISE EXCEPTION 'Inventory version-1 dependents differ from reviewed set'
      USING ERRCODE='55000'; END IF;
END $dependency_gate$;

INSERT INTO control.collector_prerequisite_compatibility
 (collector_id,collector_version,prerequisite_collector_id,
  prerequisite_collector_version,acceptable_collector_version)
VALUES
 ('database.files',1,'database.inventory',1,2),
 ('queries.performance',1,'database.inventory',1,2),
 ('queries.performance-groups',1,'database.inventory',1,2);

CREATE TABLE control.database_inventory_v2_activation
(
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 active boolean NOT NULL DEFAULT false,
 activated_at timestamptz
);
INSERT INTO control.database_inventory_v2_activation(singleton) VALUES(true);
ALTER TABLE control.database_inventory_v2_activation ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.database_inventory_v2_activation FORCE ROW LEVEL SECURITY;
CREATE POLICY database_inventory_v2_activation_owner_only
 ON control.database_inventory_v2_activation FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON control.database_inventory_v2_activation
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- Take the activation lock before the existing AFTER trigger touches schedule
-- rows, matching the catalog reconciliation lock order.
CREATE FUNCTION control.fence_database_inventory_v2_target()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control AS $fence_target$
BEGIN
 PERFORM 1 FROM control.database_inventory_v2_activation
  WHERE singleton FOR SHARE;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'Inventory v2 activation gate is missing'
   USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $fence_target$;
REVOKE ALL ON FUNCTION control.fence_database_inventory_v2_target()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER database_inventory_v2_target_fence
BEFORE INSERT OR UPDATE OF revision,lifecycle_state,host_name
ON control.observation_target
FOR EACH ROW EXECUTE FUNCTION control.fence_database_inventory_v2_target();

CREATE FUNCTION control.activate_database_inventory_v2_target()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $activate_target$
DECLARE desired_version integer;
BEGIN
 SELECT CASE WHEN active THEN 2 ELSE 1 END INTO desired_version
 FROM control.database_inventory_v2_activation WHERE singleton;
 IF desired_version IS NULL THEN
  RAISE EXCEPTION 'Inventory v2 activation gate is missing'
   USING ERRCODE='55000';
 END IF;
 UPDATE control.collector_schedule AS schedule
  SET collector_version=desired_version,
      schedule_revision=schedule.schedule_revision+1,
      next_due_at=clock_timestamp(),
      last_started_at=NULL,last_completed_at=NULL,
      last_succeeded_at=NULL,last_outcome=NULL,
      circuit_state='closed',consecutive_failure_count=0,
      circuit_open_until=NULL,updated_at=clock_timestamp()
  WHERE schedule.instance_id=NEW.instance_id
    AND schedule.collector_id='database.inventory'
    AND schedule.collector_version<>desired_version
    AND schedule.active_run_id IS NULL;
 RETURN NEW;
END $activate_target$;
REVOKE ALL ON FUNCTION control.activate_database_inventory_v2_target()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- Alphabetical trigger order runs this after observation_target_collector_schedules.
CREATE TRIGGER zz_database_inventory_v2_target
AFTER INSERT OR UPDATE OF revision,lifecycle_state,host_name
ON control.observation_target
FOR EACH ROW EXECUTE FUNCTION control.activate_database_inventory_v2_target();

CREATE FUNCTION control.reconcile_collector_catalog_m15
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
       decode('5536227e577cd6f2c3d8bdab685fcd7c10eb915d0d1f2273c3733eebd43b4f21','hex')
    OR p_asset_bundle_sha256[2] IS DISTINCT FROM
       decode('e48082564bda4f8f0a06c545640832b13dd99b01db1755ec3a2a8d0b794a7311','hex')
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
  'ec1cbfea68854d111d11aab48b476addbf2416e99e639bf97ea58545d78af484','hex');
 legacy_bundles[2] := decode(
  '10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17','hex');
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

REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_m15(
 text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor,sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.reconcile_collector_catalog_m15(
 text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 TO sqlobserver_collector;
