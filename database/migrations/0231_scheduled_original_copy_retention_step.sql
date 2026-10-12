-- Let the existing collector retention worker run an opted-in original-copy batch.
-- The direct target-scoped delete remains ungranted to the collector.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.guard_collection_run_original_delete()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET row_security=on SET TimeZone='UTC'
AS $guard$
BEGIN
 IF current_setting('sqlobserver.original_run_copy_cleanup',true)
      IS DISTINCT FROM 'authorized'
    OR NOT pg_has_role(current_user,'sqlobserver_migrator','member')
    OR NOT EXISTS (
      SELECT 1 FROM system.collection_run_original_rollback_seal AS seal
      WHERE seal.singleton AND seal.sealed_at IS NOT NULL) THEN
  RAISE EXCEPTION 'original collection run deletion is guarded'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $guard$;

CREATE OR REPLACE FUNCTION control.retire_collection_run_original_copies(
 p_instance_id uuid,p_before timestamptz,p_owner_execution_id uuid,
 p_fencing_token bigint,p_max_rows integer DEFAULT 1000)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system,audit
SET row_security=on SET TimeZone='UTC' SET lock_timeout='5s'
AS $retire$
DECLARE
 v_now timestamptz:=clock_timestamp();
 v_ids uuid[];
 v_count integer;
 v_matched integer;
 v_first timestamptz;
 v_last timestamptz;
 v_attestation uuid;
 v_prior_context text:=current_setting('sqlobserver.original_run_copy_cleanup',true);
 v_policy jsonb;
 v_batch uuid:=gen_random_uuid();
BEGIN
 IF p_instance_id IS NULL OR p_before IS NULL
    OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL
    OR p_max_rows IS NULL OR p_max_rows NOT BETWEEN 1 AND 1000
    OR p_before>v_now-interval '30 days' THEN
  RAISE EXCEPTION 'original collection run cleanup bounds are invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT (pg_has_role(session_user,'sqlobserver_migrator','member')
         OR (pg_has_role(session_user,'sqlobserver_collector','member')
             AND current_setting('sqlobserver.original_run_copy_scheduler',true)
                 ='authorized'))
    OR current_setting('sqlobserver.target_scope',true)
       IS DISTINCT FROM p_instance_id::text THEN
  RAISE EXCEPTION 'original collection run cleanup requires migrator and target scope'
   USING ERRCODE='42501';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 PERFORM 1 FROM system.collection_run_original_rollback_seal AS seal
 WHERE seal.singleton AND seal.sealed_at IS NOT NULL FOR SHARE;
 IF NOT FOUND OR NOT EXISTS (
  SELECT 1 FROM system.collection_run_identity_fk_cutover) THEN
  RAISE EXCEPTION 'collection run rollback and reference cutover are incomplete'
   USING ERRCODE='55000';
 END IF;
 IF EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  WHERE fk.contype='f'
    AND fk.confrelid='telemetry.collection_run'::regclass) THEN
  RAISE EXCEPTION 'original collection run has an inbound foreign key'
   USING ERRCODE='55000';
 END IF;
 IF EXISTS (
  SELECT 1 FROM pg_depend AS dependency
  JOIN pg_rewrite AS rewrite ON rewrite.oid=dependency.objid
  JOIN pg_class AS view_row ON view_row.oid=rewrite.ev_class
  JOIN pg_namespace AS namespace ON namespace.oid=view_row.relnamespace
  WHERE dependency.classid='pg_rewrite'::regclass
    AND dependency.refclassid='pg_class'::regclass
    AND dependency.refobjid='telemetry.collection_run'::regclass
    AND view_row.relkind='v'
    AND (namespace.nspname,view_row.relname) NOT IN (
      ('reporting','activity_snapshot_evidence_legacy'),
      ('reporting','collector_health_projection_legacy'),
      ('telemetry','collection_run_alert_read'))) THEN
  RAISE EXCEPTION 'original collection run has an unreviewed view'
   USING ERRCODE='55000';
 END IF;
 SELECT attestation.attestation_id INTO v_attestation
 FROM system.recovery_attestation AS attestation
 WHERE attestation.valid AND attestation.expires_at>v_now
 ORDER BY attestation.expires_at DESC,attestation.attestation_id
 LIMIT 1 FOR SHARE;
 IF v_attestation IS NULL THEN
  RAISE EXCEPTION 'original collection run cleanup requires recovery attestation'
   USING ERRCODE='55000';
 END IF;
 LOCK TABLE system.retention_policy IN SHARE MODE;
 SELECT coalesce(jsonb_agg(jsonb_build_object(
  'dataClass',policy.data_class,'parentSchema',policy.parent_schema,
  'parentTable',policy.parent_table,'revision',policy.policy_revision,
  'enabled',policy.enabled,'retainFor',policy.retain_for::text)
  ORDER BY policy.data_class,
  policy.parent_schema,policy.parent_table),'[]'::jsonb)
 INTO v_policy FROM system.retention_policy AS policy;

 -- Prevent a schedule from being rebound to one of the selected historical
 -- run IDs while this transaction checks and removes its duplicate header.
 PERFORM 1 FROM control.collector_schedule AS schedule
 WHERE schedule.instance_id=p_instance_id FOR SHARE;

 SELECT array_agg(candidate.run_id ORDER BY candidate.started_at,candidate.run_id),
        min(candidate.started_at),max(candidate.started_at)
 INTO v_ids,v_first,v_last
 FROM (
  SELECT original.run_id,original.started_at
  FROM telemetry.collection_run AS original
  JOIN telemetry.collection_run_outcome AS outcome
    ON outcome.run_id=original.run_id
  WHERE original.instance_id=p_instance_id
    AND original.started_at<p_before
    AND outcome.completed_at<=v_now-interval '30 days'
    AND NOT EXISTS (
      SELECT 1 FROM control.collector_schedule AS schedule
      WHERE schedule.active_run_id=original.run_id)
  ORDER BY original.started_at,original.run_id
  LIMIT p_max_rows FOR UPDATE OF original SKIP LOCKED
 ) AS candidate;
 v_count:=coalesce(cardinality(v_ids),0);
 IF v_count=0 THEN RETURN 0; END IF;
 SELECT count(*) INTO v_matched
 FROM telemetry.collection_run AS original
 JOIN telemetry.collection_run_identity AS identity
   ON identity.run_id=original.run_id
  AND identity.started_at=original.started_at
 JOIN telemetry.collection_run_v2 AS successor
   ON successor.run_id=identity.run_id
  AND successor.started_at=identity.started_at
 WHERE original.run_id=ANY(v_ids)
   AND original.instance_id=p_instance_id
   AND to_jsonb(original)=to_jsonb(successor);
 IF v_matched<>v_count THEN
  RAISE EXCEPTION 'original collection run copy differs from canonical run'
   USING ERRCODE='55000';
 END IF;
 IF NOT EXISTS (
  SELECT 1 FROM system.recovery_attestation AS attestation
  WHERE attestation.attestation_id=v_attestation
    AND attestation.valid AND attestation.expires_at>clock_timestamp()) THEN
  RAISE EXCEPTION 'original collection run recovery attestation expired'
   USING ERRCODE='55000';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 PERFORM set_config('sqlobserver.original_run_copy_cleanup','authorized',true);
 DELETE FROM telemetry.collection_run AS original
 WHERE original.run_id=ANY(v_ids) AND original.instance_id=p_instance_id;
 GET DIAGNOSTICS v_matched=ROW_COUNT;
 PERFORM set_config('sqlobserver.original_run_copy_cleanup',
  coalesce(v_prior_context,''),true);
 IF v_matched<>v_count THEN
  RAISE EXCEPTION 'original collection run batch changed during deletion'
   USING ERRCODE='40001';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 INSERT INTO system.collection_run_original_copy_retirement
  (batch_id,instance_id,cutoff,row_count,first_started_at,last_started_at,
   run_ids_digest,recovery_attestation_id,policy_snapshot,
   owner_execution_id,fencing_token)
 VALUES(v_batch,p_instance_id,p_before,v_count,v_first,v_last,
        sha256(convert_to(array_to_string(v_ids,','),'UTF8')),
        v_attestation,v_policy,p_owner_execution_id,p_fencing_token);
 INSERT INTO audit.activity
  (activity_id,actor_kind,actor_identifier,action_name,
   authorization_result,outcome,correlation_id,safe_details)
 VALUES(gen_random_uuid(),'system','migrator/retention',
        'retention.collection_run_original.retire','not_applicable',
        'succeeded',v_batch,jsonb_build_object(
         'targetId',p_instance_id,'cutoff',p_before,'rowCount',v_count,
         'recoveryAttestationId',v_attestation,'fencingToken',p_fencing_token));
 RETURN v_count;
END $retire$;

CREATE TABLE system.collection_run_original_copy_scan_cursor (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 instance_id uuid,
 last_result text CHECK(last_result IN ('idle','scanned','retired','blocked')),
 last_result_at timestamptz
);
INSERT INTO system.collection_run_original_copy_scan_cursor(singleton) VALUES(true);
ALTER TABLE system.collection_run_original_copy_scan_cursor ENABLE ROW LEVEL SECURITY;
ALTER TABLE system.collection_run_original_copy_scan_cursor FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_original_copy_scan_cursor_migrator
 ON system.collection_run_original_copy_scan_cursor FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON system.collection_run_original_copy_scan_cursor
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE TABLE system.collection_run_original_copy_blocked (
 instance_id uuid PRIMARY KEY,
 reason text NOT NULL,
 first_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 failure_count integer NOT NULL DEFAULT 1 CHECK(failure_count>0)
);
ALTER TABLE system.collection_run_original_copy_blocked ENABLE ROW LEVEL SECURITY;
ALTER TABLE system.collection_run_original_copy_blocked FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_original_copy_blocked_migrator
 ON system.collection_run_original_copy_blocked FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON system.collection_run_original_copy_blocked
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION system.run_collection_run_original_copy_retention_step(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,telemetry
SET row_security=on SET TimeZone='UTC' SET lock_timeout='5s'
AS $scan$
DECLARE
 v_policy system.retention_policy%ROWTYPE;
 v_cursor system.collection_run_original_copy_scan_cursor%ROWTYPE;
 v_old_scope text:=current_setting('sqlobserver.target_scope',true);
 v_old_scheduler text:=current_setting('sqlobserver.original_run_copy_scheduler',true);
 v_target uuid;
 v_last_target uuid;
 v_checked integer:=0;
 v_count integer;
 v_reason text;
BEGIN
 IF p_owner_execution_id IS NULL OR p_fencing_token IS NULL
    OR NOT pg_has_role(session_user,'sqlobserver_collector','member') THEN
  RAISE EXCEPTION 'original copy retention step requires collector'
   USING ERRCODE='42501';
 END IF;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 SELECT * INTO STRICT v_policy FROM system.retention_policy AS policy
 WHERE policy.data_class='collection_run_original_copy'
   AND policy.parent_schema='telemetry'
   AND policy.parent_table='collection_run' FOR SHARE;
 IF NOT v_policy.enabled OR v_policy.retain_for IS NULL
    OR v_policy.retain_for<interval '30 days'
    OR NOT EXISTS (
      SELECT 1 FROM system.collection_run_original_rollback_seal AS seal
      WHERE seal.singleton AND seal.sealed_at IS NOT NULL)
    OR NOT EXISTS (
      SELECT 1 FROM system.recovery_attestation AS attestation
      WHERE attestation.valid AND attestation.expires_at>clock_timestamp()) THEN
  RETURN 'idle';
 END IF;
 SELECT * INTO STRICT v_cursor
 FROM system.collection_run_original_copy_scan_cursor
 WHERE singleton FOR UPDATE;
 FOR v_target IN
  SELECT target.instance_id FROM control.observation_target AS target
  WHERE v_cursor.instance_id IS NULL
     OR target.instance_id>v_cursor.instance_id
  ORDER BY target.instance_id LIMIT 32
 LOOP
  v_checked:=v_checked+1;
  v_last_target:=v_target;
  BEGIN
   PERFORM set_config('sqlobserver.target_scope',v_target::text,true);
   PERFORM set_config('sqlobserver.original_run_copy_scheduler','authorized',true);
   v_count:=control.retire_collection_run_original_copies(
    v_target,clock_timestamp()-v_policy.retain_for,
    p_owner_execution_id,p_fencing_token,100);
  EXCEPTION WHEN SQLSTATE '55000' THEN
   v_reason:=left(SQLERRM,256);
   INSERT INTO system.collection_run_original_copy_blocked
    (instance_id,reason)
   VALUES(v_target,v_reason)
   ON CONFLICT(instance_id) DO UPDATE
   SET reason=excluded.reason,last_seen_at=clock_timestamp(),
       failure_count=system.collection_run_original_copy_blocked.failure_count+1;
   UPDATE system.collection_run_original_copy_scan_cursor
   SET instance_id=v_target,last_result='blocked',
       last_result_at=clock_timestamp() WHERE singleton;
   PERFORM set_config('sqlobserver.target_scope',coalesce(v_old_scope,''),true);
   PERFORM set_config('sqlobserver.original_run_copy_scheduler',
    coalesce(v_old_scheduler,''),true);
   RETURN 'blocked';
  END;
  PERFORM set_config('sqlobserver.target_scope',coalesce(v_old_scope,''),true);
  PERFORM set_config('sqlobserver.original_run_copy_scheduler',
   coalesce(v_old_scheduler,''),true);
  DELETE FROM system.collection_run_original_copy_blocked
  WHERE instance_id=v_target;
  IF v_count>0 THEN
   UPDATE system.collection_run_original_copy_scan_cursor
   SET instance_id=v_target,last_result='retired',
       last_result_at=clock_timestamp() WHERE singleton;
   PERFORM control.assert_worker_lease(
    'retention/maintenance',p_owner_execution_id,p_fencing_token);
   RETURN 'retired';
  END IF;
 END LOOP;
 IF v_checked=32 THEN
  UPDATE system.collection_run_original_copy_scan_cursor
  SET instance_id=v_last_target,last_result='scanned',
      last_result_at=clock_timestamp() WHERE singleton;
  RETURN 'scanned';
 END IF;
 UPDATE system.collection_run_original_copy_scan_cursor
 SET instance_id=NULL,last_result='idle',last_result_at=clock_timestamp()
 WHERE singleton;
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 RETURN 'idle';
END $scan$;
REVOKE ALL ON FUNCTION system.run_collection_run_original_copy_retention_step(uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.run_collection_run_original_copy_retention_step(uuid,bigint)
 TO sqlobserver_collector;