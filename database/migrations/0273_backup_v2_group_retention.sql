-- A backup receipt, inventory and samples have the same retention lifetime.
-- Keep the graph attached/readable throughout grace, then detach/drop children
-- before parents in one transaction. Never CASCADE away repository constraints.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION system.backup_v2_retention_group_reason(p_start timestamptz,p_end timestamptz,p_now timestamptz)
RETURNS text LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog SET TimeZone='UTC'
AS $reason$
DECLARE policy system.retention_policy%ROWTYPE; part system.partition_registry%ROWTYPE; parent_name text; relation oid;
BEGIN
 SELECT * INTO policy FROM system.retention_policy WHERE data_class='m9_backup_evidence';
 IF NOT FOUND OR NOT policy.enabled THEN RETURN 'retention_disabled'; END IF;
 IF policy.retain_for IS NULL THEN RETURN 'duration_unconfigured'; END IF;
 IF p_end>p_now-policy.retain_for OR p_start>p_now-interval '1 day' THEN RETURN 'within_retention_window'; END IF;
 FOREACH parent_name IN ARRAY ARRAY['backup_v2_scan','backup_v2_database','backup_v2_sample'] LOOP
  SELECT * INTO part FROM system.partition_registry registry
   WHERE registry.parent_schema='telemetry' AND registry.parent_table=parent_name
     AND registry.range_start=p_start AND registry.range_end=p_end AND registry.lifecycle_state='attached';
  IF NOT FOUND OR part.partition_schema<>'telemetry' THEN RETURN 'dependency_pending'; END IF;
  relation:=to_regclass(format('telemetry.%I',part.partition_name));
  IF relation IS NULL OR NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=relation AND inhparent=format('telemetry.%I',parent_name)::regclass)
    OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid=relation AND relkind='r' AND pg_get_userbyid(relowner)='sqlobserver_migrator'
      AND pg_get_expr(relpartbound,oid)=format('FOR VALUES FROM (%L) TO (%L)',p_start,p_end))
  THEN RETURN 'dependency_pending'; END IF;
  IF (SELECT count(*) FROM system.partition_registry registry WHERE registry.parent_schema='telemetry'
       AND registry.parent_table=parent_name AND registry.lifecycle_state='attached' AND registry.range_start>=p_start)
      <=policy.minimum_partitions_to_keep THEN RETURN 'minimum_partition_floor'; END IF;
  IF EXISTS(SELECT 1 FROM system.partition_reader_lease reader WHERE reader.parent_schema='telemetry'
       AND reader.parent_table=parent_name AND reader.partition_name=part.partition_name
       AND reader.released_at IS NULL AND reader.expires_at>p_now) THEN RETURN 'reader_lease_active'; END IF;
  IF EXISTS(SELECT 1 FROM system.retention_backfill_state backfill WHERE backfill.parent_schema='telemetry'
       AND backfill.parent_table=parent_name AND backfill.partition_name=part.partition_name AND backfill.state<>'complete')
  THEN RETURN 'backfill_incomplete'; END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM pg_constraint fk WHERE fk.contype='f' AND fk.conparentid=0
   AND fk.confrelid IN (SELECT protected.relation FROM (
     SELECT unnest(ARRAY['telemetry.backup_v2_scan'::regclass,'telemetry.backup_v2_database'::regclass,'telemetry.backup_v2_sample'::regclass])::oid relation
     UNION ALL SELECT to_regclass(format('%I.%I',registry.partition_schema,registry.partition_name))::oid
       FROM system.partition_registry registry WHERE registry.parent_schema='telemetry'
        AND registry.parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample')
        AND registry.range_start=p_start AND registry.range_end=p_end) protected)
   AND NOT (fk.conrelid='telemetry.backup_v2_database'::regclass AND fk.confrelid='telemetry.backup_v2_scan'::regclass
     AND fk.conkey=ARRAY[1,2,3,4]::smallint[] AND fk.confkey=ARRAY[1,2,4,3]::smallint[] AND fk.confdeltype='a'
     OR fk.conrelid='telemetry.backup_v2_sample'::regclass AND fk.confrelid='telemetry.backup_v2_database'::regclass
     AND fk.conkey=ARRAY[1,2,3,4,5]::smallint[] AND fk.confkey=ARRAY[1,2,3,4,5]::smallint[] AND fk.confdeltype='a'))
 THEN RETURN 'dependency_pending'; END IF;
 IF system.has_pending_analytics_dependency(p_start,p_end) THEN RETURN 'dependency_pending'; END IF;
 IF NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery WHERE recovery.valid AND recovery.expires_at>p_now)
 THEN RETURN 'recovery_attestation_missing'; END IF;
 RETURN 'eligible';
END;
$reason$;
REVOKE ALL ON FUNCTION system.backup_v2_retention_group_reason(timestamptz,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION system.run_attached_backup_v2_retention_step(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $step$
DECLARE policy system.retention_policy%ROWTYPE; execution system.retention_execution%ROWTYPE;
 part system.partition_registry%ROWTYPE; candidate system.partition_registry%ROWTYPE;
 activity_id uuid:=gen_random_uuid(); now_utc timestamptz:=clock_timestamp(); group_digest bytea;
 parent_name text; reason text; result text; error_code text;
BEGIN
 PERFORM control.assert_worker_lease('retention/maintenance',p_owner_execution_id,p_fencing_token);
 -- Serialize group selection with policy changes; partition readers take the
 -- same advisory locks as the existing retention framework.
 SELECT * INTO policy FROM system.retention_policy WHERE data_class='m9_backup_evidence' FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Backup retention policy is missing' USING ERRCODE='55000'; END IF;
 SELECT * INTO execution FROM system.retention_execution item
 WHERE item.data_class='m9_backup_evidence' AND item.state IN ('attached_grace','attached_failed')
   AND (item.policy_revision<>policy.policy_revision OR NOT policy.enabled OR policy.retain_for IS NULL)
 ORDER BY item.drop_after,item.execution_id LIMIT 1 FOR UPDATE SKIP LOCKED;
 IF FOUND THEN
  UPDATE system.retention_execution SET state='cancelled',last_error=NULL WHERE execution_id=execution.execution_id;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
    subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details)
  VALUES(now_utc,activity_id,'system','collector/retention','retention.partition.cancel','not_applicable','succeeded',
    'partition',execution.partition_name,execution.execution_id,sha256(convert_to(execution.execution_id::text,'UTF8')),
    jsonb_build_object('dataClass','m9_backup_evidence','policyRevision',policy.policy_revision));
  PERFORM control.assert_worker_lease('retention/maintenance',p_owner_execution_id,p_fencing_token);
  RETURN 'cancelled';
 END IF;
 IF NOT policy.enabled OR policy.retain_for IS NULL THEN RETURN 'idle'; END IF;
 SELECT * INTO execution FROM system.retention_execution item
 WHERE item.data_class='m9_backup_evidence' AND item.state='attached_grace' AND item.attempt<20
   AND item.policy_revision=policy.policy_revision AND item.drop_after<=now_utc
   AND (item.attempt=0 OR (SELECT retry.next_attempt_at FROM system.retention_drop_retry retry
       WHERE retry.execution_id=item.execution_id ORDER BY retry.retry_id DESC LIMIT 1)<=now_utc)
   AND system.backup_v2_retention_group_reason(item.range_start,item.range_end,now_utc)='eligible'
 ORDER BY item.drop_after,item.execution_id LIMIT 1 FOR UPDATE SKIP LOCKED;
 IF FOUND THEN
  SELECT * INTO candidate FROM system.partition_registry registry WHERE registry.parent_schema='telemetry'
    AND registry.parent_table='backup_v2_scan' AND registry.partition_name=execution.partition_name
    AND registry.lifecycle_state='attached' AND registry.range_start=execution.range_start AND registry.range_end=execution.range_end;
  IF NOT FOUND THEN RAISE EXCEPTION 'Backup retention scan identity changed' USING ERRCODE='55000'; END IF;
 ELSE
  SELECT * INTO candidate FROM system.partition_registry registry WHERE registry.parent_schema='telemetry'
    AND registry.parent_table='backup_v2_scan' AND registry.lifecycle_state='attached'
    AND system.backup_v2_retention_group_reason(registry.range_start,registry.range_end,now_utc)='eligible'
    AND NOT EXISTS(SELECT 1 FROM system.retention_execution item WHERE item.parent_schema='telemetry'
      AND item.parent_table='backup_v2_scan' AND item.partition_name=registry.partition_name
      AND item.state IN ('attached_grace','attached_failed'))
   ORDER BY registry.range_end,registry.partition_name LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN RETURN 'idle'; END IF;
 END IF;
 group_digest:=sha256(convert_to('backup-v2|'||candidate.range_start::text||'|'||candidate.range_end::text,'UTF8'));
 -- Stable scan/database/sample lock order matches partition creation. Recheck
 -- every hold after obtaining all locks, immediately before irreversible DDL.
 FOREACH parent_name IN ARRAY ARRAY['backup_v2_scan','backup_v2_database','backup_v2_sample'] LOOP
  SELECT * INTO part FROM system.partition_registry registry WHERE registry.parent_schema='telemetry'
    AND registry.parent_table=parent_name AND registry.range_start=candidate.range_start
    AND registry.range_end=candidate.range_end AND registry.lifecycle_state='attached' FOR UPDATE;
  IF NOT FOUND THEN RETURN 'idle'; END IF;
  IF NOT pg_try_advisory_xact_lock(hashtextextended('telemetry.'||parent_name||'.'||part.partition_name::text,1)) THEN RETURN 'idle'; END IF;
  group_digest:=sha256(group_digest||convert_to(part.partition_name::text,'UTF8'));
 END LOOP;
 reason:=system.backup_v2_retention_group_reason(candidate.range_start,candidate.range_end,clock_timestamp());
 IF reason<>'eligible' THEN RETURN 'idle'; END IF;
 IF execution.execution_id IS NULL THEN
  INSERT INTO system.retention_execution(execution_id,data_class,parent_schema,parent_table,partition_name,range_start,range_end,
    state,policy_revision,registry_revision,drop_after)
  VALUES(gen_random_uuid(),'m9_backup_evidence','telemetry','backup_v2_scan',candidate.partition_name,candidate.range_start,
    candidate.range_end,'attached_grace',policy.policy_revision,group_digest,clock_timestamp()+interval '24 hours')
  RETURNING * INTO execution;
  result:='grace_started';
 ELSE
  IF execution.registry_revision<>group_digest THEN RAISE EXCEPTION 'Backup retention group identity changed' USING ERRCODE='55000'; END IF;
  BEGIN
   -- Foreign keys include receipt time, so no other day's rows can depend on
   -- this graph. Each child is dropped before its referenced parent is detached.
   FOREACH parent_name IN ARRAY ARRAY['backup_v2_sample','backup_v2_database','backup_v2_scan'] LOOP
    SELECT * INTO part FROM system.partition_registry registry WHERE registry.parent_schema='telemetry'
      AND registry.parent_table=parent_name AND registry.range_start=candidate.range_start
      AND registry.range_end=candidate.range_end AND registry.lifecycle_state='attached';
    EXECUTE format('ALTER TABLE telemetry.%I DETACH PARTITION telemetry.%I',parent_name,part.partition_name);
    EXECUTE format('DROP TABLE telemetry.%I',part.partition_name);
   END LOOP;
   UPDATE system.partition_registry SET lifecycle_state='dropped',detached_at=clock_timestamp(),dropped_at=clock_timestamp()
    WHERE parent_schema='telemetry' AND parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample')
      AND range_start=candidate.range_start AND range_end=candidate.range_end AND lifecycle_state='attached';
   UPDATE system.retention_execution SET state='dropped',detached_at=clock_timestamp(),dropped_at=clock_timestamp(),last_error=NULL
    WHERE execution_id=execution.execution_id;
   result:='dropped';
  EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
  WHEN OTHERS THEN
   error_code:=SQLSTATE;
   INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at)
    VALUES(execution.execution_id,error_code,SQLERRM,clock_timestamp()+interval '1 hour');
   UPDATE system.retention_execution SET state=CASE WHEN attempt+1>=20 THEN 'attached_failed' ELSE 'attached_grace' END,
     attempt=attempt+1,last_error=SQLERRM WHERE execution_id=execution.execution_id;
   result:=CASE WHEN execution.attempt+1>=20 THEN 'failed' ELSE 'retry' END;
  END;
 END IF;
 PERFORM control.assert_worker_lease('retention/maintenance',p_owner_execution_id,p_fencing_token);
 INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
    subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details)
 VALUES(clock_timestamp(),activity_id,'system','collector/retention',
   CASE WHEN result='grace_started' THEN 'retention.partition.grace' ELSE 'retention.partition.drop' END,
   'not_applicable',CASE WHEN result IN ('retry','failed') THEN 'failed' ELSE 'succeeded' END,
   'partition',candidate.partition_name,execution.execution_id,group_digest,
   jsonb_build_object('dataClass','m9_backup_evidence','policyRevision',policy.policy_revision,'fencingToken',p_fencing_token,
     'groupSize',3,'sqlState',error_code));
 RETURN result;
END;
$step$;
REVOKE ALL ON FUNCTION system.run_attached_backup_v2_retention_step(uuid,bigint)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION system.run_m10_retention_step(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC'
AS $retention$
DECLARE
 v_now timestamptz;
 e system.retention_execution%ROWTYPE;
 r system.partition_registry%ROWTYPE;
 p system.retention_policy%ROWTYPE;
 v_execution_id uuid;
 v_activity_id uuid;
 v_digest bytea;
 v_error_code text;
BEGIN
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 v_now:=clock_timestamp();

 -- Retry grace drops first. The candidate query skips executions held by a
 -- changed policy, an active reader, a pending analytics job, or lost recovery
 -- evidence, so one blocked execution cannot starve other eligible work.
 SELECT execution.* INTO e FROM system.retention_execution execution
 WHERE execution.state IN ('grace','retry') AND execution.attempt<20
   AND execution.drop_after<=v_now
   AND (execution.state='grace' OR (
     SELECT retry.next_attempt_at FROM system.retention_drop_retry retry
     WHERE retry.execution_id=execution.execution_id
     ORDER BY retry.retry_id DESC LIMIT 1)<=v_now)
   AND EXISTS(SELECT 1 FROM system.retention_policy policy
     WHERE policy.data_class=execution.data_class
       AND policy.policy_revision=execution.policy_revision
       AND policy.enabled AND policy.retain_for IS NOT NULL)
   AND EXISTS(SELECT 1 FROM system.partition_registry registry
     WHERE registry.parent_schema=execution.parent_schema
       AND registry.parent_table=execution.parent_table
       AND registry.partition_name=execution.partition_name
       AND registry.lifecycle_state='detached'
       AND sha256(convert_to(registry.parent_schema::text||'|'
         ||registry.parent_table::text||'|'||registry.partition_name::text||'|'
         ||registry.range_start::text||'|'||registry.range_end::text,
         'UTF8'))=execution.registry_revision)
   AND NOT EXISTS(SELECT 1 FROM system.partition_reader_lease reader
     WHERE reader.parent_schema=execution.parent_schema
       AND reader.parent_table=execution.parent_table
       AND reader.partition_name=execution.partition_name
       AND reader.released_at IS NULL AND reader.expires_at>v_now)
   AND EXISTS(SELECT 1 FROM system.recovery_attestation recovery
     WHERE recovery.valid AND recovery.expires_at>v_now)
   AND NOT system.has_pending_analytics_dependency(
     execution.range_start,execution.range_end)
 ORDER BY execution.drop_after,execution.execution_id
 LIMIT 1 FOR UPDATE OF execution SKIP LOCKED;
 IF FOUND THEN
  IF NOT pg_try_advisory_xact_lock(hashtextextended(
    e.parent_schema::text||'.'||e.parent_table::text||'.'
    ||e.partition_name::text,1)) THEN RETURN 'idle'; END IF;
  -- A competing administrator can change policy or attach a reader between
  -- candidate selection and the partition lock. Re-evaluate before DDL.
  IF NOT EXISTS(SELECT 1 FROM system.retention_policy policy
      WHERE policy.data_class=e.data_class
        AND policy.policy_revision=e.policy_revision AND policy.enabled
        AND policy.retain_for IS NOT NULL)
     OR NOT EXISTS(SELECT 1 FROM system.partition_registry registry
      WHERE registry.parent_schema=e.parent_schema
        AND registry.parent_table=e.parent_table
        AND registry.partition_name=e.partition_name
        AND registry.lifecycle_state='detached'
        AND sha256(convert_to(registry.parent_schema::text||'|'
          ||registry.parent_table::text||'|'||registry.partition_name::text||'|'
          ||registry.range_start::text||'|'||registry.range_end::text,
          'UTF8'))=e.registry_revision)
     OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
      WHERE reader.parent_schema=e.parent_schema
        AND reader.parent_table=e.parent_table
        AND reader.partition_name=e.partition_name
        AND reader.released_at IS NULL
        AND reader.expires_at>clock_timestamp())
     OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery
      WHERE recovery.valid AND recovery.expires_at>clock_timestamp())
     OR system.has_pending_analytics_dependency(e.range_start,e.range_end)
  THEN RETURN 'idle'; END IF;
  v_activity_id:=gen_random_uuid();
  v_digest:=sha256(convert_to('retention.drop|'||e.execution_id::text,
    'UTF8'));
  BEGIN
   EXECUTE format('DROP TABLE %I.%I',e.parent_schema,e.partition_name);
   UPDATE system.partition_registry SET lifecycle_state='dropped',
     dropped_at=clock_timestamp()
   WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table
     AND partition_name=e.partition_name;
   UPDATE system.retention_execution SET state='dropped',
     dropped_at=clock_timestamp(),last_error=NULL
   WHERE execution_id=e.execution_id;
   PERFORM control.assert_worker_lease(
    'retention/maintenance',p_owner_execution_id,p_fencing_token);
   INSERT INTO audit.activity
    (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
     authorization_result,outcome,subject_kind,subject_identifier,
     correlation_id,parameter_digest,safe_details)
   VALUES(clock_timestamp(),v_activity_id,'system',
     'collector/retention','retention.partition.drop','not_applicable',
     'succeeded','partition',e.partition_name::text,e.execution_id,v_digest,
     jsonb_build_object('dataClass',e.data_class,
       'policyRevision',e.policy_revision,'fencingToken',p_fencing_token));
   RETURN 'dropped';
  EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
  WHEN OTHERS THEN
   v_error_code:=SQLSTATE;
   INSERT INTO system.retention_drop_retry
    (execution_id,error_code,error_detail,next_attempt_at)
   VALUES(e.execution_id,v_error_code,SQLERRM,
     clock_timestamp()+interval '1 hour');
   UPDATE system.retention_execution
   SET state=CASE WHEN attempt+1>=20 THEN 'failed' ELSE 'retry' END,
       attempt=attempt+1,last_error=SQLERRM
   WHERE execution_id=e.execution_id;
   INSERT INTO audit.activity
    (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
     authorization_result,outcome,subject_kind,subject_identifier,
     correlation_id,parameter_digest,safe_details)
   VALUES(clock_timestamp(),v_activity_id,'system',
     'collector/retention','retention.partition.drop','not_applicable',
     'failed','partition',e.partition_name::text,e.execution_id,v_digest,
     jsonb_build_object('dataClass',e.data_class,
       'policyRevision',e.policy_revision,'sqlState',v_error_code));
   RETURN 'retry';
  END;
 END IF;

 -- Detach one oldest eligible attached partition. All policy, dependency,
 -- reader and recovery predicates are checked again after taking its lock.
 SELECT registry.* INTO r FROM system.partition_registry registry
 JOIN system.retention_policy policy
  ON policy.parent_schema=registry.parent_schema
   AND policy.parent_table=registry.parent_table
 WHERE policy.enabled AND policy.retain_for IS NOT NULL
   AND NOT (registry.parent_schema='events'::name
            AND registry.parent_table='diagnostic_event'::name)
   AND NOT (registry.parent_schema='telemetry'::name
            AND registry.parent_table IN ('tempdb_snapshot','tempdb_file_snapshot','backup_v2_scan','backup_v2_database','backup_v2_sample'))
   AND registry.lifecycle_state='attached'
   AND registry.partition_schema=registry.parent_schema
   AND registry.range_end<=v_now-policy.retain_for
   AND registry.range_start<=v_now-interval '1 day'
   AND (SELECT count(*) FROM system.partition_registry newer
      WHERE newer.parent_schema=registry.parent_schema
        AND newer.parent_table=registry.parent_table
        AND newer.lifecycle_state='attached'
        AND newer.range_start>=registry.range_start)
       >policy.minimum_partitions_to_keep
   AND NOT EXISTS(SELECT 1 FROM system.partition_reader_lease reader
      WHERE reader.parent_schema=registry.parent_schema
        AND reader.parent_table=registry.parent_table
        AND reader.partition_name=registry.partition_name
        AND reader.released_at IS NULL AND reader.expires_at>v_now)
   AND NOT EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
      WHERE backfill.parent_schema=registry.parent_schema
        AND backfill.parent_table=registry.parent_table
        AND backfill.partition_name=registry.partition_name
        AND backfill.state<>'complete')
   AND NOT system.has_pending_analytics_dependency(
      registry.range_start,registry.range_end)
   AND EXISTS(SELECT 1 FROM system.recovery_attestation recovery
      WHERE recovery.valid AND recovery.expires_at>v_now)
 ORDER BY registry.range_end,registry.parent_schema,
   registry.parent_table,registry.partition_name
 LIMIT 1 FOR UPDATE OF registry SKIP LOCKED;
 IF NOT FOUND THEN RETURN 'idle'; END IF;
 IF NOT pg_try_advisory_xact_lock(hashtextextended(
    r.parent_schema::text||'.'||r.parent_table::text||'.'
    ||r.partition_name::text,1)) THEN RETURN 'idle'; END IF;
 SELECT * INTO p FROM system.retention_policy policy
 WHERE policy.parent_schema=r.parent_schema
   AND policy.parent_table=r.parent_table;
 IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL
    OR r.lifecycle_state<>'attached'
    OR r.partition_schema<>r.parent_schema
    OR r.range_end>clock_timestamp()-p.retain_for
    OR r.range_start>clock_timestamp()-interval '1 day'
    OR (SELECT count(*) FROM system.partition_registry newer
      WHERE newer.parent_schema=r.parent_schema
        AND newer.parent_table=r.parent_table
        AND newer.lifecycle_state='attached'
        AND newer.range_start>=r.range_start)
       <=p.minimum_partitions_to_keep
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
 THEN RETURN 'idle'; END IF;
 v_execution_id:=gen_random_uuid();
 v_activity_id:=gen_random_uuid();
 v_digest:=sha256(convert_to('retention.detach|'
   ||r.parent_schema::text||'.'||r.parent_table::text||'.'
   ||r.partition_name::text,'UTF8'));
 BEGIN
  EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %I.%I',
    r.parent_schema,r.parent_table,r.parent_schema,r.partition_name);
  UPDATE system.partition_registry SET lifecycle_state='detached',
    detached_at=clock_timestamp()
  WHERE parent_schema=r.parent_schema AND parent_table=r.parent_table
    AND partition_name=r.partition_name;
  INSERT INTO system.retention_execution
   (execution_id,data_class,parent_schema,parent_table,partition_name,
    range_start,range_end,state,policy_revision,registry_revision,
    detached_at,drop_after)
  VALUES(v_execution_id,p.data_class,r.parent_schema,r.parent_table,
    r.partition_name,r.range_start,r.range_end,'grace',p.policy_revision,
    sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text||'|'
      ||r.partition_name::text||'|'||r.range_start::text||'|'
      ||r.range_end::text,'UTF8')),
    clock_timestamp(),clock_timestamp()+interval '24 hours');
  PERFORM control.assert_worker_lease(
   'retention/maintenance',p_owner_execution_id,p_fencing_token);
  INSERT INTO audit.activity
   (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
    authorization_result,outcome,subject_kind,subject_identifier,
    correlation_id,parameter_digest,safe_details)
  VALUES(clock_timestamp(),v_activity_id,'system',
    'collector/retention','retention.partition.detach','not_applicable',
    'succeeded','partition',r.partition_name::text,v_execution_id,v_digest,
    jsonb_build_object('dataClass',p.data_class,
      'policyRevision',p.policy_revision,'fencingToken',p_fencing_token,
      'dropAfter',clock_timestamp()+interval '24 hours'));
  RETURN 'detached';
 EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
 WHEN OTHERS THEN
  INSERT INTO audit.activity
   (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
    authorization_result,outcome,subject_kind,subject_identifier,
    correlation_id,parameter_digest,safe_details)
  VALUES(clock_timestamp(),v_activity_id,'system',
    'collector/retention','retention.partition.detach','not_applicable',
    'failed','partition',r.partition_name::text,v_execution_id,v_digest,
    jsonb_build_object('dataClass',p.data_class,'sqlState',SQLSTATE));
  RETURN 'failed';
 END;
END $retention$;

-- Prevent an administrator from detaching either side independently.

CREATE OR REPLACE FUNCTION system.detach_m10_partition(
 p_parent_schema name,p_parent_table name,p_partition_name name,p_execution_id uuid)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC'
AS $detach$
DECLARE
 r system.partition_registry%ROWTYPE;
 f system.partition_registry%ROWTYPE;
 p system.retention_policy%ROWTYPE;
 cp system.retention_policy%ROWTYPE;
 op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid;
 req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex');
 existing_digest bytea;
 v_attached_grace boolean;
 v_tempdb_pair boolean;
 v_drop_after timestamptz;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global'
    OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL THEN
  RAISE EXCEPTION 'retention detach requires global SecurityAdministrator authorization'
   USING ERRCODE='42501';
 END IF;
 IF p_parent_schema='telemetry' AND p_parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample') THEN
  RAISE EXCEPTION 'Backup evidence retention requires the scheduled three-table grace route' USING ERRCODE='22023';
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
 IF p_parent_schema='telemetry'::name
    AND p_parent_table='tempdb_file_snapshot'::name THEN
  RAISE EXCEPTION 'TempDB pair retention requires the paired lifecycle'
   USING ERRCODE='55000';
 END IF;
 v_tempdb_pair:=p_parent_schema='telemetry'::name
   AND p_parent_table='tempdb_snapshot'::name;
 IF NOT v_tempdb_pair THEN
  PERFORM pg_advisory_xact_lock(hashtextextended(
   p_parent_schema::text||'.'||p_parent_table::text||'.'||p_partition_name::text,1));
 END IF;
 SELECT registry.* INTO r FROM system.partition_registry registry
 WHERE registry.parent_schema=p_parent_schema
   AND registry.parent_table=p_parent_table
   AND registry.partition_name=p_partition_name FOR UPDATE;
 IF NOT FOUND OR r.lifecycle_state<>'attached' THEN
  RAISE EXCEPTION 'partition registry entry is not attached' USING ERRCODE='55000';
 END IF;
 IF v_tempdb_pair THEN
  IF NOT pg_try_advisory_xact_lock(hashtextextended(
   'telemetry.tempdb_snapshot.'||r.partition_name::text,1)) THEN
   RAISE EXCEPTION 'TempDB pair retention is busy' USING ERRCODE='55000';
  END IF;
  SELECT child.* INTO f FROM system.partition_registry child
  WHERE child.parent_schema='telemetry'
    AND child.parent_table='tempdb_file_snapshot'
    AND child.range_start=r.range_start AND child.range_end=r.range_end
  FOR UPDATE;
  IF NOT FOUND OR f.lifecycle_state<>'attached'
     OR f.partition_schema<>'telemetry' THEN
   RAISE EXCEPTION 'matching TempDB file partition is not attached'
    USING ERRCODE='55000';
  END IF;
  IF NOT pg_try_advisory_xact_lock(hashtextextended(
   'telemetry.tempdb_file_snapshot.'||f.partition_name::text,1)) THEN
   RAISE EXCEPTION 'TempDB pair retention is busy' USING ERRCODE='55000';
  END IF;
  SELECT policy.* INTO cp FROM system.retention_policy policy
  WHERE policy.data_class='m9_tempdb_files'
    AND policy.parent_schema='telemetry'
    AND policy.parent_table='tempdb_file_snapshot' FOR SHARE;
 END IF;
 SELECT policy.* INTO p FROM system.retention_policy policy
 WHERE policy.parent_schema=p_parent_schema AND policy.parent_table=p_parent_table
 FOR SHARE;
 IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL
    OR (v_tempdb_pair AND (p.data_class<>'m9_tempdb'
      OR cp.data_class IS NULL OR cp.enabled
      OR (SELECT count(*) FROM system.partition_registry newer
       WHERE newer.parent_schema='telemetry'
         AND newer.parent_table='tempdb_file_snapshot'
         AND newer.lifecycle_state='attached'
         AND newer.range_start>=f.range_start)<=cp.minimum_partitions_to_keep
      OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
       WHERE reader.parent_schema='telemetry'
         AND reader.parent_table='tempdb_file_snapshot'
         AND reader.partition_name=f.partition_name
         AND reader.released_at IS NULL
         AND reader.expires_at>clock_timestamp())
      OR EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
       WHERE backfill.parent_schema='telemetry'
         AND backfill.parent_table='tempdb_file_snapshot'
         AND backfill.partition_name=f.partition_name
         AND backfill.state<>'complete')))
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
 v_attached_grace:=v_tempdb_pair OR (p_parent_schema='events'::name
   AND p_parent_table='diagnostic_event'::name
   AND p.data_class='diagnostic_events');
 IF v_attached_grace AND EXISTS(
    SELECT 1 FROM system.retention_execution execution
    WHERE execution.parent_schema=r.parent_schema
      AND execution.parent_table=r.parent_table
      AND execution.partition_name=r.partition_name
      AND execution.state IN ('attached_grace','attached_failed')) THEN
  RAISE EXCEPTION 'attached partition grace is already active'
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
   'pairedPartition',CASE WHEN v_tempdb_pair THEN f.partition_name ELSE NULL END,
   'policyRevision',p.policy_revision,'dropAfter',v_drop_after,
   'changeReason',current_setting('sqlobserver.retention_change_reason',true)));
 RETURN true;
END $detach$;



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
   CASE WHEN policy.data_class='m9_tempdb' THEN EXISTS(
    SELECT 1 FROM system.partition_registry child
    JOIN system.retention_policy child_policy
      ON child_policy.data_class='m9_tempdb_files'
     AND child_policy.parent_schema=child.parent_schema
     AND child_policy.parent_table=child.parent_table
    WHERE child.parent_schema='telemetry'
      AND child.parent_table='tempdb_file_snapshot'
      AND child.range_start=registry.range_start
      AND child.range_end=registry.range_end
      AND child.lifecycle_state='attached'
      AND child.partition_schema='telemetry'
      AND NOT child_policy.enabled
      AND (SELECT count(*) FROM system.partition_registry newer
       WHERE newer.parent_schema=child.parent_schema
         AND newer.parent_table=child.parent_table
         AND newer.lifecycle_state='attached'
         AND newer.range_start>=child.range_start)
        >child_policy.minimum_partitions_to_keep
      AND NOT EXISTS(SELECT 1 FROM system.partition_reader_lease reader
       WHERE reader.parent_schema=child.parent_schema
         AND reader.parent_table=child.parent_table
         AND reader.partition_name=child.partition_name
         AND reader.released_at IS NULL AND reader.expires_at>p_now_utc)
      AND NOT EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
       WHERE backfill.parent_schema=child.parent_schema
         AND backfill.parent_table=child.parent_table
         AND backfill.partition_name=child.partition_name
         AND backfill.state<>'complete'))
    ELSE true END AS pair_ready,
   CASE WHEN policy.data_class='m9_backup_evidence' THEN system.backup_v2_retention_group_reason(registry.range_start,registry.range_end,p_now_utc) ELSE 'eligible' END AS backup_group_reason,
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
    WHEN data_class='m9_backup_evidence' AND backup_group_reason<>'eligible' THEN backup_group_reason
    WHEN reader_active THEN 'reader_lease_active'
    WHEN backfill_active THEN 'backfill_incomplete'
    WHEN NOT pair_ready THEN 'dependency_pending'
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


CREATE OR REPLACE FUNCTION system.run_m10_retention_step_safe(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control SET TimeZone='UTC'
AS $safe_step$
DECLARE v_outcome text;
BEGIN
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 v_outcome:=system.reconcile_m10_retention_policy_change(
  p_owner_execution_id,p_fencing_token);
 IF v_outcome<>'idle' THEN RETURN v_outcome; END IF;
 v_outcome:=system.run_attached_event_retention_step(
  p_owner_execution_id,p_fencing_token);
 IF v_outcome<>'idle' THEN RETURN v_outcome; END IF;
 v_outcome:=system.run_attached_tempdb_retention_step(
  p_owner_execution_id,p_fencing_token);
 IF v_outcome<>'idle' THEN RETURN v_outcome; END IF;
 v_outcome:=system.run_attached_backup_v2_retention_step(p_owner_execution_id,p_fencing_token);
 IF v_outcome<>'idle' THEN RETURN v_outcome; END IF;
 RETURN system.run_m10_retention_step(
  p_owner_execution_id,p_fencing_token);
END $safe_step$;
