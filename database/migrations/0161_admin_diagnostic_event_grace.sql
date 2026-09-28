-- A SecurityAdministrator's existing detach operation starts attached grace
-- for diagnostic-event months. The fenced worker alone retires the month.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION system.detach_m10_partition(
 p_parent_schema name,p_parent_table name,p_partition_name name,p_execution_id uuid)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC'
AS $detach$
DECLARE
 r system.partition_registry%ROWTYPE;
 p system.retention_policy%ROWTYPE;
 op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid;
 req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex');
 existing_digest bytea;
 v_attached_grace boolean;
 v_drop_after timestamptz;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global'
    OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL THEN
  RAISE EXCEPTION 'retention detach requires global SecurityAdministrator authorization'
   USING ERRCODE='42501';
 END IF;
 IF p_execution_id IS NULL OR op_id IS NULL
    OR coalesce(octet_length(req_digest),0)<>32 THEN
  RAISE EXCEPTION 'retention execution fence is required' USING ERRCODE='22023';
 END IF;
 SELECT request_digest INTO existing_digest FROM control.m10_mutation_replay
 WHERE operation_id=op_id;
 IF FOUND THEN
  IF existing_digest<>req_digest THEN
   RAISE EXCEPTION 'divergent retention replay' USING ERRCODE='40001';
  END IF;
  RETURN true;
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(
  p_parent_schema::text||'.'||p_parent_table::text||'.'||p_partition_name::text,1));
 SELECT registry.* INTO r FROM system.partition_registry registry
 WHERE registry.parent_schema=p_parent_schema
   AND registry.parent_table=p_parent_table
   AND registry.partition_name=p_partition_name FOR UPDATE;
 IF NOT FOUND OR r.lifecycle_state<>'attached' THEN
  RAISE EXCEPTION 'partition registry entry is not attached' USING ERRCODE='55000';
 END IF;
 SELECT policy.* INTO p FROM system.retention_policy policy
 WHERE policy.parent_schema=p_parent_schema AND policy.parent_table=p_parent_table
 FOR SHARE;
 IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL
    OR r.partition_schema<>r.parent_schema
    OR r.range_end>clock_timestamp()-p.retain_for
    OR r.range_start>clock_timestamp()-interval '1 day'
    OR (SELECT count(*) FROM system.partition_registry newer
      WHERE newer.parent_schema=r.parent_schema
        AND newer.parent_table=r.parent_table
        AND newer.lifecycle_state='attached'
        AND newer.range_start>=r.range_start)<=p.minimum_partitions_to_keep
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
      WHERE reader.parent_schema=r.parent_schema
        AND reader.parent_table=r.parent_table
        AND reader.partition_name=r.partition_name
        AND reader.released_at IS NULL
        AND reader.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
      WHERE backfill.parent_schema=r.parent_schema
        AND backfill.parent_table=r.parent_table
        AND backfill.partition_name=r.partition_name
        AND backfill.state<>'complete')
    OR system.has_pending_analytics_dependency(r.range_start,r.range_end)
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery
      WHERE recovery.valid AND recovery.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_execution execution
      WHERE execution.execution_id=p_execution_id)
 THEN
  RAISE EXCEPTION 'retention prerequisites are not satisfied' USING ERRCODE='55000';
 END IF;
 v_attached_grace:=p_parent_schema='events'::name
   AND p_parent_table='diagnostic_event'::name
   AND p.data_class='diagnostic_events';
 IF v_attached_grace AND EXISTS(
    SELECT 1 FROM system.retention_execution execution
    WHERE execution.parent_schema=r.parent_schema
      AND execution.parent_table=r.parent_table
      AND execution.partition_name=r.partition_name
      AND execution.state IN ('attached_grace','attached_failed')) THEN
  RAISE EXCEPTION 'diagnostic-event grace is already active'
   USING ERRCODE='55000';
 END IF;
 v_drop_after:=clock_timestamp()+interval '24 hours';
 IF NOT v_attached_grace THEN
  EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %I.%I',
   p_parent_schema,p_parent_table,p_parent_schema,p_partition_name);
  UPDATE system.partition_registry
   SET lifecycle_state='detached',detached_at=clock_timestamp()
  WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table
    AND partition_name=p_partition_name;
 END IF;
 INSERT INTO system.retention_execution
  (execution_id,data_class,parent_schema,parent_table,partition_name,
   range_start,range_end,state,policy_revision,registry_revision,
   detached_at,drop_after)
 VALUES(p_execution_id,p.data_class,p_parent_schema,p_parent_table,p_partition_name,
  r.range_start,r.range_end,
  CASE WHEN v_attached_grace THEN 'attached_grace' ELSE 'grace' END,
  p.policy_revision,
  sha256(convert_to(p_parent_schema::text||'|'||p_parent_table::text||'|'
   ||p_partition_name::text||'|'||r.range_start::text||'|'
   ||r.range_end::text,'UTF8')),
  CASE WHEN v_attached_grace THEN NULL ELSE clock_timestamp() END,v_drop_after);
 INSERT INTO control.m10_mutation_replay
  (operation_id,action_name,instance_id,request_digest,result_digest,result)
 VALUES(op_id,'retention_detach',
  nullif(current_setting('sqlobserver.target_scope',true),'')::uuid,
  req_digest,sha256(req_digest),
  jsonb_build_object('executionId',p_execution_id,'state',
    CASE WHEN v_attached_grace THEN 'attached_grace' ELSE 'grace' END));
 INSERT INTO audit.activity
  (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
   authorization_result,outcome,subject_kind,subject_identifier,
   correlation_id,parameter_digest,safe_details)
 VALUES(clock_timestamp(),op_id,'user',current_setting('sqlobserver.actor_sid',true),
  CASE WHEN v_attached_grace
   THEN 'retention.partition.attached_grace_start'
   ELSE 'retention.partition.detach' END,
  'allowed','succeeded','partition',p_partition_name::text,
  nullif(current_setting('sqlobserver.retention_correlation_id',true),'')::uuid,
  req_digest,jsonb_build_object('dataClass',p.data_class,
   'policyRevision',p.policy_revision,'dropAfter',v_drop_after,
   'changeReason',current_setting('sqlobserver.retention_change_reason',true)));
 RETURN true;
END $detach$;

-- Preview reflects attached grace rather than offering a second detach.
CREATE OR REPLACE FUNCTION system.preview_m10_retention(
 p_now_utc timestamptz DEFAULT clock_timestamp(),p_cursor text DEFAULT NULL)
RETURNS TABLE(data_class text,parent_schema name,parent_table name,
 partition_name name,range_start timestamptz,range_end timestamptz,
 eligible boolean,reason text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,system,control SET TimeZone='UTC'
AS $preview$
 WITH cursor_value AS (
  SELECT CASE WHEN p_cursor IS NULL THEN NULL::jsonb ELSE
   convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')
    ||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb END AS value
 ), candidates AS (
  SELECT policy.data_class,registry.parent_schema,registry.parent_table,
   registry.partition_name,registry.range_start,registry.range_end,
   policy.enabled,policy.retain_for,policy.minimum_partitions_to_keep,
   registry.lifecycle_state,
   (SELECT execution.state FROM system.retention_execution execution
    WHERE execution.parent_schema=registry.parent_schema
      AND execution.parent_table=registry.parent_table
      AND execution.partition_name=registry.partition_name
      AND execution.state IN ('attached_grace','attached_failed')
    ORDER BY execution.drop_after DESC,execution.execution_id DESC LIMIT 1)
    AS attached_execution_state,
   (SELECT count(*) FROM system.partition_registry newer
    WHERE newer.parent_schema=registry.parent_schema
      AND newer.parent_table=registry.parent_table
      AND newer.lifecycle_state='attached'
      AND newer.range_start>=registry.range_start) AS newer_count,
   EXISTS(SELECT 1 FROM system.partition_reader_lease reader
    WHERE reader.parent_schema=registry.parent_schema
      AND reader.parent_table=registry.parent_table
      AND reader.partition_name=registry.partition_name
      AND reader.released_at IS NULL AND reader.expires_at>p_now_utc)
    AS reader_active,
   EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
    WHERE backfill.parent_schema=registry.parent_schema
      AND backfill.parent_table=registry.parent_table
      AND backfill.partition_name=registry.partition_name
      AND backfill.state<>'complete') AS backfill_active,
   system.has_pending_analytics_dependency(
    registry.range_start,registry.range_end) AS analytics_active,
   EXISTS(SELECT 1 FROM system.recovery_attestation recovery
    WHERE recovery.valid AND recovery.expires_at>p_now_utc)
    AS recovery_valid
  FROM system.retention_policy policy
  JOIN system.partition_registry registry
   ON registry.parent_schema=policy.parent_schema
    AND registry.parent_table=policy.parent_table
  CROSS JOIN cursor_value
  WHERE p_cursor IS NULL OR
    ((cursor_value.value->>'v')='m10.retention.v1'
      AND (cursor_value.value->>'o')='dataClass,parentSchema,parentTable,partitionName'
      AND (policy.data_class,registry.parent_schema::text,
           registry.parent_table::text,registry.partition_name::text)
        >(cursor_value.value->>'d',cursor_value.value->>'s1',
          cursor_value.value->>'t',cursor_value.value->>'p'))
 ), decisions AS (
  SELECT candidate.*,
   CASE
    WHEN NOT enabled THEN 'retention_disabled'
    WHEN retain_for IS NULL THEN 'duration_unconfigured'
    WHEN lifecycle_state<>'attached' THEN 'partition_not_attached'
    WHEN attached_execution_state='attached_grace' THEN 'grace_in_progress'
    WHEN attached_execution_state='attached_failed' THEN 'retention_failed'
    WHEN range_end>p_now_utc-retain_for THEN 'within_retention_window'
    WHEN newer_count<=minimum_partitions_to_keep THEN 'minimum_partition_floor'
    WHEN reader_active THEN 'reader_lease_active'
    WHEN backfill_active THEN 'backfill_incomplete'
    WHEN analytics_active THEN 'dependency_pending'
    WHEN NOT recovery_valid THEN 'recovery_attestation_missing'
    ELSE 'eligible'
   END AS decision_reason
  FROM candidates candidate
 )
 SELECT data_class,parent_schema,parent_table,partition_name,
  range_start,range_end,decision_reason='eligible',decision_reason
 FROM decisions
 ORDER BY data_class,parent_schema,parent_table,partition_name;
$preview$;
