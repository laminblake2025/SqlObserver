-- Bind staged cache evidence to canonical run identity and graph retention.
-- No collector version, source catalog or runtime grant is activated.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- FORCE RLS can hide pre-existing rows from ADD FOREIGN KEY validation.
-- Lock the private relations and expose them only to the migration owner in
-- this transaction. Validate explicitly; the temporary SELECT policies are
-- dropped before commit and never become visible to runtime connections.
LOCK TABLE events.plan_cache_runtime_watermark,events.plan_cache_runtime_delta,
 events.plan_cache_runtime_receipt IN ACCESS EXCLUSIVE MODE;
DO $validate_cache_runs$
DECLARE v_relation record; v_column name; v_orphan boolean;
BEGIN
 PERFORM 1 FROM system.partition_registry registry WHERE registry.parent_schema='events'
    AND registry.parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt')
    AND registry.lifecycle_state='detached' FOR SHARE;
 IF EXISTS(SELECT 1 FROM system.partition_registry registry WHERE registry.parent_schema='events'
    AND registry.parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt')
    AND registry.lifecycle_state='detached' AND (registry.partition_schema<>'events'
      OR to_regclass(format('%I.%I',registry.partition_schema,registry.partition_name)) IS NULL)) THEN
  RAISE EXCEPTION 'Registered detached cache relation is unavailable for validation' USING ERRCODE='55000'; END IF;
 FOR v_relation IN
  SELECT relation.oid,namespace.nspname,relation.relname,relation.relrowsecurity,relation.relforcerowsecurity,
    pg_get_userbyid(relation.relowner) AS owner_name,relation.relkind,
    relation.relname='plan_cache_runtime_watermark' OR registry.parent_table='plan_cache_runtime_watermark' AS watermark,
    registry.lifecycle_state='detached' AS detached
  FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
  LEFT JOIN system.partition_registry registry ON registry.partition_schema=namespace.nspname
    AND registry.partition_name=relation.relname AND registry.lifecycle_state='detached'
  WHERE namespace.nspname='events' AND (
    relation.relname IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt')
    OR registry.parent_schema='events' AND registry.parent_table IN
      ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt'))
  ORDER BY relation.relname LOOP
  IF v_relation.owner_name<>'sqlobserver_migrator' OR NOT v_relation.relrowsecurity
     OR NOT v_relation.relforcerowsecurity OR v_relation.relkind NOT IN ('p','r') THEN
   RAISE EXCEPTION 'Private cache relation is not migration-owned and forced-RLS' USING ERRCODE='55000'; END IF;
  EXECUTE format('LOCK TABLE %I.%I IN ACCESS EXCLUSIVE MODE',v_relation.nspname,v_relation.relname);
  EXECUTE format('CREATE POLICY cache_run_upgrade_validation ON %I.%I FOR SELECT TO sqlobserver_migrator USING(true)',
    v_relation.nspname,v_relation.relname);
  v_column:=CASE WHEN v_relation.watermark THEN 'last_run_id' ELSE 'run_id' END;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I cache LEFT JOIN telemetry.collection_run_identity identity
    ON identity.run_id=cache.%I WHERE identity.run_id IS NULL)',v_relation.nspname,v_relation.relname,v_column)
    INTO v_orphan;
  IF v_orphan THEN
   RAISE EXCEPTION 'Existing private cache evidence has no canonical run identity' USING ERRCODE='23503'; END IF;
  IF v_relation.detached THEN
   EXECUTE format('CREATE INDEX %I ON %I.%I(%I)','ix_cache_run_'||v_relation.oid,v_relation.nspname,v_relation.relname,v_column);
   EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT fk_plan_cache_detached_run
     FOREIGN KEY(%I) REFERENCES telemetry.collection_run_identity(run_id)',v_relation.nspname,v_relation.relname,v_column);
  END IF;
  EXECUTE format('DROP POLICY cache_run_upgrade_validation ON %I.%I',v_relation.nspname,v_relation.relname);
 END LOOP;
END $validate_cache_runs$;

CREATE INDEX ix_plan_cache_watermark_canonical_run ON events.plan_cache_runtime_watermark(last_run_id);
CREATE INDEX ix_plan_cache_delta_canonical_run ON events.plan_cache_runtime_delta(run_id);
CREATE INDEX ix_plan_cache_receipt_canonical_run ON events.plan_cache_runtime_receipt(run_id);
ALTER TABLE events.plan_cache_runtime_watermark ADD CONSTRAINT fk_plan_cache_watermark_run
 FOREIGN KEY(last_run_id) REFERENCES telemetry.collection_run_identity(run_id);
ALTER TABLE events.plan_cache_runtime_delta ADD CONSTRAINT fk_plan_cache_delta_run
 FOREIGN KEY(run_id) REFERENCES telemetry.collection_run_identity(run_id);
ALTER TABLE events.plan_cache_runtime_receipt ADD CONSTRAINT fk_plan_cache_receipt_run
 FOREIGN KEY(run_id) REFERENCES telemetry.collection_run_identity(run_id);

CREATE FUNCTION system.plan_cache_runtime_run_retention_holds(p_instance_id uuid,p_run_id uuid)
RETURNS TABLE(hold_code text) LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,events,telemetry SET row_security=on SET TimeZone='UTC'
AS $cache_holds$
DECLARE v_partition record; v_relation oid; v_column name; v_present boolean;
BEGIN
 IF p_instance_id IS NULL OR p_run_id IS NULL
    OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    OR NOT pg_has_role(current_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'Cache run retention preview requires migrator and target scope' USING ERRCODE='42501'; END IF;
 -- Stabilize both attached and detached registry rows before querying data.
 -- A registered detach/drop needs its exclusive row lock in this transaction.
 PERFORM 1 FROM system.partition_registry registry WHERE registry.parent_schema='events'
    AND registry.parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt')
    AND registry.lifecycle_state IN ('attached','detached') FOR SHARE;
 IF EXISTS(SELECT 1 FROM events.plan_cache_runtime_watermark
    WHERE instance_id=p_instance_id AND last_run_id=p_run_id) THEN hold_code:='cache_watermark'; RETURN NEXT; END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_runtime_delta
    WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN hold_code:='cache_delta'; RETURN NEXT; END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_runtime_receipt
    WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN hold_code:='cache_receipt'; RETURN NEXT; END IF;
 -- Detached grace data is no longer visible through its former parent.
 -- Probe the exact target/run in each registered relation, with its run index.
 FOR v_partition IN SELECT registry.* FROM system.partition_registry registry
  WHERE registry.parent_schema='events'
    AND registry.parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt')
    AND registry.lifecycle_state='detached' ORDER BY registry.parent_table,registry.partition_name LOOP
  v_relation:=to_regclass(format('%I.%I',v_partition.partition_schema,v_partition.partition_name));
  v_column:=CASE WHEN v_partition.parent_table='plan_cache_runtime_watermark' THEN 'last_run_id' ELSE 'run_id' END;
  IF v_relation IS NULL OR v_partition.partition_schema<>'events' OR NOT EXISTS(
    SELECT 1 FROM pg_class relation WHERE relation.oid=v_relation AND relation.relkind='r'
      AND relation.relrowsecurity AND relation.relforcerowsecurity
      AND pg_get_userbyid(relation.relowner)='sqlobserver_migrator') OR NOT EXISTS(
    SELECT 1 FROM pg_constraint fk JOIN pg_attribute col ON col.attrelid=fk.conrelid AND col.attnum=fk.conkey[1]
    WHERE fk.contype='f' AND fk.conrelid=v_relation AND fk.confrelid='telemetry.collection_run_identity'::regclass
      AND fk.convalidated AND NOT fk.condeferrable AND fk.confdeltype='a' AND col.attname=v_column) THEN
   hold_code:='cache_partition_unverified'; RETURN NEXT; CONTINUE;
  END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I WHERE instance_id=$1 AND %I=$2)',
    v_partition.partition_schema,v_partition.partition_name,v_column) INTO v_present USING p_instance_id,p_run_id;
  IF v_present THEN hold_code:='cache_partition_grace'; RETURN NEXT; END IF;
 END LOOP;
END $cache_holds$;
REVOKE ALL ON FUNCTION system.plan_cache_runtime_run_retention_holds(uuid,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.plan_cache_runtime_run_retention_holds(uuid,uuid) TO sqlobserver_migrator;

CREATE OR REPLACE FUNCTION system.query_performance_run_retention_holds(
 p_instance_id uuid,p_run_id uuid)
RETURNS TABLE(hold_code text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,events,telemetry,analytics
SET row_security=on SET TimeZone='UTC'
AS $holds$
DECLARE
 v_run events.query_performance_run%ROWTYPE;
 v_policy system.retention_policy%ROWTYPE;
 v_now timestamptz:=statement_timestamp();
 v_holds integer:=0;
BEGIN
 IF p_instance_id IS NULL OR p_run_id IS NULL
    OR current_setting('sqlobserver.target_scope',true)
       IS DISTINCT FROM p_instance_id::text
    OR NOT pg_has_role(current_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'M7 graph preview requires migrator and target scope'
   USING ERRCODE='42501';
 END IF;
 SELECT * INTO v_run FROM events.query_performance_run AS run
 WHERE run.collection_run_id=p_run_id AND run.instance_id=p_instance_id;
 IF NOT FOUND THEN
  hold_code:='run_missing'; RETURN NEXT; RETURN;
 END IF;
 SELECT * INTO v_policy FROM system.retention_policy AS policy
 WHERE policy.data_class='m7_query_performance'
   AND policy.parent_schema='events'
   AND policy.parent_table='query_performance_observation_v3';
 IF NOT EXISTS (
  SELECT 1 FROM system.query_observation_v3_read_route AS route
  WHERE route.singleton AND route.use_v3 AND route.write_v3) THEN
  hold_code:='writer_inactive'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF v_policy.data_class IS NULL OR NOT v_policy.enabled
    OR v_policy.retain_for IS NULL THEN
  hold_code:='policy_disabled'; RETURN NEXT; v_holds:=v_holds+1;
 ELSIF v_run.window_end>v_now-v_policy.retain_for-interval '1 day' THEN
  hold_code:='within_retention_window'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF NOT EXISTS (SELECT 1 FROM telemetry.collection_run_outcome AS outcome
                WHERE outcome.run_id=p_run_id) THEN
  hold_code:='outcome_missing'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF EXISTS (SELECT 1 FROM events.query_performance_observation AS observation
            WHERE observation.collection_run_id=p_run_id) THEN
  hold_code:='original_observation'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF EXISTS (SELECT 1 FROM events.query_performance_observation_v3 AS observation
            WHERE observation.instance_id=p_instance_id
              AND observation.interval_end>v_run.window_start
              AND observation.interval_end<=v_run.window_end
              AND observation.collection_run_id=p_run_id) THEN
  hold_code:='v3_observation'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 -- Detached partitions retain their FK to the query graph during grace.
 -- Deleting the parent could cascade into that still-readable relation.
 IF EXISTS (
  SELECT 1 FROM system.partition_registry AS registry
  WHERE registry.parent_schema='events'
    AND registry.parent_table='query_performance_observation_v3'
    AND registry.lifecycle_state='detached'
    AND registry.range_start<=v_run.window_end
    AND registry.range_end>v_run.window_start) THEN
  hold_code:='partition_grace'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF EXISTS (
  SELECT 1 FROM system.partition_reader_lease AS reader
  JOIN system.partition_registry AS registry
    ON registry.parent_schema=reader.parent_schema
   AND registry.parent_table=reader.parent_table
   AND registry.partition_name=reader.partition_name
  WHERE reader.parent_schema='events'
    AND reader.parent_table='query_performance_observation_v3'
    AND reader.released_at IS NULL AND reader.expires_at>v_now
    AND registry.range_start<=v_run.window_end
    AND registry.range_end>v_run.window_start) THEN
  hold_code:='reader_lease_active'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF system.has_pending_analytics_dependency(
     v_run.window_start,v_run.window_end) THEN
  hold_code:='analytics_dependency'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF EXISTS (
  SELECT 1 FROM analytics.evidence_packet_v2 AS packet
  WHERE packet.instance_id=p_instance_id AND packet.source_run_id=p_run_id) THEN
  hold_code:='incident_evidence'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 IF NOT EXISTS (SELECT 1 FROM system.recovery_attestation AS attestation
                WHERE attestation.valid AND attestation.expires_at>v_now) THEN
  hold_code:='recovery_attestation_missing'; RETURN NEXT;
  v_holds:=v_holds+1;
 END IF;
 -- A newly added inbound FK may represent a new retention dependency.
 IF EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  WHERE fk.contype='f' AND fk.conparentid=0
    AND fk.confrelid IN (
     'events.query_performance_run'::regclass,
     'events.query_performance_query'::regclass,
     'events.query_performance_plan'::regclass)
    AND fk.conrelid NOT IN (
     'events.query_performance_query'::regclass,
     'events.query_performance_database_status'::regclass,
     'events.query_performance_plan'::regclass,
     'events.query_performance_observation'::regclass,
     'events.query_performance_observation_v3'::regclass,
     'events.query_performance_content_link'::regclass,
     'events.query_performance_plan_content_link'::regclass,
     'events.query_performance_wait_snapshot'::regclass)) THEN
  hold_code:='unreviewed_foreign_key'; RETURN NEXT; v_holds:=v_holds+1;
 END IF;
 FOR hold_code IN SELECT cache.hold_code
  FROM system.plan_cache_runtime_run_retention_holds(p_instance_id,p_run_id) AS cache LOOP
  RETURN NEXT; v_holds:=v_holds+1;
 END LOOP;
 IF v_holds=0 THEN hold_code:='no_known_hold'; RETURN NEXT; END IF;
END $holds$;
REVOKE ALL ON FUNCTION system.query_performance_run_retention_holds(uuid,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.query_performance_run_retention_holds(uuid,uuid) TO sqlobserver_migrator;
