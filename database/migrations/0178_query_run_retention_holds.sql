-- Preview holds on one M7 run graph. This is not a deletion authorization:
-- the eventual fenced cleanup must re-evaluate every gate under its locks.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION system.query_performance_run_retention_holds(
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
    OR NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
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
 IF v_holds=0 THEN hold_code:='no_known_hold'; RETURN NEXT; END IF;
END $holds$;
REVOKE ALL ON FUNCTION system.query_performance_run_retention_holds(uuid,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.query_performance_run_retention_holds(uuid,uuid)
 TO sqlobserver_migrator;
