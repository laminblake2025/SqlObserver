-- Pending analytics jobs protect only partitions whose UTC windows they may
-- still read or write. Missing or malformed job windows remain a global hold.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION system.has_pending_analytics_dependency(p_range_start timestamptz,p_range_end timestamptz)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control
AS $$
 SELECT CASE WHEN p_range_start IS NULL OR p_range_end IS NULL OR p_range_end<=p_range_start THEN true
 ELSE EXISTS (
  SELECT 1 FROM control.analytics_job j
  WHERE (j.status IN ('queued','running','partial') OR (j.status='failed' AND j.attempt>=5))
    AND j.job_kind<>'retention'
    AND (j.from_utc IS NULL OR j.to_utc IS NULL OR j.to_utc<=j.from_utc
      OR (j.from_utc<p_range_end AND greatest(j.to_utc,coalesce(j.source_cutoff_utc,j.to_utc))>p_range_start))
 ) END;
$$;
REVOKE ALL ON FUNCTION system.has_pending_analytics_dependency(timestamptz,timestamptz)
FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION system.preview_m10_retention(p_now_utc timestamptz DEFAULT clock_timestamp(),p_cursor text DEFAULT NULL)
RETURNS TABLE(data_class text,parent_schema name,parent_table name,partition_name name,range_start timestamptz,range_end timestamptz,eligible boolean,reason text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,system,control SET TimeZone='UTC'
AS $$
 WITH c AS (
   SELECT CASE WHEN p_cursor IS NULL THEN NULL::jsonb ELSE convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb END AS j
 ), rows AS (
 SELECT p.data_class,r.parent_schema,r.parent_table,r.partition_name,r.range_start,r.range_end,
   (p.enabled AND p.retain_for IS NOT NULL AND r.lifecycle_state='attached' AND r.range_end<=p_now_utc-p.retain_for AND r.range_start<=p_now_utc-interval '1 day' AND (SELECT count(*) FROM system.partition_registry newer WHERE newer.parent_schema=r.parent_schema AND newer.parent_table=r.parent_table AND newer.lifecycle_state='attached' AND newer.range_start>=r.range_start) > p.minimum_partitions_to_keep AND (SELECT count(*) FROM system.partition_reader_lease l WHERE l.parent_schema=r.parent_schema AND l.parent_table=r.parent_table AND l.partition_name=r.partition_name AND l.released_at IS NULL AND l.expires_at>p_now_utc)=0 AND NOT EXISTS(SELECT 1 FROM system.retention_backfill_state b WHERE b.parent_schema=r.parent_schema AND b.parent_table=r.parent_table AND b.partition_name=r.partition_name AND b.state<>'complete') AND NOT system.has_pending_analytics_dependency(r.range_start,r.range_end) AND EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>p_now_utc)) AS eligible,
   CASE WHEN NOT p.enabled OR p.retain_for IS NULL THEN 'retention_disabled' WHEN r.lifecycle_state<>'attached' THEN 'partition_not_attached' WHEN r.range_end>p_now_utc-p.retain_for THEN 'within_retention_window' WHEN (SELECT count(*) FROM system.partition_registry newer WHERE newer.parent_schema=r.parent_schema AND newer.parent_table=r.parent_table AND newer.lifecycle_state='attached' AND newer.range_start>=r.range_start) <= p.minimum_partitions_to_keep THEN 'minimum_partition_floor' WHEN EXISTS(SELECT 1 FROM system.partition_reader_lease l WHERE l.partition_name=r.partition_name AND l.released_at IS NULL AND l.expires_at>p_now_utc) THEN 'reader_lease_active' WHEN EXISTS(SELECT 1 FROM system.retention_backfill_state b WHERE b.partition_name=r.partition_name AND b.state<>'complete') THEN 'backfill_incomplete' WHEN system.has_pending_analytics_dependency(r.range_start,r.range_end) THEN 'dependency_pending' WHEN NOT EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>p_now_utc) THEN 'recovery_attestation_missing' ELSE 'eligible' END
  FROM system.retention_policy p JOIN system.partition_registry r ON r.parent_schema=p.parent_schema AND r.parent_table=p.parent_table CROSS JOIN c
  WHERE p_cursor IS NULL OR (c.j->>'v')='m10.retention.v1' AND (c.j->>'o')='dataClass,parentSchema,parentTable,partitionName'
    AND (p.data_class,r.parent_schema::text,r.parent_table::text,r.partition_name::text) > (c.j->>'d',c.j->>'s1',c.j->>'t',c.j->>'p')
 ) SELECT * FROM rows ORDER BY data_class,parent_schema,parent_table,partition_name;
$$;
REVOKE ALL ON FUNCTION system.preview_m10_retention(timestamptz,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION system.preview_m10_retention(timestamptz,text) TO sqlobserver_server,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION system.detach_m10_partition(p_parent_schema name,p_parent_table name,p_partition_name name,p_execution_id uuid)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC' AS $detach$
DECLARE r system.partition_registry%ROWTYPE; p system.retention_policy%ROWTYPE; op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid; req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex'); existing_digest bytea;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator' OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL THEN RAISE EXCEPTION 'retention detach requires global SecurityAdministrator authorization' USING ERRCODE='42501'; END IF;
 IF p_execution_id IS NULL OR op_id IS NULL OR coalesce(octet_length(req_digest),0)<>32 THEN RAISE EXCEPTION 'retention execution fence is required' USING ERRCODE='22023'; END IF;
 SELECT request_digest INTO existing_digest FROM control.m10_mutation_replay WHERE operation_id=op_id; IF FOUND THEN IF existing_digest<>req_digest THEN RAISE EXCEPTION 'divergent retention replay' USING ERRCODE='40001'; END IF; RETURN true; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(p_parent_schema::text||'.'||p_parent_table::text||'.'||p_partition_name::text,1));
 SELECT * INTO r FROM system.partition_registry WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table AND partition_name=p_partition_name FOR UPDATE;
 IF NOT FOUND OR r.lifecycle_state<>'attached' THEN RAISE EXCEPTION 'partition registry entry is not attached' USING ERRCODE='55000'; END IF;
 SELECT * INTO p FROM system.retention_policy WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table;
 IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL OR r.partition_schema<>r.parent_schema
    OR r.range_end>clock_timestamp()-p.retain_for OR r.range_start>clock_timestamp()-interval '1 day'
    OR (SELECT count(*) FROM system.partition_registry newer WHERE newer.parent_schema=r.parent_schema AND newer.parent_table=r.parent_table AND newer.lifecycle_state='attached' AND newer.range_start>=r.range_start)<=p.minimum_partitions_to_keep
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease l WHERE l.parent_schema=p_parent_schema AND l.parent_table=p_parent_table AND l.partition_name=p_partition_name AND l.released_at IS NULL AND l.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_backfill_state b WHERE b.parent_schema=p_parent_schema AND b.parent_table=p_parent_table AND b.partition_name=p_partition_name AND b.state<>'complete')
    OR system.has_pending_analytics_dependency(r.range_start,r.range_end)
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_execution e WHERE e.execution_id=p_execution_id)
 THEN RAISE EXCEPTION 'retention prerequisites are not satisfied' USING ERRCODE='55000'; END IF;
 EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %I.%I',p_parent_schema,p_parent_table,p_parent_schema,p_partition_name);
 UPDATE system.partition_registry SET lifecycle_state='detached',detached_at=clock_timestamp() WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table AND partition_name=p_partition_name;
 INSERT INTO system.retention_execution(execution_id,data_class,parent_schema,parent_table,partition_name,range_start,range_end,state,policy_revision,registry_revision,detached_at,drop_after) VALUES(p_execution_id,p.data_class,p_parent_schema,p_parent_table,p_partition_name,r.range_start,r.range_end,'grace',p.policy_revision,sha256(convert_to(p_parent_schema::text||'|'||p_parent_table::text||'|'||p_partition_name::text||'|'||r.range_start::text||'|'||r.range_end::text,'UTF8')),clock_timestamp(),clock_timestamp()+interval '24 hours');
  INSERT INTO control.m10_mutation_replay(operation_id,action_name,instance_id,request_digest,result_digest,result) VALUES(op_id,'retention_detach',nullif(current_setting('sqlobserver.target_scope',true),'')::uuid,req_digest,sha256(req_digest),jsonb_build_object('executionId',p_execution_id,'state','grace'));
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(clock_timestamp(),op_id,'user',current_setting('sqlobserver.actor_sid',true),'retention.partition.detach','allowed','succeeded','partition',p_partition_name::text,nullif(current_setting('sqlobserver.retention_correlation_id',true),'')::uuid,req_digest,jsonb_build_object('dataClass',p.data_class,'policyRevision',p.policy_revision,'dropAfter',clock_timestamp()+interval '24 hours','changeReason',current_setting('sqlobserver.retention_change_reason',true)));
 RETURN true;
END $detach$;
CREATE OR REPLACE FUNCTION system.drop_m10_partition(p_execution_id uuid) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC' AS $drop$
DECLARE e system.retention_execution%ROWTYPE; op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid; req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex'); existing_digest bytea;
BEGIN IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator' OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL OR op_id IS NULL OR coalesce(octet_length(req_digest),0)<>32 THEN RAISE EXCEPTION 'retention drop requires global SecurityAdministrator authorization' USING ERRCODE='42501'; END IF; SELECT request_digest INTO existing_digest FROM control.m10_mutation_replay WHERE operation_id=op_id; IF FOUND THEN IF existing_digest<>req_digest THEN RAISE EXCEPTION 'divergent retention replay' USING ERRCODE='40001'; END IF; RETURN true; END IF; SELECT * INTO e FROM system.retention_execution WHERE execution_id=p_execution_id FOR UPDATE; IF NOT FOUND OR e.state<>'grace' OR e.drop_after>clock_timestamp() THEN RETURN false; END IF; PERFORM pg_advisory_xact_lock(hashtextextended(e.parent_schema::text||'.'||e.parent_table::text||'.'||e.partition_name::text,1));
 IF NOT EXISTS(SELECT 1 FROM system.retention_policy p WHERE p.data_class=e.data_class AND p.policy_revision=e.policy_revision AND p.enabled AND p.retain_for IS NOT NULL)
    OR NOT EXISTS(SELECT 1 FROM system.partition_registry r WHERE r.parent_schema=e.parent_schema AND r.parent_table=e.parent_table AND r.partition_name=e.partition_name AND r.lifecycle_state='detached' AND sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text||'|'||r.partition_name::text||'|'||r.range_start::text||'|'||r.range_end::text,'UTF8'))=e.registry_revision)
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table AND partition_name=e.partition_name AND released_at IS NULL AND expires_at>clock_timestamp())
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>clock_timestamp())
    OR system.has_pending_analytics_dependency(e.range_start,e.range_end) THEN RETURN false; END IF;
 EXECUTE format('DROP TABLE IF EXISTS %I.%I', e.parent_schema,e.partition_name); UPDATE system.partition_registry SET lifecycle_state='dropped',dropped_at=clock_timestamp() WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table AND partition_name=e.partition_name; UPDATE system.retention_execution SET state='dropped',dropped_at=clock_timestamp() WHERE execution_id=p_execution_id; INSERT INTO control.m10_mutation_replay(operation_id,action_name,instance_id,request_digest,result_digest,result) VALUES(op_id,'retention_drop',nullif(current_setting('sqlobserver.target_scope',true),'')::uuid,req_digest,sha256(req_digest),jsonb_build_object('executionId',p_execution_id,'state','dropped')); INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(clock_timestamp(),op_id,'user',current_setting('sqlobserver.actor_sid',true),'retention.partition.drop','allowed','succeeded','partition',e.partition_name::text,nullif(current_setting('sqlobserver.retention_correlation_id',true),'')::uuid,req_digest,jsonb_build_object('dataClass',e.data_class,'policyRevision',e.policy_revision,'changeReason',current_setting('sqlobserver.retention_change_reason',true))); RETURN true; EXCEPTION WHEN SQLSTATE '40001' THEN RAISE; WHEN OTHERS THEN INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at) VALUES(p_execution_id,SQLSTATE,SQLERRM,clock_timestamp()+interval '1 hour'); UPDATE system.retention_execution SET state='retry',attempt=attempt+1,last_error=SQLERRM WHERE execution_id=p_execution_id; RETURN false; END $drop$;
REVOKE ALL ON FUNCTION system.detach_m10_partition(name,name,name,uuid),system.drop_m10_partition(uuid) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.detach_m10_partition(name,name,name,uuid),system.drop_m10_partition(uuid) TO sqlobserver_server;
