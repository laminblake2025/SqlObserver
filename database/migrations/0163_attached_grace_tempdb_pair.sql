-- Retire a TempDB snapshot and its same-day file partition as one unit.
-- Policies remain disabled until the paired worker and admin paths are proven.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION system.run_attached_tempdb_retention_step(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC'
AS $tempdb$
DECLARE
 v_now timestamptz:=clock_timestamp();
 e system.retention_execution%ROWTYPE;
 r system.partition_registry%ROWTYPE;
 f system.partition_registry%ROWTYPE;
 p system.retention_policy%ROWTYPE;
 cp system.retention_policy%ROWTYPE;
 v_execution_id uuid;
 v_activity_id uuid;
 v_digest bytea;
 v_sqlstate text;
 v_error text;
 v_outcome text;
BEGIN
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);

 -- A changed parent policy or an independently enabled child policy cancels
 -- grace while both partitions remain attached and readable.
 SELECT execution.* INTO e FROM system.retention_execution execution
 WHERE execution.data_class='m9_tempdb'
   AND execution.parent_schema='telemetry'
   AND execution.parent_table='tempdb_snapshot'
   AND execution.state IN ('attached_grace','attached_failed')
   AND (NOT EXISTS(SELECT 1 FROM system.retention_policy policy
      WHERE policy.data_class='m9_tempdb'
        AND policy.parent_schema=execution.parent_schema
        AND policy.parent_table=execution.parent_table
        AND policy.policy_revision=execution.policy_revision
        AND policy.enabled AND policy.retain_for IS NOT NULL)
     OR EXISTS(SELECT 1 FROM system.retention_policy child_policy
      WHERE child_policy.data_class='m9_tempdb_files'
        AND child_policy.enabled))
 ORDER BY execution.drop_after,execution.execution_id
 LIMIT 1 FOR UPDATE OF execution SKIP LOCKED;
 IF FOUND THEN
  UPDATE system.retention_execution SET state='cancelled',last_error=NULL
  WHERE execution_id=e.execution_id;
  PERFORM control.assert_worker_lease(
   'retention/maintenance',p_owner_execution_id,p_fencing_token);
  INSERT INTO audit.activity
   (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
    authorization_result,outcome,subject_kind,subject_identifier,
    correlation_id,parameter_digest,safe_details)
  VALUES(clock_timestamp(),gen_random_uuid(),'system','collector/retention',
   'retention.partition.attached_grace_cancel','not_applicable','succeeded',
   'partition',e.partition_name::text,e.execution_id,
   sha256(convert_to('retention.tempdb.cancel|'||e.execution_id::text,'UTF8')),
   jsonb_build_object('dataClass',e.data_class,
     'oldPolicyRevision',e.policy_revision,'fencingToken',p_fencing_token));
  RETURN 'cancelled';
 END IF;

 -- Skip blocked executions so a reader on one day does not stall the fleet.
 SELECT execution.* INTO e FROM system.retention_execution execution
 JOIN system.retention_policy policy
   ON policy.data_class=execution.data_class
  AND policy.parent_schema=execution.parent_schema
  AND policy.parent_table=execution.parent_table
  AND policy.policy_revision=execution.policy_revision
  AND policy.enabled AND policy.retain_for IS NOT NULL
 JOIN system.retention_policy child_policy
   ON child_policy.data_class='m9_tempdb_files'
  AND child_policy.parent_schema='telemetry'
  AND child_policy.parent_table='tempdb_file_snapshot'
  AND NOT child_policy.enabled
 WHERE execution.data_class='m9_tempdb'
   AND execution.parent_schema='telemetry'
   AND execution.parent_table='tempdb_snapshot'
   AND execution.state='attached_grace' AND execution.attempt<20
   AND execution.drop_after<=v_now
   AND execution.range_end<=v_now-policy.retain_for
   AND coalesce((SELECT retry.next_attempt_at
     FROM system.retention_drop_retry retry
     WHERE retry.execution_id=execution.execution_id
     ORDER BY retry.retry_id DESC LIMIT 1),'-infinity'::timestamptz)<=v_now
   AND NOT EXISTS(SELECT 1 FROM system.partition_reader_lease reader
     JOIN system.partition_registry registry
       ON registry.parent_schema=reader.parent_schema
      AND registry.parent_table=reader.parent_table
      AND registry.partition_name=reader.partition_name
     WHERE registry.parent_schema='telemetry'
       AND registry.parent_table IN ('tempdb_snapshot','tempdb_file_snapshot')
       AND registry.range_start=execution.range_start
       AND registry.range_end=execution.range_end
       AND reader.released_at IS NULL AND reader.expires_at>v_now)
   AND NOT EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
     JOIN system.partition_registry registry
       ON registry.parent_schema=backfill.parent_schema
      AND registry.parent_table=backfill.parent_table
      AND registry.partition_name=backfill.partition_name
     WHERE registry.parent_schema='telemetry'
       AND registry.parent_table IN ('tempdb_snapshot','tempdb_file_snapshot')
       AND registry.range_start=execution.range_start
       AND registry.range_end=execution.range_end
       AND backfill.state<>'complete')
   AND NOT system.has_pending_analytics_dependency(
     execution.range_start,execution.range_end)
   AND EXISTS(SELECT 1 FROM system.recovery_attestation recovery
     WHERE recovery.valid AND recovery.expires_at>v_now)
 ORDER BY execution.drop_after,execution.execution_id
 LIMIT 1 FOR UPDATE OF execution SKIP LOCKED;
 IF FOUND THEN
  IF NOT pg_try_advisory_xact_lock(hashtextextended(
    e.parent_schema::text||'.'||e.parent_table::text||'.'
    ||e.partition_name::text,1)) THEN RETURN 'idle'; END IF;
  SELECT registry.* INTO r FROM system.partition_registry registry
   WHERE registry.parent_schema=e.parent_schema
     AND registry.parent_table=e.parent_table
     AND registry.partition_name=e.partition_name FOR UPDATE;
  SELECT registry.* INTO f FROM system.partition_registry registry
   WHERE registry.parent_schema='telemetry'
     AND registry.parent_table='tempdb_file_snapshot'
     AND registry.range_start=e.range_start
     AND registry.range_end=e.range_end FOR UPDATE;
  IF f.partition_name IS NOT NULL AND NOT pg_try_advisory_xact_lock(
    hashtextextended('telemetry.tempdb_file_snapshot.'
      ||f.partition_name::text,1)) THEN RETURN 'idle'; END IF;
  SELECT policy.* INTO p FROM system.retention_policy policy
   WHERE policy.data_class='m9_tempdb' FOR SHARE;
  SELECT policy.* INTO cp FROM system.retention_policy policy
   WHERE policy.data_class='m9_tempdb_files' FOR SHARE;
  IF p.data_class IS NULL OR cp.data_class IS NULL
     OR NOT p.enabled OR p.retain_for IS NULL
     OR p.policy_revision<>e.policy_revision OR cp.enabled
     OR e.range_end>clock_timestamp()-p.retain_for
     OR (SELECT count(*) FROM system.partition_registry newer
       WHERE newer.parent_schema='telemetry'
         AND newer.parent_table='tempdb_snapshot'
         AND newer.lifecycle_state='attached'
         AND newer.range_start>=e.range_start)<=p.minimum_partitions_to_keep
     OR (SELECT count(*) FROM system.partition_registry newer
       WHERE newer.parent_schema='telemetry'
         AND newer.parent_table='tempdb_file_snapshot'
         AND newer.lifecycle_state='attached'
         AND newer.range_start>=e.range_start)<=cp.minimum_partitions_to_keep
     OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
       WHERE reader.parent_schema='telemetry'
         AND ((reader.parent_table='tempdb_snapshot'
                AND reader.partition_name=e.partition_name)
           OR (reader.parent_table='tempdb_file_snapshot'
                AND reader.partition_name=f.partition_name))
         AND reader.released_at IS NULL
         AND reader.expires_at>clock_timestamp())
     OR EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
       WHERE backfill.parent_schema='telemetry'
         AND ((backfill.parent_table='tempdb_snapshot'
                AND backfill.partition_name=e.partition_name)
           OR (backfill.parent_table='tempdb_file_snapshot'
                AND backfill.partition_name=f.partition_name))
         AND backfill.state<>'complete')
     OR system.has_pending_analytics_dependency(e.range_start,e.range_end)
     OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery
       WHERE recovery.valid AND recovery.expires_at>clock_timestamp())
  THEN RETURN 'idle'; END IF;
  v_activity_id:=gen_random_uuid();
  v_digest:=sha256(convert_to('retention.tempdb.drop|'
    ||e.execution_id::text,'UTF8'));
  BEGIN
   IF r.partition_name IS NULL OR f.partition_name IS NULL
      OR r.partition_schema<>'telemetry' OR f.partition_schema<>'telemetry'
      OR r.lifecycle_state<>'attached' OR f.lifecycle_state<>'attached'
      OR r.range_start<>e.range_start OR r.range_end<>e.range_end
      OR f.range_start<>e.range_start OR f.range_end<>e.range_end
      OR sha256(convert_to(r.parent_schema::text||'|'
        ||r.parent_table::text||'|'||r.partition_name::text||'|'
        ||r.range_start::text||'|'||r.range_end::text,'UTF8'))
         <>e.registry_revision THEN
    RAISE EXCEPTION 'TempDB pair registry identity changed' USING ERRCODE='55000';
   END IF;
   EXECUTE format('ALTER TABLE telemetry.tempdb_file_snapshot DETACH PARTITION %I.%I',
     f.partition_schema,f.partition_name);
   EXECUTE format('DROP TABLE %I.%I',f.partition_schema,f.partition_name);
   EXECUTE format('ALTER TABLE telemetry.tempdb_snapshot DETACH PARTITION %I.%I',
     r.partition_schema,r.partition_name);
   EXECUTE format('DROP TABLE %I.%I',r.partition_schema,r.partition_name);
   UPDATE system.partition_registry SET lifecycle_state='dropped',
     detached_at=clock_timestamp(),dropped_at=clock_timestamp()
    WHERE parent_schema='telemetry'
      AND ((parent_table='tempdb_snapshot' AND partition_name=r.partition_name)
        OR (parent_table='tempdb_file_snapshot' AND partition_name=f.partition_name));
   UPDATE system.retention_execution SET state='dropped',
     detached_at=clock_timestamp(),dropped_at=clock_timestamp(),last_error=NULL
    WHERE execution_id=e.execution_id;
   PERFORM control.assert_worker_lease(
    'retention/maintenance',p_owner_execution_id,p_fencing_token);
   INSERT INTO audit.activity
    (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
     authorization_result,outcome,subject_kind,subject_identifier,
     correlation_id,parameter_digest,safe_details)
   VALUES(clock_timestamp(),v_activity_id,'system','collector/retention',
    'retention.partition.drop','not_applicable','succeeded','partition',
    e.partition_name::text,e.execution_id,v_digest,
    jsonb_build_object('dataClass',e.data_class,
      'pairedPartition',f.partition_name,'policyRevision',e.policy_revision,
      'fencingToken',p_fencing_token));
   RETURN 'dropped';
  EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
  WHEN OTHERS THEN
   v_sqlstate:=SQLSTATE;
   v_error:=SQLERRM;
   v_outcome:=CASE WHEN e.attempt+1>=20 THEN 'failed' ELSE 'retry' END;
   INSERT INTO system.retention_drop_retry
    (execution_id,error_code,error_detail,next_attempt_at)
   VALUES(e.execution_id,v_sqlstate,v_error,clock_timestamp()+interval '1 hour');
   UPDATE system.retention_execution
    SET state=CASE WHEN e.attempt+1>=20 THEN 'attached_failed'
                   ELSE 'attached_grace' END,
        attempt=attempt+1,last_error=v_error
    WHERE execution_id=e.execution_id;
   PERFORM control.assert_worker_lease(
    'retention/maintenance',p_owner_execution_id,p_fencing_token);
   INSERT INTO audit.activity
    (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
     authorization_result,outcome,subject_kind,subject_identifier,
     correlation_id,parameter_digest,safe_details)
   VALUES(clock_timestamp(),v_activity_id,'system','collector/retention',
    'retention.partition.drop','not_applicable','failed','partition',
    e.partition_name::text,e.execution_id,v_digest,
    jsonb_build_object('dataClass',e.data_class,
      'policyRevision',e.policy_revision,'sqlState',v_sqlstate));
   RETURN v_outcome;
  END;
 END IF;

 -- Pick one old parent day only when its exact child day is also attached.
 SELECT registry.* INTO r FROM system.partition_registry registry
 JOIN system.partition_registry child
   ON child.parent_schema='telemetry'
  AND child.parent_table='tempdb_file_snapshot'
  AND child.range_start=registry.range_start
  AND child.range_end=registry.range_end
  AND child.lifecycle_state='attached'
  AND child.partition_schema='telemetry'
 JOIN system.retention_policy policy
   ON policy.data_class='m9_tempdb'
  AND policy.parent_schema=registry.parent_schema
  AND policy.parent_table=registry.parent_table
  AND policy.enabled AND policy.retain_for IS NOT NULL
 JOIN system.retention_policy child_policy
   ON child_policy.data_class='m9_tempdb_files'
  AND child_policy.parent_schema=child.parent_schema
  AND child_policy.parent_table=child.parent_table
  AND NOT child_policy.enabled
 WHERE registry.parent_schema='telemetry'
   AND registry.parent_table='tempdb_snapshot'
   AND registry.lifecycle_state='attached'
   AND registry.partition_schema='telemetry'
   AND registry.range_end<=v_now-policy.retain_for
   AND registry.range_start<=v_now-interval '1 day'
   AND (SELECT count(*) FROM system.partition_registry newer
     WHERE newer.parent_schema='telemetry'
       AND newer.parent_table='tempdb_snapshot'
       AND newer.lifecycle_state='attached'
       AND newer.range_start>=registry.range_start)
      >policy.minimum_partitions_to_keep
   AND (SELECT count(*) FROM system.partition_registry newer
     WHERE newer.parent_schema='telemetry'
       AND newer.parent_table='tempdb_file_snapshot'
       AND newer.lifecycle_state='attached'
       AND newer.range_start>=registry.range_start)
      >child_policy.minimum_partitions_to_keep
   AND NOT EXISTS(SELECT 1 FROM system.retention_execution active
     WHERE active.parent_schema=registry.parent_schema
       AND active.parent_table=registry.parent_table
       AND active.partition_name=registry.partition_name
       AND active.state IN ('attached_grace','attached_failed'))
   AND NOT EXISTS(SELECT 1 FROM system.partition_reader_lease reader
     WHERE reader.parent_schema='telemetry'
       AND ((reader.parent_table='tempdb_snapshot'
              AND reader.partition_name=registry.partition_name)
         OR (reader.parent_table='tempdb_file_snapshot'
              AND reader.partition_name=child.partition_name))
       AND reader.released_at IS NULL AND reader.expires_at>v_now)
   AND NOT EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
     WHERE backfill.parent_schema='telemetry'
       AND ((backfill.parent_table='tempdb_snapshot'
              AND backfill.partition_name=registry.partition_name)
         OR (backfill.parent_table='tempdb_file_snapshot'
              AND backfill.partition_name=child.partition_name))
       AND backfill.state<>'complete')
   AND NOT system.has_pending_analytics_dependency(
     registry.range_start,registry.range_end)
   AND EXISTS(SELECT 1 FROM system.recovery_attestation recovery
     WHERE recovery.valid AND recovery.expires_at>v_now)
 ORDER BY registry.range_end,registry.partition_name
 LIMIT 1 FOR UPDATE OF registry SKIP LOCKED;
 IF NOT FOUND THEN RETURN 'idle'; END IF;
 IF NOT pg_try_advisory_xact_lock(hashtextextended(
  'telemetry.tempdb_snapshot.'||r.partition_name::text,1))
 THEN RETURN 'idle'; END IF;
 SELECT child.* INTO f FROM system.partition_registry child
 WHERE child.parent_schema='telemetry'
   AND child.parent_table='tempdb_file_snapshot'
   AND child.range_start=r.range_start AND child.range_end=r.range_end
 FOR UPDATE;
 IF f.partition_name IS NULL OR NOT pg_try_advisory_xact_lock(
  hashtextextended('telemetry.tempdb_file_snapshot.'
    ||f.partition_name::text,1)) THEN RETURN 'idle'; END IF;
 SELECT policy.* INTO p FROM system.retention_policy policy
  WHERE policy.data_class='m9_tempdb' FOR SHARE;
 SELECT policy.* INTO cp FROM system.retention_policy policy
  WHERE policy.data_class='m9_tempdb_files' FOR SHARE;
 IF p.data_class IS NULL OR cp.data_class IS NULL
    OR NOT p.enabled OR p.retain_for IS NULL OR cp.enabled
    OR r.lifecycle_state<>'attached' OR f.lifecycle_state<>'attached'
    OR r.partition_schema<>'telemetry' OR f.partition_schema<>'telemetry'
    OR r.range_start<>f.range_start OR r.range_end<>f.range_end
    OR r.range_end>clock_timestamp()-p.retain_for
    OR (SELECT count(*) FROM system.partition_registry newer
      WHERE newer.parent_schema='telemetry'
        AND newer.parent_table='tempdb_snapshot'
        AND newer.lifecycle_state='attached'
        AND newer.range_start>=r.range_start)<=p.minimum_partitions_to_keep
    OR (SELECT count(*) FROM system.partition_registry newer
      WHERE newer.parent_schema='telemetry'
        AND newer.parent_table='tempdb_file_snapshot'
        AND newer.lifecycle_state='attached'
        AND newer.range_start>=r.range_start)<=cp.minimum_partitions_to_keep
    OR EXISTS(SELECT 1 FROM system.retention_execution active
      WHERE active.parent_schema=r.parent_schema
        AND active.parent_table=r.parent_table
        AND active.partition_name=r.partition_name
        AND active.state IN ('attached_grace','attached_failed'))
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
      WHERE reader.parent_schema='telemetry'
        AND ((reader.parent_table='tempdb_snapshot'
               AND reader.partition_name=r.partition_name)
          OR (reader.parent_table='tempdb_file_snapshot'
               AND reader.partition_name=f.partition_name))
        AND reader.released_at IS NULL AND reader.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
      WHERE backfill.parent_schema='telemetry'
        AND ((backfill.parent_table='tempdb_snapshot'
               AND backfill.partition_name=r.partition_name)
          OR (backfill.parent_table='tempdb_file_snapshot'
               AND backfill.partition_name=f.partition_name))
        AND backfill.state<>'complete')
    OR system.has_pending_analytics_dependency(r.range_start,r.range_end)
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery
      WHERE recovery.valid AND recovery.expires_at>clock_timestamp())
 THEN RETURN 'idle'; END IF;
 v_execution_id:=gen_random_uuid();
 v_activity_id:=gen_random_uuid();
 v_digest:=sha256(convert_to('retention.tempdb.grace|'
   ||r.partition_name::text,'UTF8'));
 INSERT INTO system.retention_execution
  (execution_id,data_class,parent_schema,parent_table,partition_name,
   range_start,range_end,state,policy_revision,registry_revision,drop_after)
 VALUES(v_execution_id,p.data_class,r.parent_schema,r.parent_table,r.partition_name,
  r.range_start,r.range_end,'attached_grace',p.policy_revision,
  sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text||'|'
    ||r.partition_name::text||'|'||r.range_start::text||'|'
    ||r.range_end::text,'UTF8')),
  clock_timestamp()+interval '24 hours');
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);
 INSERT INTO audit.activity
  (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
   authorization_result,outcome,subject_kind,subject_identifier,
   correlation_id,parameter_digest,safe_details)
 VALUES(clock_timestamp(),v_activity_id,'system','collector/retention',
  'retention.partition.attached_grace_start','not_applicable','succeeded',
  'partition',r.partition_name::text,v_execution_id,v_digest,
  jsonb_build_object('dataClass',p.data_class,
    'pairedPartition',f.partition_name,
    'policyRevision',p.policy_revision,'fencingToken',p_fencing_token));
 RETURN 'grace_started';
END $tempdb$;
REVOKE ALL ON FUNCTION system.run_attached_tempdb_retention_step(uuid,bigint)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- Keep the generic detach-first worker away from the TempDB pair.
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
            AND registry.parent_table IN ('tempdb_snapshot','tempdb_file_snapshot'))
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
 IF p_parent_schema='telemetry'::name
    AND p_parent_table IN ('tempdb_snapshot','tempdb_file_snapshot') THEN
  RAISE EXCEPTION 'TempDB pair retention requires the paired lifecycle'
   USING ERRCODE='55000';
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

-- The collector has only this fenced entry point. The pair step precedes
-- generic detach, which now excludes both TempDB parents.
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
 RETURN system.run_m10_retention_step(
  p_owner_execution_id,p_fencing_token);
END $safe_step$;
