-- Register the pinned server configuration collector without enabling schedules.
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
VALUES
 ('engine.configurations',1,19,6,1,
  decode('0e2091d6e4e8246154aeacb7c7fe1578a9264623aae883e0ec7088b9ca266aeb','hex'),
  decode('0eb242b28bd428bbcb5bc3ea146c6fa8739226fbc779da470e431832a156a4c9','hex'),
  interval '5 minutes',interval '1 minute',interval '5 seconds',513,65536,
  'low',2,3,interval '5 minutes')
ON CONFLICT (collector_id,collector_version) DO NOTHING;

DO $configuration_contract_gate$
BEGIN
 IF NOT EXISTS
 (
  SELECT 1 FROM control.collector_contract
  WHERE collector_id='engine.configurations' AND collector_version=1
    AND execution_order=19 AND manifest_schema_version=6 AND output_schema_version=1
    AND manifest_sha256=decode('0e2091d6e4e8246154aeacb7c7fe1578a9264623aae883e0ec7088b9ca266aeb','hex')
    AND asset_bundle_sha256=decode('0eb242b28bd428bbcb5bc3ea146c6fa8739226fbc779da470e431832a156a4c9','hex')
    AND default_interval=interval '5 minutes' AND minimum_interval=interval '1 minute'
    AND execution_timeout=interval '5 seconds' AND maximum_rows=513
    AND maximum_response_bytes=65536 AND estimated_cost='low'
    AND maximum_attempts=2 AND circuit_failure_threshold=3
    AND circuit_open_interval=interval '5 minutes'
 ) THEN RAISE EXCEPTION 'server configuration collector contract differs from pinned catalog' USING ERRCODE='55000'; END IF;
END $configuration_contract_gate$;

INSERT INTO control.collector_dependency
 (collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES ('engine.configurations',1,'engine.core',1)
ON CONFLICT DO NOTHING;

CREATE FUNCTION control.reconcile_collector_catalog_m14
(
 p_collector_ids text[], p_collector_versions integer[], p_manifest_sha256 bytea[], p_asset_bundle_sha256 bytea[],
 p_execution_orders integer[], p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint
) RETURNS TABLE(inserted_count integer,updated_count integer,unchanged_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=control,pg_catalog AS $m14_catalog$
DECLARE now_utc timestamptz := clock_timestamp(); matched_count integer;
BEGIN
 IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile' OR p_owner_execution_id IS NULL OR p_fencing_token<=0
    OR cardinality(p_collector_ids)<>19 OR cardinality(p_collector_versions)<>19
    OR cardinality(p_manifest_sha256)<>19 OR cardinality(p_asset_bundle_sha256)<>19
    OR cardinality(p_execution_orders)<>19
    OR p_collector_ids[19] IS DISTINCT FROM 'engine.configurations'
    OR p_collector_versions[19] IS DISTINCT FROM 1
    OR p_execution_orders[19] IS DISTINCT FROM 19
 THEN RAISE EXCEPTION 'M14 catalog is not the exact ordered 19-entry contract' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT count(*) INTO matched_count
 FROM unnest(p_collector_ids,p_collector_versions,p_manifest_sha256,p_asset_bundle_sha256,p_execution_orders)
      WITH ORDINALITY AS supplied(id,version,manifest,bundle,execution_order,ordinal)
 JOIN control.collector_contract AS contract
   ON contract.collector_id=supplied.id AND contract.collector_version=supplied.version
  AND contract.manifest_sha256=supplied.manifest AND contract.asset_bundle_sha256=supplied.bundle
  AND contract.execution_order=supplied.execution_order
 WHERE supplied.execution_order=supplied.ordinal;
 IF matched_count<>19 THEN RAISE EXCEPTION 'M14 catalog differs from immutable collector contracts' USING ERRCODE='55000'; END IF;
 PERFORM control.reconcile_collector_catalog_m13(
  p_collector_ids[1:18],p_collector_versions[1:18],p_manifest_sha256[1:18],
  p_asset_bundle_sha256[1:18],p_execution_orders[1:18],p_work_key,p_owner_execution_id,p_fencing_token);
 INSERT INTO control.collector_schedule
  (instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,
   collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
 SELECT target.instance_id,contract.collector_id,contract.collector_version,target.revision,1,false,
        contract.default_interval,now_utc,'closed',0,now_utc,now_utc
 FROM control.observation_target AS target
 CROSS JOIN control.collector_contract AS contract
 WHERE target.host_name IS NOT NULL AND target.lifecycle_state IN ('pending_discovery','active')
   AND contract.collector_id='engine.configurations' AND contract.collector_version=1
 ON CONFLICT (instance_id,collector_id) DO UPDATE
 SET target_revision=EXCLUDED.target_revision,
     schedule_revision=collector_schedule.schedule_revision+1,
     enabled=false,collection_interval=EXCLUDED.collection_interval,
     next_due_at=EXCLUDED.next_due_at,active_run_id=NULL,circuit_state='closed',
     consecutive_failure_count=0,circuit_open_until=NULL,updated_at=EXCLUDED.updated_at
 WHERE collector_schedule.target_revision IS DISTINCT FROM EXCLUDED.target_revision
   AND collector_schedule.active_run_id IS NULL;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 0,0,19,now_utc;
END $m14_catalog$;

REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_m14(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.reconcile_collector_catalog_m14(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 TO sqlobserver_collector;

CREATE FUNCTION control.keep_configuration_schedule_opt_in()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,control AS $configuration_schedule_default$
BEGIN
 IF TG_OP='UPDATE' AND OLD.revision=NEW.revision
    AND OLD.lifecycle_state=NEW.lifecycle_state
    AND OLD.host_name IS NOT DISTINCT FROM NEW.host_name
    AND OLD.instance_name IS NOT DISTINCT FROM NEW.instance_name
    AND OLD.tcp_port IS NOT DISTINCT FROM NEW.tcp_port
    AND OLD.certificate_host_name IS NOT DISTINCT FROM NEW.certificate_host_name
    AND OLD.connect_timeout=NEW.connect_timeout
    AND OLD.authentication_mode=NEW.authentication_mode
    AND OLD.transport_security_mode=NEW.transport_security_mode
 THEN RETURN NEW; END IF;
 IF NEW.host_name IS NOT NULL AND NEW.lifecycle_state IN ('pending_discovery','active') THEN
  INSERT INTO control.collector_schedule
   (instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,
    collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
  SELECT NEW.instance_id,contract.collector_id,contract.collector_version,NEW.revision,1,false,
         contract.default_interval,clock_timestamp(),'closed',0,clock_timestamp(),clock_timestamp()
  FROM control.collector_contract AS contract
  WHERE contract.collector_id='engine.configurations' AND contract.collector_version=1
  ON CONFLICT (instance_id,collector_id) DO NOTHING;
 END IF;
 UPDATE control.collector_schedule
 SET enabled=false,updated_at=clock_timestamp()
 WHERE instance_id=NEW.instance_id AND collector_id='engine.configurations' AND enabled;
 RETURN NEW;
END $configuration_schedule_default$;
REVOKE ALL ON FUNCTION control.keep_configuration_schedule_opt_in()
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE TRIGGER zz_configuration_schedule_opt_in
AFTER INSERT OR UPDATE OF revision,lifecycle_state,host_name,instance_name,
 tcp_port,certificate_host_name,connect_timeout,authentication_mode,
 transport_security_mode
ON control.observation_target
FOR EACH ROW EXECUTE FUNCTION control.keep_configuration_schedule_opt_in();
