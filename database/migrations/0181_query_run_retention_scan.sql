-- Let the scheduled collector invoke the existing gated M7 graph retirement
-- only through a fenced, bounded SECURITY DEFINER scan.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

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
 IF v_holds=0 THEN hold_code:='no_known_hold'; RETURN NEXT; END IF;
END $holds$;

CREATE OR REPLACE FUNCTION system.retire_query_performance_run(
 p_instance_id uuid,p_run_id uuid,p_owner_execution_id uuid,
 p_fencing_token bigint,p_max_children integer DEFAULT 4096)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,events,telemetry,analytics,audit
SET row_security=on SET TimeZone='UTC'
AS $retire$
DECLARE
 v_run events.query_performance_run%ROWTYPE;
 v_policy system.retention_policy%ROWTYPE;
 v_attestation uuid;
 v_hold text;
 v_count integer;
 v_total integer:=0;
 v_counts jsonb:='{}'::jsonb;
 v_prior_context text:=current_setting('sqlobserver.retention_context',true);
 v_table name;
 v_expected integer;
BEGIN
 IF p_instance_id IS NULL OR p_run_id IS NULL
    OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL
    OR p_max_children IS NULL OR p_max_children NOT BETWEEN 1 AND 4096
    OR current_setting('sqlobserver.target_scope',true)
       IS DISTINCT FROM p_instance_id::text
    OR NOT pg_has_role(current_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'M7 graph retirement requires migrator and target scope'
   USING ERRCODE='42501';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 -- The collector takes this row lock before appending late plan links/waits.
 PERFORM 1 FROM telemetry.collection_run AS collection
 WHERE collection.run_id=p_run_id AND collection.instance_id=p_instance_id
 FOR UPDATE;
 IF NOT FOUND THEN RETURN 'missing'; END IF;
 SELECT * INTO v_run FROM events.query_performance_run AS run
 WHERE run.collection_run_id=p_run_id AND run.instance_id=p_instance_id
 FOR UPDATE;
 IF NOT FOUND THEN RETURN 'missing'; END IF;
 SELECT * INTO v_policy FROM system.retention_policy AS policy
 WHERE policy.data_class='m7_query_performance'
   AND policy.parent_schema='events'
   AND policy.parent_table='query_performance_observation_v3'
 FOR SHARE;
 -- Holding these registry rows prevents a detached partition from being
 -- dropped or reattached between the hold check and graph removal.
 PERFORM 1 FROM system.partition_registry AS registry
 WHERE registry.parent_schema='events'
   AND registry.parent_table='query_performance_observation_v3'
   AND registry.range_start<=v_run.window_end
   AND registry.range_end>v_run.window_start
 FOR SHARE;
 PERFORM 1 FROM events.query_performance_query AS query_row
 WHERE query_row.collection_run_id=p_run_id FOR UPDATE;
 PERFORM 1 FROM events.query_performance_plan AS plan_row
 WHERE plan_row.collection_run_id=p_run_id FOR UPDATE;
 SELECT hold_code INTO v_hold
 FROM system.query_performance_run_retention_holds(p_instance_id,p_run_id)
 WHERE hold_code<>'no_known_hold' LIMIT 1;
 IF v_hold IS NOT NULL THEN RETURN 'held'; END IF;
 IF v_policy.data_class IS NULL THEN RETURN 'held'; END IF;
 SELECT attestation.attestation_id INTO v_attestation
 FROM system.recovery_attestation AS attestation
 WHERE attestation.valid AND attestation.expires_at>clock_timestamp()
 ORDER BY attestation.expires_at DESC,attestation.attestation_id
 LIMIT 1 FOR SHARE;
 IF v_attestation IS NULL THEN RETURN 'held'; END IF;
 -- A stale stage row carries raw query data even though it has no FK.
 -- Count every row that will be removed before changing the graph.
 FOREACH v_table IN ARRAY ARRAY[
  'query_performance_stage','query_performance_content_link',
  'query_performance_plan_content_link','query_performance_wait_snapshot',
  'query_performance_database_status','query_performance_observation',
  'query_performance_plan','query_performance_query']::name[] LOOP
  EXECUTE format('SELECT count(*) FROM events.%I WHERE collection_run_id=$1',
                 v_table) INTO v_count USING p_run_id;
  v_total:=v_total+v_count;
  IF v_total>p_max_children THEN RETURN 'oversize'; END IF;
  v_counts:=v_counts||jsonb_build_object(v_table::text,v_count);
 END LOOP;
 -- Fail closed if a later migration introduces another dependent relation.
 IF EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  WHERE fk.contype='f' AND fk.conparentid=0
    AND fk.confrelid IN (
     'events.query_performance_run'::regclass,
     'events.query_performance_query'::regclass,
     'events.query_performance_plan'::regclass,
     'events.query_performance_observation'::regclass,
     'events.query_performance_database_status'::regclass,
     'events.query_performance_content_link'::regclass,
     'events.query_performance_plan_content_link'::regclass,
     'events.query_performance_wait_snapshot'::regclass)
    AND (fk.conrelid,fk.confrelid) NOT IN (
     ('events.query_performance_query'::regclass,
      'events.query_performance_run'::regclass),
     ('events.query_performance_database_status'::regclass,
      'events.query_performance_run'::regclass),
     ('events.query_performance_plan'::regclass,
      'events.query_performance_query'::regclass),
     ('events.query_performance_observation'::regclass,
      'events.query_performance_query'::regclass),
     ('events.query_performance_observation_v3'::regclass,
      'events.query_performance_query'::regclass),
     ('events.query_performance_content_link'::regclass,
      'events.query_performance_query'::regclass),
     ('events.query_performance_plan_content_link'::regclass,
      'events.query_performance_plan'::regclass),
     ('events.query_performance_plan_content_link'::regclass,
      'events.query_performance_query'::regclass),
     ('events.query_performance_wait_snapshot'::regclass,
      'events.query_performance_plan'::regclass),
     ('events.query_performance_wait_snapshot'::regclass,
      'events.query_performance_query'::regclass))) THEN
  RETURN 'held';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 PERFORM set_config('sqlobserver.retention_context','envelope_cascade',true);
 FOREACH v_table IN ARRAY ARRAY[
  'query_performance_stage','query_performance_content_link',
  'query_performance_plan_content_link','query_performance_wait_snapshot',
  'query_performance_database_status','query_performance_observation',
  'query_performance_plan','query_performance_query']::name[] LOOP
  EXECUTE format('DELETE FROM events.%I WHERE collection_run_id=$1',
                 v_table) USING p_run_id;
  GET DIAGNOSTICS v_count=ROW_COUNT;
  v_expected:=(v_counts->>v_table::text)::integer;
  IF v_count<>v_expected THEN
   RAISE EXCEPTION 'M7 graph changed during retirement' USING ERRCODE='40001';
  END IF;
 END LOOP;
 DELETE FROM events.query_performance_run AS run
 WHERE run.collection_run_id=p_run_id AND run.instance_id=p_instance_id;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>1 THEN
  RAISE EXCEPTION 'M7 run changed during retirement' USING ERRCODE='40001';
 END IF;
 PERFORM set_config('sqlobserver.retention_context',
  coalesce(v_prior_context,''),true);
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 INSERT INTO system.query_run_retirement
  (run_id,instance_id,window_end,policy_revision,recovery_attestation_id,
   child_counts,child_count,fencing_token)
 VALUES(p_run_id,p_instance_id,v_run.window_end,v_policy.policy_revision,
        v_attestation,v_counts,v_total,p_fencing_token);
 INSERT INTO audit.activity
  (activity_id,actor_kind,actor_identifier,action_name,
   authorization_result,outcome,correlation_id,safe_details)
 VALUES(gen_random_uuid(),'system','collector/retention',
        'retention.query_run.retire','not_applicable','succeeded',
        gen_random_uuid(),jsonb_build_object(
         'runId',p_run_id,'childCount',v_total,
         'policyRevision',v_policy.policy_revision,
         'recoveryAttestationId',v_attestation,
         'fencingToken',p_fencing_token));
 RETURN 'retired';
END $retire$;

-- A cursor can point to the last examined run or to the last target in a
-- 32-target empty scan. Revisit held runs on the next complete sweep.
CREATE TABLE system.query_run_retention_scan_cursor (
 singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
 instance_id uuid,
 window_end timestamptz,
 run_id uuid,
 last_result text,
 last_result_at timestamptz,
 CHECK ((window_end IS NULL AND run_id IS NULL)
        OR (instance_id IS NOT NULL AND window_end IS NOT NULL
            AND run_id IS NOT NULL)),
 CHECK (last_result IS NULL OR last_result IN
        ('idle','scanned','held','oversize','retired','missing'))
);
INSERT INTO system.query_run_retention_scan_cursor(singleton) VALUES(true);
REVOKE ALL ON system.query_run_retention_scan_cursor
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE TABLE system.query_run_retention_blocked (
 run_id uuid PRIMARY KEY,
 instance_id uuid NOT NULL,
 reason text NOT NULL CHECK (reason='oversize'),
 first_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK (last_seen_at>=first_seen_at)
);
REVOKE ALL ON system.query_run_retention_blocked
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION system.run_query_run_retention_step(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,events
SET row_security=on SET TimeZone='UTC'
AS $scan$
DECLARE
 v_cursor system.query_run_retention_scan_cursor%ROWTYPE;
 v_policy system.retention_policy%ROWTYPE;
 v_cutoff timestamptz;
 v_old_scope text:=current_setting('sqlobserver.target_scope',true);
 v_target uuid;
 v_last_target uuid;
 v_run uuid;
 v_end timestamptz;
 v_checked integer:=0;
 v_result text;
BEGIN
 IF p_owner_execution_id IS NULL OR p_fencing_token IS NULL
    OR NOT pg_has_role(session_user,'sqlobserver_collector','member') THEN
  RAISE EXCEPTION 'M7 retention scan requires collector'
   USING ERRCODE='42501';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 SELECT * INTO STRICT v_cursor
 FROM system.query_run_retention_scan_cursor WHERE singleton FOR UPDATE;
 SELECT * INTO v_policy FROM system.retention_policy AS policy
 WHERE policy.data_class='m7_query_performance'
   AND policy.parent_schema='events'
   AND policy.parent_table='query_performance_observation_v3'
 FOR SHARE;
 IF v_policy.data_class IS NULL OR NOT v_policy.enabled
    OR v_policy.retain_for IS NULL
    OR NOT EXISTS (
     SELECT 1 FROM system.query_observation_v3_read_route AS route
     WHERE route.singleton AND route.use_v3 AND route.write_v3)
    OR NOT EXISTS (
     SELECT 1 FROM system.recovery_attestation AS attestation
     WHERE attestation.valid AND attestation.expires_at>clock_timestamp()) THEN
  RETURN 'idle';
 END IF;
 v_cutoff:=clock_timestamp()-v_policy.retain_for-interval '1 day';
 IF v_cursor.instance_id IS NOT NULL AND v_cursor.run_id IS NOT NULL THEN
  v_target:=v_cursor.instance_id;
  PERFORM set_config('sqlobserver.target_scope',v_target::text,true);
  SELECT run.collection_run_id,run.window_end INTO v_run,v_end
  FROM events.query_performance_run AS run
  WHERE run.instance_id=v_target AND run.window_end<=v_cutoff
    AND (run.window_end,run.collection_run_id)>
        (v_cursor.window_end,v_cursor.run_id)
  ORDER BY run.window_end,run.collection_run_id LIMIT 1;
 END IF;
 IF v_run IS NULL THEN
  FOR v_target IN
   SELECT target.instance_id FROM control.observation_target AS target
   WHERE v_cursor.instance_id IS NULL
      OR target.instance_id>v_cursor.instance_id
   ORDER BY target.instance_id LIMIT 32
  LOOP
   v_checked:=v_checked+1;
   v_last_target:=v_target;
   PERFORM set_config('sqlobserver.target_scope',v_target::text,true);
   SELECT run.collection_run_id,run.window_end INTO v_run,v_end
   FROM events.query_performance_run AS run
   WHERE run.instance_id=v_target AND run.window_end<=v_cutoff
   ORDER BY run.window_end,run.collection_run_id LIMIT 1;
   EXIT WHEN v_run IS NOT NULL;
  END LOOP;
 END IF;
 IF v_run IS NOT NULL THEN
  v_result:=system.retire_query_performance_run(
   v_target,v_run,p_owner_execution_id,p_fencing_token,4096);
  IF v_result='oversize' THEN
   INSERT INTO system.query_run_retention_blocked
    (run_id,instance_id,reason)
   VALUES(v_run,v_target,'oversize')
   ON CONFLICT (run_id) DO UPDATE
   SET last_seen_at=clock_timestamp();
  ELSIF v_result IN ('retired','missing') THEN
   DELETE FROM system.query_run_retention_blocked
   WHERE run_id=v_run AND instance_id=v_target;
  END IF;
  UPDATE system.query_run_retention_scan_cursor
  SET instance_id=v_target,window_end=v_end,run_id=v_run,
      last_result=v_result,last_result_at=clock_timestamp()
  WHERE singleton;
  PERFORM set_config('sqlobserver.target_scope',coalesce(v_old_scope,''),true);
  RETURN v_result;
 END IF;
 IF v_checked=32 THEN
  UPDATE system.query_run_retention_scan_cursor
  SET instance_id=v_last_target,window_end=NULL,run_id=NULL,
      last_result='scanned',last_result_at=clock_timestamp()
  WHERE singleton;
  v_result:='scanned';
 ELSE
  UPDATE system.query_run_retention_scan_cursor
  SET instance_id=NULL,window_end=NULL,run_id=NULL,
      last_result='idle',last_result_at=clock_timestamp()
  WHERE singleton;
  v_result:='idle';
 END IF;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_old_scope,''),true);
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 RETURN v_result;
END $scan$;
REVOKE ALL ON FUNCTION system.run_query_run_retention_step(uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.run_query_run_retention_step(uuid,bigint)
 TO sqlobserver_collector;
