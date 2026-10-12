-- Explicit, identity-bound backup-age policy; no assumed business RPO.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE control.backup_freshness_policy
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 database_fingerprint bytea NOT NULL CHECK(octet_length(database_fingerprint)=32),
 policy_revision bigint NOT NULL CHECK(policy_revision>0),
 enabled boolean NOT NULL,
 maximum_full_age_seconds integer NOT NULL CHECK(maximum_full_age_seconds BETWEEN 1 AND 2592000),
 maximum_log_age_seconds integer CHECK(maximum_log_age_seconds BETWEEN 1 AND 2592000),
 maximum_observation_age_seconds integer NOT NULL CHECK(maximum_observation_age_seconds BETWEEN 1 AND 3600),
 full_requirement smallint NOT NULL CHECK(full_requirement IN (1,2)),
 require_checksums boolean NOT NULL,
 inventory_run_id uuid,
 operation_id uuid NOT NULL UNIQUE CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
 request_sha256 bytea NOT NULL CHECK(octet_length(request_sha256)=32),
 audit_activity_id uuid NOT NULL,
 audit_occurred_at timestamptz NOT NULL CHECK(isfinite(audit_occurred_at)),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp() CHECK(isfinite(recorded_at)),
 PRIMARY KEY(instance_id,target_revision,database_fingerprint,policy_revision)
);
ALTER TABLE control.backup_freshness_policy ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.backup_freshness_policy FORCE ROW LEVEL SECURITY;
CREATE POLICY backup_freshness_policy_owner ON control.backup_freshness_policy
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER backup_freshness_policy_append_only BEFORE UPDATE OR DELETE
 ON control.backup_freshness_policy FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON control.backup_freshness_policy FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.set_backup_freshness_policy(
 p_instance uuid,p_target_revision bigint,p_database bytea,p_expected_revision bigint,p_operation uuid,
 p_enabled boolean,p_full_seconds integer,p_log_seconds integer,p_observation_seconds integer,
 p_requirement integer,p_checksums boolean,p_actor text,p_correlation uuid)
RETURNS TABLE(result_status text,policy_revision bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $policy$
DECLARE target control.observation_target%ROWTYPE; current_revision bigint;
 replay control.backup_freshness_policy%ROWTYPE; scan telemetry.backup_v2_scan%ROWTYPE;
 inventory telemetry.backup_v2_database%ROWTYPE; digest bytea; audit_id uuid; audit_at timestamptz;
 failure_status text; failure_reason text; now_utc timestamptz;
BEGIN
 IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'
   OR p_target_revision IS NULL OR p_target_revision<1 OR octet_length(p_database) IS DISTINCT FROM 32
   OR p_expected_revision IS NULL OR p_expected_revision<0 OR p_expected_revision=9223372036854775807
   OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000'
   OR p_enabled IS NULL OR p_full_seconds IS NULL OR p_full_seconds NOT BETWEEN 1 AND 2592000
   OR p_log_seconds IS NOT NULL AND p_log_seconds NOT BETWEEN 1 AND 2592000
   OR p_observation_seconds IS NULL OR p_observation_seconds NOT BETWEEN 1 AND 3600
   OR p_requirement IS NULL OR p_requirement NOT IN (1,2) OR p_checksums IS NULL
   OR p_actor IS NULL OR p_correlation IS NULL
 THEN RAISE EXCEPTION 'Backup freshness policy is invalid' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:backup-policy-operation:'||p_operation::text,0));
 SELECT * INTO target FROM control.observation_target WHERE instance_id=p_instance FOR UPDATE;
 IF NOT FOUND THEN failure_status:='not_found'; failure_reason:='target_not_found';
 ELSIF target.lifecycle_state<>'active' THEN
  failure_status:='inactive'; failure_reason:=CASE WHEN target.lifecycle_state='retired' THEN 'target_retired' ELSE 'revision_conflict' END;
 ELSIF target.revision<>p_target_revision THEN failure_status:='revision_conflict'; failure_reason:='revision_conflict';
 END IF;
 IF failure_status IS NOT NULL THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted',
    CASE WHEN failure_status='not_found' THEN 'failed' ELSE 'conflict' END,failure_reason);
  RETURN QUERY SELECT failure_status,NULL::bigint; RETURN;
 END IF;
 SELECT coalesce(max(policy.policy_revision),0) INTO current_revision FROM control.backup_freshness_policy policy
 WHERE policy.instance_id=p_instance AND policy.target_revision=p_target_revision AND policy.database_fingerprint=p_database;
 digest:=sha256(convert_to(jsonb_build_array(p_instance,p_target_revision,encode(p_database,'hex'),p_expected_revision,p_operation,
   p_enabled,p_full_seconds,p_log_seconds,p_observation_seconds,p_requirement,p_checksums,p_actor)::text,'UTF8'));
 SELECT * INTO replay FROM control.backup_freshness_policy WHERE operation_id=p_operation;
 IF FOUND THEN
  IF replay.request_sha256=digest AND replay.instance_id=p_instance AND replay.target_revision=p_target_revision
    AND replay.database_fingerprint=p_database AND replay.policy_revision=current_revision THEN
   RETURN QUERY SELECT 'replayed'::text,current_revision; RETURN;
  END IF;
  failure_status:='revision_conflict';
 ELSIF current_revision<>p_expected_revision THEN failure_status:='revision_conflict';
 END IF;
 IF failure_status IS NULL AND (p_enabled OR current_revision=0) THEN
  now_utc:=clock_timestamp();
  SELECT * INTO scan FROM telemetry.backup_v2_scan observation
   WHERE observation.instance_id=p_instance AND observation.target_revision=p_target_revision
   ORDER BY observation.received_at DESC,observation.run_id LIMIT 1;
  IF NOT FOUND OR scan.coverage NOT IN (1,2) OR scan.source_observed_at IS NULL
    OR scan.received_at>now_utc OR scan.received_at<now_utc-interval '1 hour'
    OR scan.source_observed_at>now_utc OR scan.source_observed_at<now_utc-interval '1 hour' THEN
   failure_status:='evidence_unavailable';
  ELSE
   SELECT * INTO inventory FROM telemetry.backup_v2_database item
    WHERE item.instance_id=p_instance AND item.target_revision=p_target_revision
      AND item.received_at=scan.received_at AND item.run_id=scan.run_id AND item.database_fingerprint=p_database;
   IF NOT FOUND THEN failure_status:='database_not_observed';
   ELSIF inventory.coverage<>1 THEN failure_status:='evidence_unavailable';
   ELSIF p_enabled AND inventory.recovery_model IN (2,3) AND p_log_seconds IS NULL THEN failure_status:='log_threshold_required';
   END IF;
  END IF;
 END IF;
 IF failure_status IS NOT NULL THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict','revision_conflict');
  RETURN QUERY SELECT failure_status,nullif(current_revision,0); RETURN;
 END IF;
 SELECT audit_activity_id,repository_time INTO audit_id,audit_at FROM audit.append_administrative_activity(
  p_actor,p_correlation,'update_observation_target',p_instance,'granted','succeeded','completed');
 INSERT INTO control.backup_freshness_policy(instance_id,target_revision,database_fingerprint,policy_revision,
  enabled,maximum_full_age_seconds,maximum_log_age_seconds,maximum_observation_age_seconds,full_requirement,
  require_checksums,inventory_run_id,operation_id,request_sha256,audit_activity_id,audit_occurred_at)
 VALUES(p_instance,p_target_revision,p_database,current_revision+1,p_enabled,p_full_seconds,p_log_seconds,p_observation_seconds,
  p_requirement::smallint,p_checksums,scan.run_id,p_operation,digest,audit_id,audit_at);
 RETURN QUERY SELECT 'applied'::text,current_revision+1;
END;
$policy$;
REVOKE ALL ON FUNCTION control.set_backup_freshness_policy(uuid,bigint,bytea,bigint,uuid,boolean,integer,integer,integer,integer,boolean,text,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_backup_freshness_policy(uuid,bigint,bytea,bigint,uuid,boolean,integer,integer,integer,integer,boolean,text,uuid)
 TO sqlobserver_server;

CREATE FUNCTION control.read_backup_freshness_policy(p_instance uuid,p_target_revision bigint,p_database bytea,p_at timestamptz)
RETURNS TABLE(policy_revision bigint,enabled boolean,maximum_full_age_seconds integer,maximum_log_age_seconds integer,
 maximum_observation_age_seconds integer,full_requirement smallint,require_checksums boolean,recorded_at timestamptz)
LANGUAGE sql SECURITY DEFINER STABLE PARALLEL UNSAFE SET search_path=pg_catalog AS $read$
 SELECT policy.policy_revision,policy.enabled,policy.maximum_full_age_seconds,policy.maximum_log_age_seconds,
  policy.maximum_observation_age_seconds,policy.full_requirement,policy.require_checksums,policy.recorded_at
 FROM control.backup_freshness_policy policy WHERE policy.instance_id=p_instance AND policy.target_revision=p_target_revision
  AND policy.database_fingerprint=p_database AND policy.recorded_at<=p_at
 ORDER BY policy.policy_revision DESC LIMIT 1;
$read$;
REVOKE ALL ON FUNCTION control.read_backup_freshness_policy(uuid,bigint,bytea,timestamptz) FROM PUBLIC,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.read_backup_freshness_policy(uuid,bigint,bytea,timestamptz) TO sqlobserver_server,sqlobserver_collector;
