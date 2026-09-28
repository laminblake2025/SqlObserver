-- Keep diagnostic-event months readable during grace. Their typed deadlock
-- summaries reference the event envelope and prevent detach-first retention.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.retention_execution DROP CONSTRAINT ck_retention_execution_state;
ALTER TABLE system.retention_execution ADD CONSTRAINT ck_retention_execution_state CHECK
 (state IN ('previewed','blocked','detached','grace','dropped','failed','retry',
            'reattached','attached_grace','attached_failed','cancelled'));
ALTER TABLE system.retention_execution ADD CONSTRAINT ck_retention_execution_attached_grace CHECK
 (state NOT IN ('attached_grace','attached_failed')
  OR (detached_at IS NULL AND drop_after IS NOT NULL));

-- A SECURITY DEFINER retention routine runs as the migrator but retains the
-- collector session_user. Direct collector DELETE remains ungranted, and this
-- guard still requires the scoped retention context for cascaded child rows.
CREATE OR REPLACE FUNCTION events.reject_deadlock_mutation() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,events AS $guard$
BEGIN
 IF TG_OP='DELETE'
    AND current_setting('sqlobserver.retention_context',true)='envelope_cascade'
    AND current_user='sqlobserver_migrator' THEN
  RETURN OLD;
 END IF;
 RAISE EXCEPTION 'deadlock evidence is append-only' USING ERRCODE='55000';
END $guard$;

CREATE FUNCTION system.run_attached_event_retention_step(
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,events,audit SET TimeZone='UTC'
AS $attached$
DECLARE
 v_now timestamptz:=clock_timestamp();
 e system.retention_execution%ROWTYPE;
 r system.partition_registry%ROWTYPE;
 p system.retention_policy%ROWTYPE;
 v_execution_id uuid;
 v_activity_id uuid;
 v_digest bytea;
 v_sqlstate text;
 v_error text;
 v_count integer;
 v_prior_context text;
 v_outcome text;
BEGIN
 PERFORM control.assert_worker_lease(
  'retention/maintenance',p_owner_execution_id,p_fencing_token);

 -- A changed or disabled policy cancels an attached grace without hiding or
 -- changing any evidence. A later eligible revision may begin a new grace.
 SELECT execution.* INTO e FROM system.retention_execution execution
 WHERE execution.parent_schema='events' AND execution.parent_table='diagnostic_event'
   AND execution.state IN ('attached_grace','attached_failed')
   AND NOT EXISTS(SELECT 1 FROM system.retention_policy policy
     WHERE policy.data_class=execution.data_class
       AND policy.parent_schema=execution.parent_schema
       AND policy.parent_table=execution.parent_table
       AND policy.policy_revision=execution.policy_revision
       AND policy.enabled AND policy.retain_for IS NOT NULL)
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
   sha256(convert_to('retention.cancel|'||e.execution_id::text,'UTF8')),
   jsonb_build_object('dataClass',e.data_class,
     'oldPolicyRevision',e.policy_revision,'fencingToken',p_fencing_token));
  RETURN 'cancelled';
 END IF;

 -- Retire one due attached month. A failed operation rolls back both typed
 -- deadlock removal and partition DDL, then records a bounded retry.
 SELECT execution.* INTO e FROM system.retention_execution execution
 WHERE execution.parent_schema='events' AND execution.parent_table='diagnostic_event'
   AND execution.state='attached_grace' AND execution.attempt<20
   AND execution.drop_after<=v_now
   AND coalesce((SELECT retry.next_attempt_at FROM system.retention_drop_retry retry
     WHERE retry.execution_id=execution.execution_id
     ORDER BY retry.retry_id DESC LIMIT 1),'-infinity'::timestamptz)<=v_now
   AND EXISTS(SELECT 1 FROM system.retention_policy policy
     WHERE policy.data_class=execution.data_class
       AND policy.parent_schema=execution.parent_schema
       AND policy.parent_table=execution.parent_table
       AND policy.policy_revision=execution.policy_revision
       AND policy.enabled AND policy.retain_for IS NOT NULL
       AND execution.range_end<=v_now-policy.retain_for)
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
  SELECT registry.* INTO r FROM system.partition_registry registry
  WHERE registry.parent_schema=e.parent_schema
    AND registry.parent_table=e.parent_table
    AND registry.partition_name=e.partition_name
    AND registry.lifecycle_state='attached'
  FOR UPDATE;
  IF NOT FOUND THEN RETURN 'idle'; END IF;
  IF r.partition_schema<>e.parent_schema OR r.range_start<>e.range_start
     OR r.range_end<>e.range_end
     OR sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text
       ||'|'||r.partition_name::text||'|'||r.range_start::text||'|'
       ||r.range_end::text,'UTF8'))<>e.registry_revision THEN
   RAISE EXCEPTION 'attached-grace registry identity changed' USING ERRCODE='55000';
  END IF;
  IF NOT pg_try_advisory_xact_lock(hashtextextended(
    e.parent_schema::text||'.'||e.parent_table::text||'.'||e.partition_name::text,1))
  THEN RETURN 'idle'; END IF;
  SELECT policy.* INTO p FROM system.retention_policy policy
  WHERE policy.data_class=e.data_class AND policy.parent_schema=e.parent_schema
    AND policy.parent_table=e.parent_table FOR SHARE;
  IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL
     OR p.policy_revision<>e.policy_revision
     OR e.range_end>clock_timestamp()-p.retain_for
     OR (SELECT count(*) FROM system.partition_registry newer
         WHERE newer.parent_schema=e.parent_schema
           AND newer.parent_table=e.parent_table
           AND newer.lifecycle_state='attached'
           AND newer.range_start>=e.range_start)<=p.minimum_partitions_to_keep
     OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
         WHERE reader.parent_schema=e.parent_schema
           AND reader.parent_table=e.parent_table
           AND reader.partition_name=e.partition_name
           AND reader.released_at IS NULL
           AND reader.expires_at>clock_timestamp())
     OR EXISTS(SELECT 1 FROM system.retention_backfill_state backfill
         WHERE backfill.parent_schema=e.parent_schema
           AND backfill.parent_table=e.parent_table
           AND backfill.partition_name=e.partition_name
           AND backfill.state<>'complete')
     OR system.has_pending_analytics_dependency(e.range_start,e.range_end)
     OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation recovery
         WHERE recovery.valid AND recovery.expires_at>clock_timestamp())
  THEN RETURN 'idle'; END IF;
  v_activity_id:=gen_random_uuid();
  v_digest:=sha256(convert_to('retention.attached_drop|'
    ||e.execution_id::text,'UTF8'));
  BEGIN
   -- The child lock prevents a new event envelope from entering this month
   -- between the bounded typed-row count and detach.
   EXECUTE format('LOCK TABLE %I.%I IN ACCESS EXCLUSIVE MODE',
     r.partition_schema,r.partition_name);
   SELECT count(*) INTO v_count FROM
    (SELECT 1 FROM events.deadlock_summary summary
      WHERE summary.occurred_at>=e.range_start
        AND summary.occurred_at<e.range_end LIMIT 10001) bounded;
   IF v_count>10000 THEN
    RAISE EXCEPTION 'deadlock month exceeds 10000 typed rows for atomic retirement'
     USING ERRCODE='54000';
   END IF;
   v_prior_context:=current_setting('sqlobserver.retention_context',true);
   PERFORM set_config('sqlobserver.retention_context','envelope_cascade',true);
   DELETE FROM events.deadlock_summary summary
    WHERE summary.occurred_at>=e.range_start
      AND summary.occurred_at<e.range_end;
   PERFORM set_config('sqlobserver.retention_context',coalesce(v_prior_context,''),true);
   EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %I.%I',
     r.parent_schema,r.parent_table,r.partition_schema,r.partition_name);
   UPDATE system.partition_registry SET lifecycle_state='detached',
     detached_at=clock_timestamp()
    WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table
      AND partition_name=e.partition_name;
   EXECUTE format('DROP TABLE %I.%I',r.partition_schema,r.partition_name);
   UPDATE system.partition_registry SET lifecycle_state='dropped',
     dropped_at=clock_timestamp()
    WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table
      AND partition_name=e.partition_name;
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
      'policyRevision',e.policy_revision,'typedDeadlockRows',v_count,
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
    SET state=CASE WHEN e.attempt+1>=20 THEN 'attached_failed' ELSE 'attached_grace' END,
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

 -- Start grace for one eligible month. It stays attached and visible.
 SELECT registry.* INTO r FROM system.partition_registry registry
 JOIN system.retention_policy policy
  ON policy.parent_schema=registry.parent_schema
   AND policy.parent_table=registry.parent_table
 WHERE registry.parent_schema='events' AND registry.parent_table='diagnostic_event'
   AND registry.lifecycle_state='attached'
   AND registry.partition_schema=registry.parent_schema
   AND policy.enabled AND policy.retain_for IS NOT NULL
   AND registry.range_end<=v_now-policy.retain_for
   AND registry.range_start<=v_now-interval '1 day'
   AND (SELECT count(*) FROM system.partition_registry newer
     WHERE newer.parent_schema=registry.parent_schema
       AND newer.parent_table=registry.parent_table
       AND newer.lifecycle_state='attached'
       AND newer.range_start>=registry.range_start)
      >policy.minimum_partitions_to_keep
   AND NOT EXISTS(SELECT 1 FROM system.retention_execution active
     WHERE active.parent_schema=registry.parent_schema
       AND active.parent_table=registry.parent_table
       AND active.partition_name=registry.partition_name
       AND active.state IN ('attached_grace','attached_failed'))
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
 ORDER BY registry.range_end,registry.partition_name
 LIMIT 1 FOR UPDATE OF registry SKIP LOCKED;
 IF NOT FOUND THEN RETURN 'idle'; END IF;
 IF NOT pg_try_advisory_xact_lock(hashtextextended(
  r.parent_schema::text||'.'||r.parent_table::text||'.'||r.partition_name::text,1))
 THEN RETURN 'idle'; END IF;
 SELECT policy.* INTO p FROM system.retention_policy policy
 WHERE policy.parent_schema=r.parent_schema AND policy.parent_table=r.parent_table
 FOR SHARE;
 IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL
    OR r.range_end>clock_timestamp()-p.retain_for
    OR (SELECT count(*) FROM system.partition_registry newer
      WHERE newer.parent_schema=r.parent_schema
        AND newer.parent_table=r.parent_table
        AND newer.lifecycle_state='attached'
        AND newer.range_start>=r.range_start)<=p.minimum_partitions_to_keep
    OR EXISTS(SELECT 1 FROM system.retention_execution active
      WHERE active.parent_schema=r.parent_schema AND active.parent_table=r.parent_table
        AND active.partition_name=r.partition_name
        AND active.state IN ('attached_grace','attached_failed'))
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease reader
      WHERE reader.parent_schema=r.parent_schema
        AND reader.parent_table=r.parent_table
        AND reader.partition_name=r.partition_name
        AND reader.released_at IS NULL AND reader.expires_at>clock_timestamp())
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
 v_digest:=sha256(convert_to('retention.attached_grace|'
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
    'policyRevision',p.policy_revision,'fencingToken',p_fencing_token));
 RETURN 'grace_started';
END $attached$;
REVOKE ALL ON FUNCTION system.run_attached_event_retention_step(uuid,bigint)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- Preserve the existing path for every other data class.
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
-- The collector enters through this sole granted step. Reattach wins over
-- attached-event retirement; both precede the original detach-first path.
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
 RETURN system.run_m10_retention_step(
  p_owner_execution_id,p_fencing_token);
END $safe_step$;