-- A policy change during detach grace must restore the partition to its
-- parent. Otherwise the old policy fences the drop forever while the data
-- remains invisible to normal reads. Reconcile one partition per fenced step.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.retention_execution
    ADD COLUMN reattached_at timestamptz,
    ADD COLUMN reattach_next_attempt_at timestamptz;
ALTER TABLE system.retention_execution
    DROP CONSTRAINT retention_execution_state_check;
ALTER TABLE system.retention_execution
    ADD CONSTRAINT ck_retention_execution_state CHECK
      (state IN ('previewed','blocked','detached','grace','dropped','failed','retry','reattached')),
    ADD CONSTRAINT ck_retention_execution_reattached CHECK
      (state <> 'reattached' OR reattached_at IS NOT NULL);

CREATE FUNCTION system.reconcile_m10_retention_policy_change(
    p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC'
AS $reconcile$
DECLARE
    v_execution system.retention_execution%ROWTYPE;
    v_registry system.partition_registry%ROWTYPE;
    v_activity_id uuid;
    v_digest bytea;
    v_error_code text;
    v_error_message text;
    v_outcome text;
BEGIN
    PERFORM control.assert_worker_lease(
        'retention/maintenance',p_owner_execution_id,p_fencing_token);
    SELECT execution.* INTO v_execution
    FROM system.retention_execution AS execution
    WHERE execution.state IN ('grace','retry','failed')
      AND (execution.reattach_next_attempt_at IS NULL
           OR execution.reattach_next_attempt_at<=clock_timestamp())
      AND NOT EXISTS (
          SELECT 1 FROM system.retention_policy AS policy
          WHERE policy.data_class=execution.data_class
            AND policy.parent_schema=execution.parent_schema
            AND policy.parent_table=execution.parent_table
            AND policy.policy_revision=execution.policy_revision
            AND policy.enabled AND policy.retain_for IS NOT NULL)
    ORDER BY execution.drop_after,execution.execution_id
    LIMIT 1 FOR UPDATE OF execution SKIP LOCKED;
    IF NOT FOUND THEN RETURN 'idle'; END IF;

    SELECT registry.* INTO v_registry
    FROM system.partition_registry AS registry
    WHERE registry.parent_schema=v_execution.parent_schema
      AND registry.parent_table=v_execution.parent_table
      AND registry.partition_name=v_execution.partition_name
      AND registry.lifecycle_state='detached'
    FOR UPDATE;
    IF NOT FOUND THEN RETURN 'idle'; END IF;
    IF v_registry.partition_schema<>v_execution.parent_schema
       OR v_registry.range_start<>v_execution.range_start
       OR v_registry.range_end<>v_execution.range_end
       OR sha256(convert_to(v_registry.parent_schema::text||'|'
          ||v_registry.parent_table::text||'|'
          ||v_registry.partition_name::text||'|'
          ||v_registry.range_start::text||'|'
          ||v_registry.range_end::text,'UTF8'))<>v_execution.registry_revision THEN
        RAISE EXCEPTION 'retention reattach registry identity changed'
            USING ERRCODE='55000';
    END IF;
    IF NOT pg_try_advisory_xact_lock(hashtextextended(
        v_execution.parent_schema::text||'.'||v_execution.parent_table::text||'.'
        ||v_execution.partition_name::text,1)) THEN RETURN 'idle'; END IF;

    v_activity_id:=gen_random_uuid();
    v_digest:=sha256(convert_to('retention.reattach|'
        ||v_execution.execution_id::text,'UTF8'));
    BEGIN
        -- The original drop path can be fenced by a policy update after
        -- candidate selection. Confirm the revision again under the lock.
        IF EXISTS (
            SELECT 1 FROM system.retention_policy AS policy
            WHERE policy.data_class=v_execution.data_class
              AND policy.parent_schema=v_execution.parent_schema
              AND policy.parent_table=v_execution.parent_table
              AND policy.policy_revision=v_execution.policy_revision
              AND policy.enabled AND policy.retain_for IS NOT NULL) THEN
            RETURN 'idle';
        END IF;
        IF to_regclass(format('%I.%I',v_registry.partition_schema,
             v_registry.partition_name)) IS NULL THEN
            RAISE EXCEPTION 'retention reattach relation is missing'
                USING ERRCODE='55000';
        END IF;
        IF EXISTS (
             SELECT 1 FROM pg_inherits AS inheritance
             WHERE inheritance.inhrelid=format('%I.%I',
               v_registry.partition_schema,v_registry.partition_name)::regclass) THEN
            RAISE EXCEPTION 'retention reattach relation is attached elsewhere'
                USING ERRCODE='55000';
        END IF;
        EXECUTE format('ALTER TABLE %I.%I ATTACH PARTITION %I.%I '
                       ||'FOR VALUES FROM (%L) TO (%L)',
            v_registry.parent_schema,v_registry.parent_table,
            v_registry.partition_schema,v_registry.partition_name,
            v_registry.range_start,v_registry.range_end);
        UPDATE system.partition_registry
        SET lifecycle_state='attached',detached_at=NULL
        WHERE parent_schema=v_registry.parent_schema
          AND parent_table=v_registry.parent_table
          AND partition_name=v_registry.partition_name
          AND lifecycle_state='detached';
        UPDATE system.retention_execution
        SET state='reattached',reattached_at=clock_timestamp(),
            reattach_next_attempt_at=NULL,last_error=NULL
        WHERE execution_id=v_execution.execution_id;
        PERFORM control.assert_worker_lease(
            'retention/maintenance',p_owner_execution_id,p_fencing_token);
        INSERT INTO audit.activity
          (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
           authorization_result,outcome,subject_kind,subject_identifier,
           correlation_id,parameter_digest,safe_details)
        VALUES(clock_timestamp(),v_activity_id,'system','collector/retention',
          'retention.partition.reattach','not_applicable','succeeded',
          'partition',v_registry.partition_name::text,v_execution.execution_id,
          v_digest,jsonb_build_object('dataClass',v_execution.data_class,
            'oldPolicyRevision',v_execution.policy_revision,
            'fencingToken',p_fencing_token));
        RETURN 'reattached';
    EXCEPTION WHEN SQLSTATE '40001' THEN RAISE;
    WHEN OTHERS THEN
        v_error_code:=SQLSTATE;
        v_error_message:=SQLERRM;
        -- Restoration must keep retrying: a terminal failure would strand
        -- detached data outside every normal read indefinitely.
        v_outcome:='retry';
        UPDATE system.retention_execution
        SET state=v_outcome,attempt=least(attempt+1,20),last_error=v_error_message,
            reattach_next_attempt_at=clock_timestamp()+interval '1 hour'
        WHERE execution_id=v_execution.execution_id;
        PERFORM control.assert_worker_lease(
            'retention/maintenance',p_owner_execution_id,p_fencing_token);
        INSERT INTO audit.activity
          (occurred_at,activity_id,actor_kind,actor_identifier,action_name,
           authorization_result,outcome,subject_kind,subject_identifier,
           correlation_id,parameter_digest,safe_details)
        VALUES(clock_timestamp(),v_activity_id,'system','collector/retention',
          'retention.partition.reattach','not_applicable','failed',
          'partition',v_registry.partition_name::text,v_execution.execution_id,
          v_digest,jsonb_build_object('dataClass',v_execution.data_class,
            'oldPolicyRevision',v_execution.policy_revision,
            'sqlState',v_error_code));
        RETURN v_outcome;
    END;
END $reconcile$;
REVOKE ALL ON FUNCTION system.reconcile_m10_retention_policy_change(uuid,bigint)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION system.run_m10_retention_step_safe(
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
    RETURN system.run_m10_retention_step(
        p_owner_execution_id,p_fencing_token);
END $safe_step$;
REVOKE ALL ON FUNCTION system.run_m10_retention_step_safe(uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.run_m10_retention_step_safe(uuid,bigint)
    TO sqlobserver_collector;
REVOKE EXECUTE ON FUNCTION system.run_m10_retention_step(uuid,bigint)
    FROM sqlobserver_collector;
