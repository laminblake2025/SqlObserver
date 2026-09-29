-- Retire bounded duplicate original headers only after the v2 rollback seal.
-- Canonical v2 rows, identity keys, outcomes, and child evidence remain intact.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_original_copy_retirement (
 batch_id uuid PRIMARY KEY,
 instance_id uuid NOT NULL,
 cutoff timestamptz NOT NULL,
 row_count integer NOT NULL CHECK(row_count BETWEEN 1 AND 1000),
 first_started_at timestamptz NOT NULL,
 last_started_at timestamptz NOT NULL,
 run_ids_digest bytea NOT NULL CHECK(octet_length(run_ids_digest)=32),
 recovery_attestation_id uuid NOT NULL,
 policy_snapshot jsonb NOT NULL,
 owner_execution_id uuid NOT NULL,
 fencing_token bigint NOT NULL,
 retired_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE system.collection_run_original_copy_retirement ENABLE ROW LEVEL SECURITY;
ALTER TABLE system.collection_run_original_copy_retirement FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_original_copy_retirement_migrator
 ON system.collection_run_original_copy_retirement FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON system.collection_run_original_copy_retirement
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_original_copy_retirement_append_only
 BEFORE UPDATE OR DELETE ON system.collection_run_original_copy_retirement
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

-- Keep UPDATE prohibited. DELETE is possible only inside the gated cleanup
-- transaction; the migrator remains the only role with table DELETE rights.
DROP TRIGGER collection_run_append_only ON telemetry.collection_run;
CREATE TRIGGER collection_run_append_only
 BEFORE UPDATE ON telemetry.collection_run FOR EACH STATEMENT
 EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION control.guard_collection_run_original_delete()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET row_security=on SET TimeZone='UTC'
AS $guard$
BEGIN
 IF current_setting('sqlobserver.original_run_copy_cleanup',true)
      IS DISTINCT FROM 'authorized'
    OR NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT EXISTS (
      SELECT 1 FROM system.collection_run_original_rollback_seal AS seal
      WHERE seal.singleton AND seal.sealed_at IS NOT NULL) THEN
  RAISE EXCEPTION 'original collection run deletion is guarded'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $guard$;
REVOKE ALL ON FUNCTION control.guard_collection_run_original_delete()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_delete_guard
 BEFORE DELETE ON telemetry.collection_run FOR EACH STATEMENT
 EXECUTE FUNCTION control.guard_collection_run_original_delete();

CREATE FUNCTION control.retire_collection_run_original_copies(
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
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member')
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
REVOKE ALL ON FUNCTION control.retire_collection_run_original_copies(
 uuid,timestamptz,uuid,bigint,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.retire_collection_run_original_copies(
 uuid,timestamptz,uuid,bigint,integer) TO sqlobserver_migrator;
