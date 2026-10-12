-- Bound incremental deadlock replay storage before collector v2 activation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry' AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events' AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events' AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR (data_class='m7_query_performance' AND parent_schema='events' AND parent_table='query_performance_observation_v3' AND partition_granularity='day')
   OR (data_class='collection_run_original_copy' AND parent_schema='telemetry' AND parent_table='collection_run' AND partition_granularity='none')
   OR (data_class='m9_ag_replica_evidence' AND parent_schema='telemetry' AND parent_table='availability_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_ag_database_evidence' AND parent_schema='telemetry' AND parent_table='availability_database_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_backup_evidence' AND parent_schema='telemetry' AND parent_table='backup_v2_scan' AND partition_granularity='day')
   OR (data_class='sql_metric_baselines' AND parent_schema='analytics' AND parent_table='sql_metric_baseline' AND partition_granularity='day')
   OR (data_class='m6_deadlock_read_receipts' AND parent_schema='events' AND parent_table='deadlock_read_receipt' AND partition_granularity='month')
   OR data_class IN ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking','m6_deadlocks',
     'm8_alert_history','m8_alert_delivery','m9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases','m10_host_metrics','m10_replication','m10_rollups','m10_evidence')));
INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('m6_deadlock_read_receipts','events','deadlock_read_receipt','month',true,interval '30 days',2,'migration-0312');
CREATE INDEX ix_deadlock_checkpoint_completed ON control.deadlock_read_checkpoint(completed_at);

CREATE FUNCTION system.deadlock_read_receipt_retention_reason(p_start timestamptz,p_end timestamptz,p_now timestamptz)
RETURNS text LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog SET TimeZone='UTC'
AS $reason$
DECLARE v_policy system.retention_policy%ROWTYPE; v_part system.partition_registry%ROWTYPE; v_relation oid;
BEGIN
    SELECT * INTO v_policy FROM system.retention_policy WHERE data_class='m6_deadlock_read_receipts';
    IF NOT FOUND OR NOT v_policy.enabled THEN RETURN 'retention_disabled'; END IF;
    IF v_policy.retain_for IS NULL THEN RETURN 'duration_unconfigured'; END IF;
    SELECT * INTO v_part FROM system.partition_registry WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
        AND range_start=p_start AND range_end=p_end AND lifecycle_state='attached';
    IF NOT FOUND THEN RETURN 'partition_not_attached'; END IF;
    IF p_end>p_now-v_policy.retain_for THEN RETURN 'within_retention_window'; END IF;
    IF (SELECT count(*) FROM system.partition_registry WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
        AND lifecycle_state='attached' AND range_start>=p_start)<=v_policy.minimum_partitions_to_keep THEN RETURN 'minimum_partition_floor'; END IF;
    v_relation:=to_regclass(format('events.%I',v_part.partition_name));
    IF v_part.partition_schema<>'events' OR v_part.partition_granularity<>'month'
        OR v_relation IS NULL
        OR NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=v_relation AND inhparent='events.deadlock_read_receipt'::regclass)
        OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid=v_relation AND relkind='r' AND pg_get_userbyid(relowner)='sqlobserver_migrator'
            AND relrowsecurity AND relforcerowsecurity AND pg_get_expr(relpartbound,oid)=format('FOR VALUES FROM (%L) TO (%L)',p_start,p_end))
    THEN RETURN 'dependency_pending'; END IF;
    -- One offline target holds only the month containing its latest receipt.
    -- Other target/month partitions remain eligible; this is not a fleet gate.
    IF EXISTS(SELECT 1 FROM control.deadlock_read_checkpoint WHERE completed_at>=p_start AND completed_at<p_end)
    THEN RETURN 'checkpoint_active'; END IF;
    IF EXISTS(SELECT 1 FROM system.partition_reader_lease WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
        AND partition_name=v_part.partition_name AND released_at IS NULL AND expires_at>p_now) THEN RETURN 'reader_lease_active'; END IF;
    IF EXISTS(SELECT 1 FROM system.retention_backfill_state WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
        AND partition_name=v_part.partition_name AND state<>'complete') THEN RETURN 'backfill_incomplete'; END IF;
    IF EXISTS(SELECT 1 FROM pg_constraint WHERE contype='f' AND conparentid=0
        AND confrelid IN ('events.deadlock_read_receipt'::regclass,v_relation)) THEN RETURN 'dependency_pending'; END IF;
    IF NOT EXISTS(SELECT 1 FROM system.recovery_attestation WHERE valid AND expires_at>p_now) THEN RETURN 'recovery_attestation_missing'; END IF;
    RETURN 'eligible';
END $reason$;
REVOKE ALL ON FUNCTION system.deadlock_read_receipt_retention_reason(timestamptz,timestamptz,timestamptz)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION system.run_attached_deadlock_read_retention_step(p_owner uuid,p_fence bigint)
RETURNS text LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $step$
DECLARE v_policy system.retention_policy%ROWTYPE; v_execution system.retention_execution%ROWTYPE;
    v_part system.partition_registry%ROWTYPE; v_now timestamptz:=clock_timestamp(); v_digest bytea;
    v_result text; v_error text; v_activity uuid:=gen_random_uuid();
BEGIN
    PERFORM control.assert_worker_lease('retention/maintenance',p_owner,p_fence);
    SELECT * INTO STRICT v_policy FROM system.retention_policy WHERE data_class='m6_deadlock_read_receipts' FOR SHARE;
    SELECT * INTO v_execution FROM system.retention_execution WHERE data_class='m6_deadlock_read_receipts'
        AND state IN ('attached_grace','attached_failed') AND (policy_revision<>v_policy.policy_revision OR NOT v_policy.enabled OR v_policy.retain_for IS NULL)
        ORDER BY drop_after,execution_id LIMIT 1 FOR UPDATE SKIP LOCKED;
    IF FOUND THEN
        UPDATE system.retention_execution SET state='cancelled',last_error=NULL WHERE execution_id=v_execution.execution_id;
        INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
            subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details)
        VALUES(v_now,v_activity,'system','collector/retention','retention.partition.cancel','not_applicable','succeeded',
            'partition',v_execution.partition_name,v_execution.execution_id,sha256(convert_to(v_execution.execution_id::text,'UTF8')),
            jsonb_build_object('dataClass','m6_deadlock_read_receipts','policyRevision',v_policy.policy_revision));
        PERFORM control.assert_worker_lease('retention/maintenance',p_owner,p_fence);
        RETURN 'cancelled';
    END IF;
    IF NOT v_policy.enabled OR v_policy.retain_for IS NULL THEN RETURN 'idle'; END IF;
    SELECT * INTO v_execution FROM system.retention_execution e WHERE data_class='m6_deadlock_read_receipts'
        AND state='attached_grace' AND attempt<20 AND policy_revision=v_policy.policy_revision AND drop_after<=v_now
        AND (attempt=0 OR (SELECT next_attempt_at FROM system.retention_drop_retry WHERE execution_id=e.execution_id ORDER BY retry_id DESC LIMIT 1)<=v_now)
        AND system.deadlock_read_receipt_retention_reason(range_start,range_end,v_now)='eligible'
        ORDER BY drop_after,execution_id LIMIT 1 FOR UPDATE SKIP LOCKED;
    IF FOUND THEN
        SELECT * INTO v_part FROM system.partition_registry WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
            AND partition_name=v_execution.partition_name AND range_start=v_execution.range_start AND range_end=v_execution.range_end
            AND lifecycle_state='attached' FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock receipt retention identity changed' USING ERRCODE='55000'; END IF;
    ELSE
        SELECT * INTO v_part FROM system.partition_registry r WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
            AND lifecycle_state='attached' AND system.deadlock_read_receipt_retention_reason(range_start,range_end,v_now)='eligible'
            AND NOT EXISTS(SELECT 1 FROM system.retention_execution WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
                AND partition_name=r.partition_name AND state IN ('attached_grace','attached_failed'))
            ORDER BY range_end,partition_name LIMIT 1 FOR UPDATE SKIP LOCKED;
        IF NOT FOUND THEN RETURN 'idle'; END IF;
    END IF;
    IF NOT pg_try_advisory_xact_lock(hashtextextended('events.deadlock_read_receipt.'||v_part.partition_name::text,1)) THEN RETURN 'idle'; END IF;
    IF system.deadlock_read_receipt_retention_reason(v_part.range_start,v_part.range_end,clock_timestamp())<>'eligible' THEN RETURN 'idle'; END IF;
    v_digest:=sha256(convert_to('deadlock-read-receipt|'||v_part.partition_name::text||'|'||v_part.range_start::text||'|'||v_part.range_end::text,'UTF8'));
    IF v_execution.execution_id IS NULL THEN
        INSERT INTO system.retention_execution(execution_id,data_class,parent_schema,parent_table,partition_name,range_start,range_end,
            state,policy_revision,registry_revision,drop_after)
        VALUES(gen_random_uuid(),'m6_deadlock_read_receipts','events','deadlock_read_receipt',v_part.partition_name,v_part.range_start,v_part.range_end,
            'attached_grace',v_policy.policy_revision,v_digest,clock_timestamp()+interval '24 hours') RETURNING * INTO v_execution;
        v_result:='grace_started';
    ELSE
        IF v_execution.registry_revision<>v_digest THEN RAISE EXCEPTION 'Deadlock receipt retention registry changed' USING ERRCODE='55000'; END IF;
        -- SQL readers hold AccessShare until their transaction ends. Detach also
        -- locks the parent; take both locks without waiting or counting a busy
        -- replay transaction as a failed drop attempt.
        BEGIN
            EXECUTE 'LOCK TABLE ONLY events.deadlock_read_receipt IN ACCESS EXCLUSIVE MODE NOWAIT';
            EXECUTE format('LOCK TABLE events.%I IN ACCESS EXCLUSIVE MODE NOWAIT',v_part.partition_name);
        EXCEPTION WHEN lock_not_available THEN RETURN 'idle'; END;
        IF system.deadlock_read_receipt_retention_reason(v_part.range_start,v_part.range_end,clock_timestamp())<>'eligible' THEN RETURN 'idle'; END IF;
        BEGIN
            EXECUTE format('ALTER TABLE events.deadlock_read_receipt DETACH PARTITION events.%I',v_part.partition_name);
            EXECUTE format('DROP TABLE events.%I',v_part.partition_name);
            UPDATE system.partition_registry SET lifecycle_state='dropped',detached_at=clock_timestamp(),dropped_at=clock_timestamp()
                WHERE parent_schema='events' AND parent_table='deadlock_read_receipt' AND partition_name=v_part.partition_name;
            UPDATE system.retention_execution SET state='dropped',detached_at=clock_timestamp(),dropped_at=clock_timestamp(),last_error=NULL
                WHERE execution_id=v_execution.execution_id;
            v_result:='dropped';
        EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
        WHEN OTHERS THEN
            v_error:=SQLSTATE;
            INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at)
            VALUES(v_execution.execution_id,v_error,SQLERRM,clock_timestamp()+interval '1 hour');
            UPDATE system.retention_execution SET state=CASE WHEN attempt+1>=20 THEN 'attached_failed' ELSE 'attached_grace' END,
                attempt=attempt+1,last_error=SQLERRM WHERE execution_id=v_execution.execution_id;
            v_result:=CASE WHEN v_execution.attempt+1>=20 THEN 'failed' ELSE 'retry' END;
        END;
    END IF;
    INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
        subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details)
    VALUES(clock_timestamp(),v_activity,'system','collector/retention',
        CASE WHEN v_result='grace_started' THEN 'retention.partition.grace' ELSE 'retention.partition.drop' END,'not_applicable',
        CASE WHEN v_result IN ('retry','failed') THEN 'failed' ELSE 'succeeded' END,'partition',v_part.partition_name,v_execution.execution_id,v_digest,
        jsonb_build_object('dataClass','m6_deadlock_read_receipts','policyRevision',v_policy.policy_revision,'fencingToken',p_fence,'sqlState',v_error));
    PERFORM control.assert_worker_lease('retention/maintenance',p_owner,p_fence);
    RETURN v_result;
END $step$;
REVOKE ALL ON FUNCTION system.run_attached_deadlock_read_retention_step(uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- Keep the existing manual retention authorization/replay path for other data
-- classes, but receipts must use the scheduled attached-grace route.
ALTER FUNCTION system.detach_m10_partition(name,name,name,uuid) RENAME TO detach_m10_partition_without_deadlock_receipts;
REVOKE ALL ON FUNCTION system.detach_m10_partition_without_deadlock_receipts(name,name,name,uuid)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION system.detach_m10_partition(p_schema name,p_parent name,p_partition name,p_execution uuid)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog
AS $detach$
BEGIN
    IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
        OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global'
        OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL THEN
        RAISE EXCEPTION 'retention detach requires global SecurityAdministrator authorization' USING ERRCODE='42501'; END IF;
    IF p_schema='events' AND p_parent='deadlock_read_receipt' THEN
        RAISE EXCEPTION 'Deadlock receipt retention requires the scheduled attached grace route' USING ERRCODE='22023'; END IF;
    RETURN system.detach_m10_partition_without_deadlock_receipts(p_schema,p_parent,p_partition,p_execution);
END $detach$;
REVOKE ALL ON FUNCTION system.detach_m10_partition(name,name,name,uuid) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.detach_m10_partition(name,name,name,uuid) TO sqlobserver_server;

-- Existing generic worker, preview, policy mutation and scheduled wrapper
-- retain their contracts; receipts use the scoped route above.

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
   AND policy.data_class<>'m6_deadlock_read_receipts'
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
    WHEN data_class='m6_deadlock_read_receipts' THEN system.deadlock_read_receipt_retention_reason(range_start,range_end,p_now_utc)
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

CREATE OR REPLACE FUNCTION system.update_m10_retention_policy
 (p_data_class text,p_enabled boolean,p_retain_for interval,p_minimum_partitions integer,
  p_expected_revision bigint,p_changed_by text,p_change_reason text)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system
AS $policy_update$
DECLARE next_revision bigint;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' THEN
  RAISE EXCEPTION 'global retention mutation requires SecurityAdministrator authorization context'
   USING ERRCODE='42501';
 END IF;
 IF p_data_class IS NULL OR p_data_class NOT IN
    ('raw_metric_samples','diagnostic_events',
     'm5_activity','m5_requests','m5_waits','m5_blocking',
     'm9_backup_status','m9_agent_history','m9_agent_occurrences',
     'm9_ag_replicas','m9_ag_databases','m9_tempdb',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence',
     'sql_volume_capacity','m7_group_watermarks','m7_group_deltas',
     'm7_query_performance','collection_run_original_copy',
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
    OR (p_data_class='m6_deadlock_read_receipts' AND (p_minimum_partitions<2 OR (p_enabled AND p_retain_for<interval '30 days')))
    OR (p_data_class='collection_run_original_copy'
        AND (p_minimum_partitions<>1 OR (p_enabled
          AND p_retain_for<interval '30 days'))) THEN
  RAISE EXCEPTION 'retention policy bounds rejected' USING ERRCODE='22023';
 END IF;
 PERFORM set_config('sqlobserver.retention_change_reason',p_change_reason,true);
 PERFORM set_config('sqlobserver.retention_changed_by',p_changed_by,true);
 UPDATE system.retention_policy
 SET enabled=p_enabled,retain_for=p_retain_for,
     minimum_partitions_to_keep=p_minimum_partitions,
     policy_revision=policy_revision+1,updated_by=p_changed_by,
     updated_at=clock_timestamp()
 WHERE data_class=p_data_class AND policy_revision=p_expected_revision
 RETURNING policy_revision INTO next_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'retention policy revision conflict' USING ERRCODE='40001';
 END IF;
 RETURN next_revision;
END $policy_update$;

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
 v_outcome:=system.run_attached_deadlock_read_retention_step(p_owner_execution_id,p_fencing_token);
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
