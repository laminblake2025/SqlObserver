-- A failed partition drop records a retry, but the previous function only
-- accepted grace executions. Reopen that path after its immutable backoff.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION system.drop_m10_partition(p_execution_id uuid)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC'
AS $drop$
DECLARE
 e system.retention_execution%ROWTYPE;
 op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid;
 req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex');
 existing_digest bytea;
 retry_at timestamptz;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global'
    OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL
    OR op_id IS NULL OR coalesce(octet_length(req_digest),0)<>32 THEN
  RAISE EXCEPTION 'retention drop requires global SecurityAdministrator authorization'
   USING ERRCODE='42501';
 END IF;
 SELECT request_digest INTO existing_digest
 FROM control.m10_mutation_replay WHERE operation_id=op_id;
 IF FOUND THEN
  IF existing_digest<>req_digest THEN
   RAISE EXCEPTION 'divergent retention replay' USING ERRCODE='40001';
  END IF;
  RETURN true;
 END IF;
 SELECT * INTO e FROM system.retention_execution
 WHERE execution_id=p_execution_id FOR UPDATE;
 IF NOT FOUND OR e.state NOT IN ('grace','retry') OR e.attempt>=20
    OR e.drop_after>clock_timestamp() THEN
  RETURN false;
 END IF;
 IF e.state='retry' THEN
  SELECT next_attempt_at INTO retry_at FROM system.retention_drop_retry
  WHERE execution_id=e.execution_id ORDER BY retry_id DESC LIMIT 1;
  IF retry_at IS NULL OR retry_at>clock_timestamp() THEN RETURN false; END IF;
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(
  e.parent_schema::text||'.'||e.parent_table::text||'.'||e.partition_name::text,1));
 IF NOT EXISTS(SELECT 1 FROM system.retention_policy p
    WHERE p.data_class=e.data_class AND p.policy_revision=e.policy_revision
      AND p.enabled AND p.retain_for IS NOT NULL)
    OR NOT EXISTS(SELECT 1 FROM system.partition_registry r
      WHERE r.parent_schema=e.parent_schema AND r.parent_table=e.parent_table
        AND r.partition_name=e.partition_name AND r.lifecycle_state='detached'
        AND sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text||'|'
          ||r.partition_name::text||'|'||r.range_start::text||'|'
          ||r.range_end::text,'UTF8'))=e.registry_revision)
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease
      WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table
        AND partition_name=e.partition_name AND released_at IS NULL
        AND expires_at>clock_timestamp())
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation a
      WHERE a.valid AND a.expires_at>clock_timestamp())
    OR system.has_pending_analytics_dependency(e.range_start,e.range_end) THEN
  RETURN false;
 END IF;
 BEGIN
  EXECUTE format('DROP TABLE IF EXISTS %I.%I',e.parent_schema,e.partition_name);
  UPDATE system.partition_registry SET lifecycle_state='dropped',
    dropped_at=clock_timestamp()
  WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table
    AND partition_name=e.partition_name;
  UPDATE system.retention_execution SET state='dropped',
    dropped_at=clock_timestamp(),last_error=NULL WHERE execution_id=p_execution_id;
  INSERT INTO control.m10_mutation_replay
    (operation_id,action_name,instance_id,request_digest,result_digest,result)
  VALUES(op_id,'retention_drop',
    nullif(current_setting('sqlobserver.target_scope',true),'')::uuid,
    req_digest,sha256(req_digest),
    jsonb_build_object('executionId',p_execution_id,'state','dropped'));
  INSERT INTO audit.activity
    (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
     authorization_result,outcome,subject_kind,subject_identifier,
     correlation_id,parameter_digest,safe_details)
  VALUES(clock_timestamp(),op_id,'user',current_setting('sqlobserver.actor_sid',true),
    'retention.partition.drop','allowed','succeeded','partition',
    e.partition_name::text,
    nullif(current_setting('sqlobserver.retention_correlation_id',true),'')::uuid,
    req_digest,jsonb_build_object('dataClass',e.data_class,
      'policyRevision',e.policy_revision,
      'changeReason',current_setting('sqlobserver.retention_change_reason',true)));
  RETURN true;
 EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
 WHEN OTHERS THEN
  INSERT INTO system.retention_drop_retry
    (execution_id,error_code,error_detail,next_attempt_at)
  VALUES(p_execution_id,SQLSTATE,SQLERRM,clock_timestamp()+interval '1 hour');
  UPDATE system.retention_execution
  SET state=CASE WHEN attempt+1>=20 THEN 'failed' ELSE 'retry' END,
      attempt=attempt+1,last_error=SQLERRM
  WHERE execution_id=p_execution_id;
  RETURN false;
 END;
END $drop$;

REVOKE ALL ON FUNCTION system.drop_m10_partition(uuid)
FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.drop_m10_partition(uuid) TO sqlobserver_server;
