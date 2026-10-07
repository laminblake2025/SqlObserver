-- Register the pinned SQL Agent activity observations collector without enabling schedules.
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
 ('sql-agent.activity-observations',1,22,6,1,
  decode('c7e82d6b77087a0ef2ab7aec7551d5f4cb2be335129afbf6391bbf503afba15d','hex'),
  decode('d60bbdffe9b547c0fff27b7de0fff8457f7b6e42d34f55a9a21f68c995ef06a4','hex'),
  interval '60 seconds',interval '30 seconds',interval '10 seconds',513,524288,
  'moderate',1,3,interval '5 minutes')
ON CONFLICT (collector_id,collector_version) DO NOTHING;

DO $sql_agent_activity_observations_contract_gate$
BEGIN
 IF NOT EXISTS
 (
  SELECT 1 FROM control.collector_contract
  WHERE collector_id='sql-agent.activity-observations' AND collector_version=1
    AND execution_order=22 AND manifest_schema_version=6 AND output_schema_version=1
    AND manifest_sha256=decode('c7e82d6b77087a0ef2ab7aec7551d5f4cb2be335129afbf6391bbf503afba15d','hex')
    AND asset_bundle_sha256=decode('d60bbdffe9b547c0fff27b7de0fff8457f7b6e42d34f55a9a21f68c995ef06a4','hex')
    AND default_interval=interval '60 seconds' AND minimum_interval=interval '30 seconds'
    AND execution_timeout=interval '10 seconds' AND maximum_rows=513
    AND maximum_response_bytes=524288 AND estimated_cost='moderate'
    AND maximum_attempts=1 AND circuit_failure_threshold=3
    AND circuit_open_interval=interval '5 minutes'
 ) THEN RAISE EXCEPTION 'SQL Agent activity observations collector contract differs from pinned catalog' USING ERRCODE='55000'; END IF;
END $sql_agent_activity_observations_contract_gate$;

-- Connection discovery is a manifest prerequisite, not a scheduled collector FK.
INSERT INTO control.m9_manifest_dependency(collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES('sql-agent.activity-observations',1,'capability.connection',1),('sql-agent.activity-observations',1,'engine.core',1);
INSERT INTO control.collector_dependency(collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES('sql-agent.activity-observations',1,'engine.core',1);

-- Private admission predicate; VOLATILE obtains a fresh snapshot after the enrollment lock.
CREATE FUNCTION control.sql_agent_activity_enrollment_active(p_instance uuid,p_revision bigint)
RETURNS boolean LANGUAGE sql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $active$
 SELECT coalesce((SELECT c.enrollment IS NOT NULL
   -- Preserve all enrollment fractional digits rather than rounding to PostgreSQL microseconds.
   AND control.agent_activity_authorization_ticks(c.enrollment->>'notBeforeUtc')<=extract(epoch FROM clock_timestamp())*10000000
   AND extract(epoch FROM clock_timestamp())*10000000<control.agent_activity_authorization_ticks(c.enrollment->>'expiresAtUtc')
   AND t.lifecycle_state='active' AND t.authentication_mode='windows_integrated_service_identity'
   AND t.transport_security_mode='mandatory_validated' AND t.instance_name IS NULL
   AND lower(c.enrollment->'binding'->>'endpointHost')=lower(t.host_name)
   AND (c.enrollment->'binding'->>'tcpPort')::integer=t.tcp_port
   AND lower(c.enrollment->'binding'->>'certificateHost')=lower(t.certificate_host_name)
  FROM control.sql_agent_module_enrollment_command c
  JOIN control.observation_target t ON t.instance_id=c.instance_id AND t.revision=c.target_revision
  WHERE c.instance_id=p_instance AND c.target_revision=p_revision
  ORDER BY c.command_revision DESC LIMIT 1),false);
$active$;
REVOKE ALL ON FUNCTION control.sql_agent_activity_enrollment_active(uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.reconcile_collector_catalog_sql_agent_activity_observations
(
 p_collector_ids text[], p_collector_versions integer[], p_manifest_sha256 bytea[], p_asset_bundle_sha256 bytea[],
 p_execution_orders integer[], p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint
) RETURNS TABLE(inserted_count integer,updated_count integer,unchanged_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=control,pg_catalog AS $agent_activity_catalog$
DECLARE now_utc timestamptz := clock_timestamp(); matched_count integer; lock_target uuid;
BEGIN
 IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile' OR p_owner_execution_id IS NULL OR p_fencing_token<=0
    OR cardinality(p_collector_ids) IS DISTINCT FROM 22 OR array_lower(p_collector_ids,1) IS DISTINCT FROM 1 OR array_ndims(p_collector_ids) IS DISTINCT FROM 1 OR cardinality(p_collector_versions) IS DISTINCT FROM 22 OR array_lower(p_collector_versions,1) IS DISTINCT FROM 1 OR array_ndims(p_collector_versions) IS DISTINCT FROM 1
    OR cardinality(p_manifest_sha256) IS DISTINCT FROM 22 OR array_lower(p_manifest_sha256,1) IS DISTINCT FROM 1 OR array_ndims(p_manifest_sha256) IS DISTINCT FROM 1 OR cardinality(p_asset_bundle_sha256) IS DISTINCT FROM 22 OR array_lower(p_asset_bundle_sha256,1) IS DISTINCT FROM 1 OR array_ndims(p_asset_bundle_sha256) IS DISTINCT FROM 1
    OR cardinality(p_execution_orders) IS DISTINCT FROM 22 OR array_lower(p_execution_orders,1) IS DISTINCT FROM 1 OR array_ndims(p_execution_orders) IS DISTINCT FROM 1
    OR p_collector_ids[22] IS DISTINCT FROM 'sql-agent.activity-observations'
    OR p_collector_versions[22] IS DISTINCT FROM 1
    OR p_execution_orders[22] IS DISTINCT FROM 22
 THEN RAISE EXCEPTION 'SQL Agent activity observations catalog is not the exact ordered 22-entry contract' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT count(*) INTO matched_count
 FROM unnest(p_collector_ids,p_collector_versions,p_manifest_sha256,p_asset_bundle_sha256,p_execution_orders)
      WITH ORDINALITY AS supplied(id,version,manifest,bundle,execution_order,ordinal)
 JOIN control.collector_contract AS contract
   ON contract.collector_id=supplied.id AND contract.collector_version=supplied.version
  AND contract.manifest_sha256=supplied.manifest AND contract.asset_bundle_sha256=supplied.bundle
  AND contract.execution_order=supplied.execution_order
 WHERE supplied.execution_order=supplied.ordinal;
 IF matched_count<>22 THEN RAISE EXCEPTION 'SQL Agent activity observations catalog differs from immutable collector contracts' USING ERRCODE='55000'; END IF;
 -- Acquire every current target's enrollment lock in UUID order before predecessor schedule work.
 FOR lock_target IN SELECT instance_id FROM control.observation_target ORDER BY instance_id LOOP
  PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-target:'||lock_target::text,0));
 END LOOP;
 now_utc:=clock_timestamp();
 -- Delegate the complete prior twenty-one-entry chain, including reviewed successor versions.
 PERFORM control.reconcile_collector_catalog_server_lifecycle(p_collector_ids[1:21],p_collector_versions[1:21],p_manifest_sha256[1:21],p_asset_bundle_sha256[1:21],p_execution_orders[1:21],p_work_key,p_owner_execution_id,p_fencing_token);
 IF (SELECT count(*) FROM control.m9_manifest_dependency WHERE collector_id='sql-agent.activity-observations' AND collector_version=1)<>2 OR
    (SELECT count(*) FROM control.m9_manifest_dependency WHERE collector_id='sql-agent.activity-observations' AND collector_version=1 AND prerequisite_collector_id IN('capability.connection','engine.core') AND prerequisite_collector_version=1)<>2 OR
    (SELECT count(*) FROM control.collector_dependency WHERE collector_id='sql-agent.activity-observations' AND collector_version=1)<>1 OR
    NOT EXISTS(SELECT 1 FROM control.collector_dependency WHERE collector_id='sql-agent.activity-observations' AND collector_version=1 AND prerequisite_collector_id='engine.core' AND prerequisite_collector_version=1) THEN
  RAISE EXCEPTION 'Agent activity observations dependencies differ from immutable contract' USING ERRCODE='55000'; END IF;
 PERFORM control.ensure_sql_agent_activity_partition_set(p_owner_execution_id,p_fencing_token);
 INSERT INTO control.collector_schedule
  (instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,
   collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
 SELECT target.instance_id,contract.collector_id,contract.collector_version,target.revision,1,false,
        contract.default_interval,now_utc,'closed',0,now_utc,now_utc
 FROM control.observation_target AS target
 CROSS JOIN control.collector_contract AS contract
 WHERE target.host_name IS NOT NULL AND target.lifecycle_state IN ('pending_discovery','active')
   AND contract.collector_id='sql-agent.activity-observations' AND contract.collector_version=1
 ON CONFLICT (instance_id,collector_id) DO UPDATE
 SET target_revision=EXCLUDED.target_revision,
     schedule_revision=collector_schedule.schedule_revision+1,
     enabled=false,collection_interval=EXCLUDED.collection_interval,
     next_due_at=EXCLUDED.next_due_at,active_run_id=NULL,circuit_state='closed',
     consecutive_failure_count=0,circuit_open_until=NULL,updated_at=EXCLUDED.updated_at
 WHERE collector_schedule.target_revision IS DISTINCT FROM EXCLUDED.target_revision
   AND collector_schedule.active_run_id IS NULL;
 -- Preserve an explicit same-revision opt-in only while current enrollment is active.
 -- Active runs retain their ID so they may produce a failure/gap completion under the fenced writer.
 UPDATE control.collector_schedule s SET enabled=false,schedule_revision=s.schedule_revision+1,updated_at=clock_timestamp()
 FROM control.observation_target t
 WHERE s.instance_id=t.instance_id AND s.collector_id='sql-agent.activity-observations' AND s.enabled
  AND (t.lifecycle_state<>'active' OR s.target_revision<>t.revision
       OR NOT control.sql_agent_activity_enrollment_active(t.instance_id,t.revision));
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 -- Counts describe unchanged immutable catalog contracts, as in the predecessor chain; not schedule rows.
 RETURN QUERY SELECT 0,0,22,now_utc;
END $agent_activity_catalog$;

REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_sql_agent_activity_observations(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.reconcile_collector_catalog_sql_agent_activity_observations(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)
 TO sqlobserver_collector;

CREATE FUNCTION control.keep_sql_agent_activity_observations_schedule_opt_in()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,control AS $sql_agent_activity_observations_schedule_default$
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
 THEN
  IF NOT EXISTS(SELECT 1 FROM control.collector_schedule s WHERE s.instance_id=NEW.instance_id
    AND s.collector_id='sql-agent.activity-observations' AND s.enabled)
    OR control.sql_agent_activity_enrollment_active(NEW.instance_id,NEW.revision)
  THEN RETURN NEW; END IF;
 END IF;
 IF NEW.host_name IS NOT NULL AND NEW.lifecycle_state IN ('pending_discovery','active') THEN
  INSERT INTO control.collector_schedule
   (instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,
    collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
  SELECT NEW.instance_id,contract.collector_id,contract.collector_version,NEW.revision,1,false,
         contract.default_interval,clock_timestamp(),'closed',0,clock_timestamp(),clock_timestamp()
  FROM control.collector_contract AS contract
  WHERE contract.collector_id='sql-agent.activity-observations' AND contract.collector_version=1
  ON CONFLICT (instance_id,collector_id) DO NOTHING;
 END IF;
 UPDATE control.collector_schedule
 SET enabled=false,schedule_revision=schedule_revision+1,updated_at=clock_timestamp()
 WHERE instance_id=NEW.instance_id AND collector_id='sql-agent.activity-observations'
  AND (enabled OR TG_OP='UPDATE');
 RETURN NEW;
END $sql_agent_activity_observations_schedule_default$;
REVOKE ALL ON FUNCTION control.keep_sql_agent_activity_observations_schedule_opt_in()
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE TRIGGER zz_sql_agent_activity_observations_schedule_opt_in
AFTER INSERT OR UPDATE OF revision,lifecycle_state,host_name,instance_name,
 tcp_port,certificate_host_name,connect_timeout,authentication_mode,
 transport_security_mode
ON control.observation_target
FOR EACH ROW EXECUTE FUNCTION control.keep_sql_agent_activity_observations_schedule_opt_in();

CREATE FUNCTION control.set_sql_agent_activity_observations_collection_enabled(
    p_instance_id uuid,p_expected_target_revision bigint,p_expected_schedule_revision bigint,
    p_expected_enabled boolean,p_enabled boolean,
    p_actor_identifier text,p_correlation_id uuid)
RETURNS TABLE(result_status text,schedule_revision bigint,collector_enabled boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
DECLARE
    v_target_revision bigint;
    v_lifecycle text;
    v_schedule control.collector_schedule%ROWTYPE;
BEGIN
    IF p_instance_id IS NULL OR p_expected_target_revision IS NULL
       OR p_expected_target_revision NOT BETWEEN 1 AND 9007199254740991
       OR p_expected_schedule_revision IS NULL OR p_expected_schedule_revision NOT BETWEEN 1 AND 9007199254740991
       OR (p_enabled IS DISTINCT FROM p_expected_enabled AND p_expected_schedule_revision=9007199254740991)
       OR p_expected_enabled IS NULL
       OR p_enabled IS NULL OR p_actor_identifier IS NULL
       OR p_correlation_id IS NULL
    THEN RAISE EXCEPTION 'SQL Agent activity observations collection request is invalid'
        USING ERRCODE='22023'; END IF;

    -- Enrollment lock precedes target and schedule row locks, matching revocation and canonical commit.
    PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-target:'||p_instance_id::text,0));

    SELECT target.revision,target.lifecycle_state
      INTO v_target_revision,v_lifecycle
    FROM control.observation_target AS target
    WHERE target.instance_id=p_instance_id FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','failed','target_not_found');
        RETURN QUERY SELECT 'not_found'::text,NULL::bigint,NULL::boolean;
        RETURN;
    END IF;
    IF v_lifecycle<>'active' THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict',
            CASE WHEN v_lifecycle='retired' THEN 'target_retired'
                 ELSE 'revision_conflict' END);
        RETURN QUERY SELECT 'inactive'::text,NULL::bigint,NULL::boolean;
        RETURN;
    END IF;
    IF v_target_revision<>p_expected_target_revision THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict','revision_conflict');
        RETURN QUERY SELECT 'revision_conflict'::text,NULL::bigint,NULL::boolean;
        RETURN;
    END IF;

    SELECT * INTO v_schedule FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id=p_instance_id
      AND schedule.collector_id='sql-agent.activity-observations' FOR UPDATE;
    IF NOT FOUND OR v_schedule.target_revision<>v_target_revision
    THEN RAISE EXCEPTION 'SQL Agent activity observations collector schedule is unavailable'
        USING ERRCODE='55000'; END IF;
    IF v_schedule.schedule_revision<>p_expected_schedule_revision OR v_schedule.enabled<>p_expected_enabled THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict','revision_conflict');
        RETURN QUERY SELECT 'revision_conflict'::text,
            v_schedule.schedule_revision,v_schedule.enabled;
        RETURN;
    END IF;
    -- Disable/no-op-off remains possible after revocation or expiry.
    IF p_enabled AND NOT control.sql_agent_activity_enrollment_active(p_instance_id,v_target_revision) THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict','revision_conflict');
        RETURN QUERY SELECT 'enrollment_unavailable'::text,
            v_schedule.schedule_revision,v_schedule.enabled;
        RETURN;
    END IF;
    IF v_schedule.enabled<>p_enabled THEN
        UPDATE control.collector_schedule AS schedule
        SET enabled=p_enabled,schedule_revision=schedule.schedule_revision+1,
            next_due_at=CASE WHEN p_enabled THEN clock_timestamp()
                             ELSE schedule.next_due_at END,
            updated_at=clock_timestamp()
        WHERE schedule.instance_id=p_instance_id
          AND schedule.collector_id='sql-agent.activity-observations'
        RETURNING schedule.schedule_revision,schedule.enabled
             INTO v_schedule.schedule_revision,v_schedule.enabled;
    END IF;
    PERFORM * FROM audit.append_administrative_activity(
        p_actor_identifier,p_correlation_id,'update_observation_target',
        p_instance_id,'granted','succeeded','completed');
    RETURN QUERY SELECT 'applied'::text,
        v_schedule.schedule_revision,v_schedule.enabled;
END;
$fn$;

REVOKE ALL ON FUNCTION control.set_sql_agent_activity_observations_collection_enabled(
    uuid,bigint,bigint,boolean,boolean,text,uuid)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_sql_agent_activity_observations_collection_enabled(
    uuid,bigint,bigint,boolean,boolean,text,uuid) TO sqlobserver_server;

CREATE FUNCTION reporting.read_sql_agent_activity_observations_collection_state(p_target uuid,p_revision bigint)
RETURNS TABLE(target_revision bigint,schedule_revision bigint,collector_enabled boolean)
LANGUAGE plpgsql SECURITY DEFINER STABLE SET search_path=pg_catalog SET row_security=on AS $state$
BEGIN
 IF p_target IS NULL OR p_revision IS NULL OR p_revision NOT BETWEEN 1 AND 9007199254740991 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'Agent activity observations target scope required' USING ERRCODE='42501'; END IF;
 RETURN QUERY SELECT t.revision,s.schedule_revision,s.enabled FROM control.observation_target t
  JOIN control.collector_schedule s ON s.instance_id=t.instance_id AND s.collector_id='sql-agent.activity-observations' WHERE t.instance_id=p_target;
END $state$;
REVOKE ALL ON FUNCTION reporting.read_sql_agent_activity_observations_collection_state(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_agent_activity_observations_collection_state(uuid,bigint) TO sqlobserver_server;

-- Existing targets receive a disabled schedule before the first post-upgrade reconciliation.
INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,
 collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
SELECT t.instance_id,'sql-agent.activity-observations',1,t.revision,1,false,c.default_interval,clock_timestamp(),'closed',0,clock_timestamp(),clock_timestamp()
FROM control.observation_target t JOIN control.collector_contract c ON c.collector_id='sql-agent.activity-observations' AND c.collector_version=1
WHERE t.host_name IS NOT NULL AND t.lifecycle_state IN('pending_discovery','active')
ON CONFLICT(instance_id,collector_id) DO NOTHING;
