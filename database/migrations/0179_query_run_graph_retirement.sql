-- Retire one fully expired M7 run graph. The operator supplies the dedicated
-- retention lease; collection-run history and shared ciphertext remain intact.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- PL/pgSQL may evaluate every operand of the old AND expression. Accessing
-- OLD.instance_id on the plan table then fails during a legitimate cascade.
CREATE OR REPLACE FUNCTION events.reject_query_performance_mutation()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,events
AS $guard$
BEGIN
 IF TG_OP='UPDATE' AND TG_TABLE_SCHEMA='events'
    AND TG_TABLE_NAME='query_performance_observation'
    AND current_setting('sqlobserver.query_identity_backfill',true)='enabled'
    AND pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  IF OLD.instance_id IS NULL AND NEW.instance_id IS NOT NULL
     AND NEW.instance_id=nullif(
      current_setting('sqlobserver.target_scope',true),'')::uuid
     AND (to_jsonb(NEW)-'instance_id')=(to_jsonb(OLD)-'instance_id') THEN
   RETURN NEW;
  END IF;
 END IF;
 IF TG_OP IN ('UPDATE','DELETE')
    AND current_setting('sqlobserver.retention_context',true)
        ='envelope_cascade'
    AND pg_has_role(current_user,'sqlobserver_migrator','member') THEN
  RETURN OLD;
 END IF;
 RAISE EXCEPTION 'query performance evidence is append-only'
  USING ERRCODE='55000';
END $guard$;

CREATE TABLE system.query_run_retirement (
 run_id uuid PRIMARY KEY,
 instance_id uuid NOT NULL,
 window_end timestamptz NOT NULL,
 policy_revision bigint NOT NULL,
 recovery_attestation_id uuid NOT NULL,
 child_counts jsonb NOT NULL,
 child_count integer NOT NULL CHECK (child_count BETWEEN 0 AND 4096),
 fencing_token bigint NOT NULL CHECK (fencing_token>0),
 retired_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK (jsonb_typeof(child_counts)='object')
);
REVOKE ALL ON system.query_run_retirement FROM PUBLIC,sqlobserver_collector,
 sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION system.retire_query_performance_run(
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
    OR NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
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
REVOKE ALL ON FUNCTION system.retire_query_performance_run(
 uuid,uuid,uuid,bigint,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.retire_query_performance_run(
 uuid,uuid,uuid,bigint,integer) TO sqlobserver_migrator;
