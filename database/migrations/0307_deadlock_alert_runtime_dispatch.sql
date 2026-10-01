-- Integrate deadlock sources into the shared fenced evaluation pipeline.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE alerting.deadlock_reconciliation_cursor(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id) ON DELETE CASCADE,
    last_checked_at timestamptz NOT NULL CHECK(isfinite(last_checked_at))
);
ALTER TABLE alerting.deadlock_reconciliation_cursor ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.deadlock_reconciliation_cursor FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlock_reconciliation_cursor_owner ON alerting.deadlock_reconciliation_cursor
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.deadlock_reconciliation_cursor FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION alerting.list_targets_with_due_alert_work(
    p_work_kind text,p_work_key text,p_owner_execution_id uuid,p_fencing bigint,p_max_results integer)
RETURNS TABLE(target_id uuid) LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $targets$
BEGIN
    IF p_work_kind IS DISTINCT FROM 'evaluation' OR p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile' THEN
        RETURN QUERY SELECT legacy.target_id FROM alerting.list_legacy_targets_with_due_alert_work(
            p_work_kind,p_work_key,p_owner_execution_id,p_fencing,p_max_results) legacy;
        RETURN;
    END IF;
    IF p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Alert discovery limit is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    -- Completed/cancelled capture bodies are no longer fresh evidence. Replay is
    -- owned by the independent result ledger; do not retain three full aggregates
    -- per evaluation forever. Queue metadata keeps cadence/operation history.
    WITH retired AS (
        SELECT c.operation_id FROM alerting.backup_evaluation_capture c
        JOIN alerting.evaluation_queue q ON q.operation_id=c.operation_id
        WHERE q.completed_at IS NOT NULL ORDER BY q.completed_at,c.operation_id
        LIMIT p_max_results FOR UPDATE OF c SKIP LOCKED
    ) DELETE FROM alerting.backup_evaluation_capture c USING retired WHERE c.operation_id=retired.operation_id;
    RETURN QUERY WITH legacy AS MATERIALIZED (
        SELECT old.target_id FROM alerting.list_legacy_targets_with_due_alert_work(
            p_work_kind,p_work_key,p_owner_execution_id,p_fencing,p_max_results) old
    ), candidates AS (
        SELECT old.target_id,coalesce((SELECT min(q.observed_at) FROM alerting.evaluation_queue q
            WHERE q.instance_id=old.target_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp()
                AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())),clock_timestamp()) AS due
        FROM legacy old
        UNION ALL
        SELECT r.instance_id,greatest(r.updated_at,
            coalesce((SELECT max(q.observed_at)+r.evaluation_interval FROM alerting.evaluation_queue q
                WHERE q.instance_id=r.instance_id AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at),
            coalesce((SELECT s.last_observed_at+r.evaluation_interval FROM alerting.rule_state s
                WHERE s.instance_id=r.instance_id AND s.rule_id=r.rule_id),r.updated_at)) AS due
        FROM alerting.rule r JOIN control.observation_target target ON target.instance_id=r.instance_id
        WHERE r.enabled AND r.kind IN (3,4,5) AND target.lifecycle_state='active'
            AND NOT EXISTS(SELECT 1 FROM alerting.evaluation_queue q WHERE q.instance_id=r.instance_id
                AND q.rule_id=r.rule_id AND q.completed_at IS NULL)
        UNION ALL
        SELECT r.instance_id,greatest(r.updated_at,
            coalesce(c.last_checked_at+least(r.evaluation_interval,interval '15 seconds'),r.updated_at),
            coalesce((SELECT s.last_observed_at+r.evaluation_interval FROM alerting.rule_state s
                WHERE s.instance_id=r.instance_id AND s.rule_id=r.rule_id),r.updated_at),
            coalesce((SELECT max(q.observed_at)+r.evaluation_interval FROM alerting.evaluation_queue q
                WHERE q.instance_id=r.instance_id AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at)) AS due
        FROM alerting.rule r JOIN control.observation_target t ON t.instance_id=r.instance_id
        LEFT JOIN alerting.blocking_reconciliation_cursor c ON c.instance_id=r.instance_id
        WHERE r.enabled AND r.kind=6 AND t.lifecycle_state='active'
            AND NOT EXISTS(SELECT 1 FROM alerting.evaluation_queue q WHERE q.instance_id=r.instance_id
                AND q.rule_id=r.rule_id AND q.completed_at IS NULL)
        UNION ALL
        SELECT r.instance_id,greatest(r.updated_at,
            coalesce(c.last_checked_at+least(r.evaluation_interval,interval '15 seconds'),r.updated_at),
            coalesce((SELECT s.last_observed_at+r.evaluation_interval FROM alerting.rule_state s
                WHERE s.instance_id=r.instance_id AND s.rule_id=r.rule_id),r.updated_at),
            coalesce((SELECT max(q.observed_at)+r.evaluation_interval FROM alerting.evaluation_queue q
                WHERE q.instance_id=r.instance_id AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at)) AS due
        FROM alerting.rule r JOIN control.observation_target t ON t.instance_id=r.instance_id
        LEFT JOIN alerting.deadlock_reconciliation_cursor c ON c.instance_id=r.instance_id
        WHERE r.enabled AND r.kind=7 AND t.lifecycle_state='active'
            AND NOT EXISTS(SELECT 1 FROM alerting.evaluation_queue q WHERE q.instance_id=r.instance_id
                AND q.rule_id=r.rule_id AND q.completed_at IS NULL)
        UNION ALL
        -- Disabled/retired targets and disabled rules still need bounded cleanup.
        SELECT c.instance_id,q.observed_at FROM alerting.blocking_evaluation_capture c
        JOIN alerting.evaluation_queue q ON q.operation_id=c.operation_id
        WHERE q.completed_at IS NULL AND q.due_at<=clock_timestamp()
            AND (q.claimed_until IS NULL OR q.claimed_until<=clock_timestamp())
        UNION ALL
        SELECT c.instance_id,q.observed_at FROM alerting.deadlock_evaluation_capture c
        JOIN alerting.evaluation_queue q ON q.operation_id=c.operation_id
        WHERE q.completed_at IS NULL AND q.due_at<=clock_timestamp()
            AND (q.claimed_until IS NULL OR q.claimed_until<=clock_timestamp())
    ) SELECT candidate.target_id FROM candidates candidate WHERE candidate.due<=clock_timestamp()
        GROUP BY candidate.target_id ORDER BY min(candidate.due),candidate.target_id LIMIT p_max_results;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $targets$;

CREATE OR REPLACE FUNCTION alerting.reconcile_due_evaluations(
    p_target_id uuid,p_work_key text,p_owner_execution_id uuid,p_fencing bigint,p_max_results integer)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $reconcile_all$
DECLARE discovered integer;
BEGIN
    IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text
    THEN RAISE EXCEPTION 'Evaluation target scope is required' USING ERRCODE='42501'; END IF;
    IF p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Evaluation reconciliation limit is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    IF EXISTS(SELECT 1 FROM alerting.rule r WHERE r.instance_id=p_target_id AND r.kind IN (3,4,5)) THEN
        PERFORM alerting.reconcile_backup_alert_evaluations(p_target_id,p_work_key,p_owner_execution_id,p_fencing,p_max_results);
    END IF;
    IF EXISTS(SELECT 1 FROM alerting.rule r WHERE r.instance_id=p_target_id AND r.kind=6)
       OR EXISTS(SELECT 1 FROM alerting.blocking_evaluation_capture c WHERE c.instance_id=p_target_id) THEN
        PERFORM alerting.reconcile_blocking_alert_evaluations(p_target_id,p_work_key,p_owner_execution_id,p_fencing,p_max_results);
        INSERT INTO alerting.blocking_reconciliation_cursor(instance_id,last_checked_at) VALUES(p_target_id,clock_timestamp())
            ON CONFLICT(instance_id) DO UPDATE SET last_checked_at=excluded.last_checked_at;
    END IF;
    IF EXISTS(SELECT 1 FROM alerting.rule r WHERE r.instance_id=p_target_id AND r.kind=7)
       OR EXISTS(SELECT 1 FROM alerting.deadlock_evaluation_capture c WHERE c.instance_id=p_target_id) THEN
        PERFORM alerting.reconcile_deadlock_alert_evaluations(p_target_id,p_work_key,p_owner_execution_id,p_fencing,p_max_results);
        INSERT INTO alerting.deadlock_reconciliation_cursor(instance_id,last_checked_at) VALUES(p_target_id,clock_timestamp())
            ON CONFLICT(instance_id) DO UPDATE SET last_checked_at=excluded.last_checked_at;
    END IF;
    SELECT count(*) INTO discovered FROM alerting.reconcile_due_evidence_internal(p_max_results);
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    RETURN discovered;
END $reconcile_all$;

CREATE OR REPLACE FUNCTION alerting.claim_due_evaluations(
    p_target_id uuid,p_work_key text,p_owner_execution_id uuid,p_fencing bigint,p_max_results integer)
RETURNS TABLE(operation_id uuid,observations jsonb,due_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $claim_all$
DECLARE claimed record;
BEGIN
    IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text
    THEN RAISE EXCEPTION 'Evaluation claim scope is required' USING ERRCODE='42501'; END IF;
    IF p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Evaluation claim limit is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    IF EXISTS(SELECT 1 FROM alerting.rule r WHERE r.instance_id=p_target_id AND r.kind IN (3,4,5,6,7))
       OR EXISTS(SELECT 1 FROM alerting.backup_evaluation_capture c WHERE c.instance_id=p_target_id) THEN
        IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile'
        THEN RAISE EXCEPTION 'Backup evaluation coordinator is required' USING ERRCODE='22023'; END IF;
        PERFORM 1 FROM control.observation_target target WHERE target.instance_id=p_target_id FOR UPDATE;
    END IF;
    FOR claimed IN SELECT legacy.* FROM alerting.claim_legacy_due_evaluations(
        p_target_id,p_work_key,p_owner_execution_id,p_fencing,p_max_results) legacy LOOP
        IF claimed.observations->0->>'SourceKind' IN ('backup_full_policy_v2','backup_log_policy_v2','backup_observed_faults_v2') THEN
            PERFORM alerting.assert_backup_evaluation_claim(p_target_id,claimed.observations->0,
                p_work_key,p_owner_execution_id,p_fencing);
        END IF;
        IF claimed.observations->0->>'SourceKind'='blocking_duration_v1' THEN
            PERFORM alerting.assert_blocking_evaluation_claim(p_target_id,claimed.observations->0,p_work_key,p_owner_execution_id,p_fencing);
        END IF;
        IF claimed.observations->0->>'SourceKind'='deadlock_count_v1' THEN
            PERFORM alerting.assert_deadlock_evaluation_claim(p_target_id,claimed.observations->0,p_work_key,p_owner_execution_id,p_fencing);
        END IF;
        operation_id:=claimed.operation_id; observations:=claimed.observations; due_at:=claimed.due_at;
        RETURN NEXT;
    END LOOP;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $claim_all$;
CREATE OR REPLACE FUNCTION alerting.claim_legacy_due_evaluations(p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(operation_id uuid, observations jsonb, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'evaluation target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation queue claim arguments are invalid' USING ERRCODE='22023'; END IF;
  UPDATE alerting.evaluation_queue q SET completed_at=clock_timestamp(),cancel_reason='rule_revision_stale',claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL
    WHERE q.instance_id=p_target_id AND q.completed_at IS NULL AND (q.claimed_until IS NULL OR q.claimed_until<=clock_timestamp()) AND EXISTS (SELECT 1 FROM alerting.rule r WHERE r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.revision<>q.rule_revision);
  -- Reconciliation is coordinator-owned and already completed before target
  -- discovery; a target claim never performs an unscoped evidence scan.
  RETURN QUERY WITH oldest AS MATERIALIZED (SELECT x.operation_id,x.instance_id FROM alerting.evaluation_queue x JOIN alerting.rule xr ON xr.instance_id=x.instance_id AND xr.rule_id=x.rule_id WHERE x.completed_at IS NULL AND x.due_at<=clock_timestamp() AND (x.claimed_until IS NULL OR x.claimed_until<clock_timestamp() OR x.claim_work_key IS NULL) AND xr.enabled AND xr.revision=x.rule_revision ORDER BY x.observed_at,x.sample_id,x.run_id NULLS LAST,x.operation_id FOR UPDATE OF x SKIP LOCKED LIMIT p_max_results), candidates AS MATERIALIZED (SELECT oldest.operation_id FROM oldest WHERE instance_id=p_target_id), claimed AS (
  UPDATE alerting.evaluation_queue q
    SET claimed_until=clock_timestamp()+interval '30 seconds',claim_work_key=p_work_key,claim_owner_execution_id=p_owner_execution_id,claim_fencing=p_fencing,completed_at=NULL
    WHERE q.instance_id=p_target_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp() OR q.claim_work_key IS NULL)
    AND q.operation_id IN (SELECT candidates.operation_id FROM candidates)
    RETURNING q.operation_id,q.observations,q.due_at) SELECT claimed.operation_id,claimed.observations,claimed.due_at FROM claimed ORDER BY (claimed.observations->0->>'ObservedAtUtc')::timestamptz,claimed.operation_id;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue(p_instance_id uuid,p_observations jsonb,p_decisions jsonb,
    p_work_key text,p_owner_execution_id uuid,p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer,changed_count integer,suppressed_count integer,completed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $dispatch$
DECLARE v_observations jsonb; v_decisions jsonb; v_decision jsonb; v_outcome record;
    v_evaluated integer:=0; v_changed integer:=0; v_suppressed integer:=0;
BEGIN
    IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    THEN RAISE EXCEPTION 'Evaluation target scope is required' USING ERRCODE='42501'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_lease_fencing);
    IF EXISTS(SELECT 1 FROM jsonb_array_elements(p_observations) o LEFT JOIN alerting.rule r
        ON r.instance_id=p_instance_id AND r.rule_id=(o->>'RuleId')::uuid
        WHERE (r.kind=7 OR o->>'SourceKind'='deadlock_count_v1')
          AND (r.kind IS DISTINCT FROM 7 OR o->>'SourceKind' IS DISTINCT FROM 'deadlock_count_v1'))
    THEN RAISE EXCEPTION 'Evaluation deadlock rule/source binding is invalid' USING ERRCODE='22023'; END IF;
    IF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(p_observations) o WHERE o->>'SourceKind'='deadlock_count_v1') THEN
        RETURN QUERY SELECT * FROM alerting.evaluate_before_deadlock_activation(p_instance_id,p_observations,p_decisions,
            p_work_key,p_owner_execution_id,p_lease_fencing); RETURN;
    END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile' OR jsonb_array_length(p_observations) NOT BETWEEN 1 AND 100
       OR jsonb_array_length(p_observations) IS DISTINCT FROM jsonb_array_length(p_decisions)
       OR (SELECT count(DISTINCT (o->>'OperationId')::uuid) FROM jsonb_array_elements(p_observations) o)<>jsonb_array_length(p_observations)
       OR (SELECT jsonb_agg(o ORDER BY (o->>'OperationId')::uuid) FROM jsonb_array_elements(p_observations) o)
          IS DISTINCT FROM (SELECT jsonb_agg(d->'Observation' ORDER BY (d->'Observation'->>'OperationId')::uuid)
            FROM jsonb_array_elements(p_decisions) d)
    THEN RAISE EXCEPTION 'Deadlock evaluation batch bindings are invalid' USING ERRCODE='22023'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(p_instance_id::text,0));
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id FOR UPDATE;
    SELECT coalesce(jsonb_agg(o),'[]'::jsonb) INTO v_observations FROM jsonb_array_elements(p_observations) o
        WHERE o->>'SourceKind' IS DISTINCT FROM 'deadlock_count_v1';
    SELECT coalesce(jsonb_agg(d),'[]'::jsonb) INTO v_decisions FROM jsonb_array_elements(p_decisions) d
        WHERE d->'Observation'->>'SourceKind' IS DISTINCT FROM 'deadlock_count_v1';
    IF jsonb_array_length(v_observations)>0 THEN
        SELECT * INTO STRICT v_outcome FROM alerting.evaluate_before_deadlock_activation(p_instance_id,v_observations,v_decisions,
            p_work_key,p_owner_execution_id,p_lease_fencing);
        v_evaluated:=v_outcome.evaluated_count; v_changed:=v_outcome.changed_count; v_suppressed:=v_outcome.suppressed_count;
    END IF;
    FOR v_decision IN SELECT d FROM jsonb_array_elements(p_decisions) d
        WHERE d->'Observation'->>'SourceKind'='deadlock_count_v1'
        ORDER BY (d->'Observation'->>'ObservedAtUtc')::timestamptz,(d->'Observation'->>'RuleId')::uuid,
            (d->'Observation'->>'OperationId')::uuid LOOP
        SELECT * INTO STRICT v_outcome FROM alerting.commit_deadlock_alert_evaluation(p_instance_id,v_decision->'Observation',
            v_decision,p_work_key,p_owner_execution_id,p_lease_fencing);
        v_evaluated:=v_evaluated+v_outcome.evaluated_count; v_changed:=v_changed+v_outcome.changed_count;
        v_suppressed:=v_suppressed+v_outcome.suppressed_count;
    END LOOP;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_lease_fencing);
    RETURN QUERY SELECT v_evaluated,v_changed,v_suppressed,clock_timestamp();
END $dispatch$;
REVOKE ALL ON FUNCTION alerting.evaluate_before_deadlock_activation(uuid,jsonb,jsonb,text,uuid,bigint),
    alerting.claim_legacy_due_evaluations(uuid,text,uuid,bigint,integer),
    alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint),
    alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer),
    alerting.reconcile_due_evaluations(uuid,text,uuid,bigint,integer),
    alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint),
    alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer),
    alerting.reconcile_due_evaluations(uuid,text,uuid,bigint,integer),
    alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer) TO sqlobserver_collector;
