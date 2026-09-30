-- Stage backup evidence in the existing fenced queue. Rule administration stays catalog-gated.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE alerting.rule DROP CONSTRAINT rule_kind_check;
ALTER TABLE alerting.rule DROP CONSTRAINT rule_check;
ALTER TABLE alerting.rule ADD CONSTRAINT rule_kind_check CHECK(kind BETWEEN 1 AND 5);
ALTER TABLE alerting.rule ADD CONSTRAINT rule_metric_binding CHECK(
    (kind=1 AND metric_id IS NOT NULL) OR (kind BETWEEN 2 AND 5 AND metric_id IS NULL));
ALTER TABLE alerting.rule ADD CONSTRAINT rule_backup_confirmation CHECK(kind NOT IN (3,4,5) OR (
    comparison=1 AND threshold=0 AND hysteresis=0 AND evaluation_interval>=interval '15 seconds'
    AND confirmation_window>=evaluation_interval*(greatest(confirmation_count,clear_confirmation_count)-1)));

CREATE TABLE alerting.backup_evaluation_capture(
    operation_id uuid PRIMARY KEY REFERENCES alerting.evaluation_queue(operation_id) ON DELETE CASCADE,
    instance_id uuid NOT NULL,
    rule_id uuid NOT NULL,
    rule_kind smallint NOT NULL CHECK(rule_kind IN (3,4,5)),
    target_revision bigint NOT NULL CHECK(target_revision>0),
    rule_revision bigint NOT NULL CHECK(rule_revision>0),
    read_at timestamptz NOT NULL CHECK(isfinite(read_at)),
    payload jsonb CHECK(payload IS NULL OR octet_length(payload::text)<=16384),
    source_digest text,
    FOREIGN KEY(instance_id,rule_id) REFERENCES alerting.rule(instance_id,rule_id),
    CHECK((payload IS NULL AND source_digest IS NULL) OR (payload IS NOT NULL AND source_digest IS NOT NULL
        AND source_digest=encode(sha256(convert_to(payload::text,'UTF8')),'hex')))
);
ALTER TABLE alerting.backup_evaluation_capture ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.backup_evaluation_capture FORCE ROW LEVEL SECURITY;
CREATE POLICY backup_evaluation_capture_owner ON alerting.backup_evaluation_capture
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.backup_evaluation_capture FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE INDEX backup_evaluation_capture_rule_time ON alerting.backup_evaluation_capture(instance_id,rule_id,read_at DESC);
CREATE UNIQUE INDEX backup_evaluation_one_pending_rule ON alerting.evaluation_queue(instance_id,rule_id)
    WHERE completed_at IS NULL AND observations->0->>'SourceKind'
        IN ('backup_full_policy_v2','backup_log_policy_v2','backup_observed_faults_v2');

CREATE FUNCTION alerting.reconcile_backup_alert_evaluations(
    p_instance uuid,p_work_key text,p_owner uuid,p_fencing bigint,p_limit integer)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $reconcile$
DECLARE v_target_revision bigint; v_target_lifecycle text; capture record; rule record; observation jsonb; operation uuid; inserted integer:=0;
BEGIN
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Backup queue target scope is invalid' USING ERRCODE='42501'; END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile' OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Backup queue coordinator or limit is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    SELECT target.revision,target.lifecycle_state INTO v_target_revision,v_target_lifecycle
        FROM control.observation_target target WHERE target.instance_id=p_instance FOR UPDATE;
    IF v_target_revision IS NULL THEN RAISE EXCEPTION 'Backup queue target is required' USING ERRCODE='42501'; END IF;
    IF v_target_lifecycle<>'active' THEN
        -- A target disabled between discovery and reconciliation must not abort
        -- every other target's source transaction. Retire only available work.
        WITH inactive AS (
            SELECT q.operation_id FROM alerting.evaluation_queue q WHERE q.instance_id=p_instance AND q.completed_at IS NULL
                AND (q.claimed_until IS NULL OR q.claimed_until<=clock_timestamp())
                AND q.observations->0->>'SourceKind' IN ('backup_full_policy_v2','backup_log_policy_v2','backup_observed_faults_v2')
            ORDER BY q.observed_at,q.operation_id LIMIT p_limit FOR UPDATE OF q SKIP LOCKED
        ) UPDATE alerting.evaluation_queue q SET completed_at=clock_timestamp(),cancel_reason='target_not_active',
            claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=NULL
          FROM inactive WHERE q.operation_id=inactive.operation_id;
        PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
        RETURN 0;
    END IF;
    SELECT * INTO STRICT capture FROM alerting.read_backup_alert_capture(p_instance,v_target_revision,p_owner,p_fencing);
    -- Keep administration's target-before-rule/state order. Never cancel another
    -- live claim; after its lease expires it is either reclaimed exactly or superseded.
    PERFORM r.rule_id FROM alerting.rule r WHERE r.instance_id=p_instance AND r.kind IN (3,4,5)
        ORDER BY r.rule_id FOR UPDATE;
    WITH stale AS (
        SELECT q.operation_id,
            CASE WHEN NOT r.enabled THEN 'rule_disabled'
                 WHEN r.revision<>c.rule_revision THEN 'rule_revision_stale'
                 WHEN c.target_revision<>v_target_revision THEN 'target_revision_stale'
                 WHEN c.read_at<capture.read_at-interval '2 minutes' THEN 'backup_capture_expired'
                 ELSE 'backup_source_superseded' END AS reason
        FROM alerting.evaluation_queue q JOIN alerting.backup_evaluation_capture c ON c.operation_id=q.operation_id
        JOIN alerting.rule r ON r.instance_id=c.instance_id AND r.rule_id=c.rule_id
        WHERE q.instance_id=p_instance AND c.instance_id=p_instance AND q.completed_at IS NULL
            AND (q.claimed_until IS NULL OR q.claimed_until<=capture.read_at)
            AND (NOT r.enabled OR r.revision<>c.rule_revision OR r.kind<>c.rule_kind OR c.target_revision<>v_target_revision
                OR c.read_at<capture.read_at-interval '2 minutes' OR c.payload IS DISTINCT FROM capture.payload
                OR c.source_digest IS DISTINCT FROM capture.source_digest)
        ORDER BY q.observed_at,q.operation_id LIMIT p_limit FOR UPDATE OF q SKIP LOCKED
    ) UPDATE alerting.evaluation_queue q SET completed_at=capture.read_at,cancel_reason=stale.reason,
        claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=NULL
      FROM stale WHERE q.operation_id=stale.operation_id;

    FOR rule IN
        SELECT r.* FROM alerting.rule r WHERE r.instance_id=p_instance AND r.enabled AND r.kind IN (3,4,5)
            AND NOT EXISTS(SELECT 1 FROM alerting.evaluation_queue q
                WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id AND q.completed_at IS NULL)
            AND coalesce((SELECT max(q.observed_at)+r.evaluation_interval FROM alerting.evaluation_queue q
                WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at)<=capture.read_at
            AND coalesce((SELECT s.last_observed_at+r.evaluation_interval FROM alerting.rule_state s
                WHERE s.instance_id=p_instance AND s.rule_id=r.rule_id),r.updated_at)<=capture.read_at
        ORDER BY coalesce((SELECT max(q.observed_at) FROM alerting.evaluation_queue q
            WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at),r.rule_id
        LIMIT p_limit
    LOOP
        observation:=alerting.backup_alert_observation(p_instance,v_target_revision,rule.rule_id,rule.kind,
            capture.read_at,capture.payload,capture.source_digest);
        operation:=(observation->>'OperationId')::uuid;
        INSERT INTO alerting.evaluation_queue(operation_id,instance_id,rule_id,observed_at,observations,due_at,next_due_at,
            evidence_digest,sample_id,run_id,rule_revision)
        VALUES(operation,p_instance,rule.rule_id,capture.read_at,jsonb_build_array(observation),capture.read_at,
            capture.read_at+rule.evaluation_interval,decode(observation->>'EvidenceDigest','hex'),observation->>'SampleId',
            (observation->>'RunId')::uuid,rule.revision);
        INSERT INTO alerting.backup_evaluation_capture(operation_id,instance_id,rule_id,rule_kind,target_revision,
            rule_revision,read_at,payload,source_digest)
        VALUES(operation,p_instance,rule.rule_id,rule.kind,v_target_revision,rule.revision,capture.read_at,capture.payload,capture.source_digest);
        inserted:=inserted+1;
    END LOOP;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    RETURN inserted;
END $reconcile$;

CREATE FUNCTION alerting.assert_backup_evaluation_claim(
    p_instance uuid,p_observation jsonb,p_work_key text,p_owner uuid,p_fencing bigint)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $claim$
DECLARE capture record; rule record; queued record;
BEGIN
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Backup claim target scope is invalid' USING ERRCODE='42501'; END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile'
    THEN RAISE EXCEPTION 'Backup claim coordinator is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    -- Acquire the target lock before rule and queue locks. The observation guard
    -- keeps cache publication frozen through the future state/outbox transaction.
    PERFORM 1 FROM control.observation_target target WHERE target.instance_id=p_instance FOR UPDATE;
    SELECT c.* INTO capture FROM alerting.backup_evaluation_capture c
        WHERE c.operation_id=(p_observation->>'OperationId')::uuid AND c.instance_id=p_instance;
    IF NOT FOUND THEN RAISE EXCEPTION 'Backup claim capture is missing' USING ERRCODE='40001'; END IF;
    SELECT r.* INTO rule FROM alerting.rule r WHERE r.instance_id=p_instance AND r.rule_id=capture.rule_id FOR SHARE;
    SELECT q.* INTO queued FROM alerting.evaluation_queue q WHERE q.operation_id=capture.operation_id FOR UPDATE;
    IF NOT rule.enabled OR rule.kind IS DISTINCT FROM capture.rule_kind OR rule.revision IS DISTINCT FROM capture.rule_revision
        OR queued.instance_id IS DISTINCT FROM p_instance OR queued.rule_id IS DISTINCT FROM capture.rule_id
        OR queued.rule_revision IS DISTINCT FROM capture.rule_revision OR queued.observed_at IS DISTINCT FROM capture.read_at
        OR queued.observations IS DISTINCT FROM jsonb_build_array(p_observation) OR queued.completed_at IS NOT NULL
        OR queued.claimed_until IS NULL OR queued.claimed_until<=clock_timestamp()
        OR queued.claim_work_key IS DISTINCT FROM p_work_key OR queued.claim_owner_execution_id IS DISTINCT FROM p_owner
        OR queued.claim_fencing IS DISTINCT FROM p_fencing
        OR queued.evidence_digest IS DISTINCT FROM decode(p_observation->>'EvidenceDigest','hex')
        OR queued.sample_id IS DISTINCT FROM p_observation->>'SampleId'
        OR queued.run_id IS DISTINCT FROM (p_observation->>'RunId')::uuid
    THEN RAISE EXCEPTION 'Backup evaluation is not an exact current claim' USING ERRCODE='40001'; END IF;
    PERFORM alerting.assert_backup_alert_observation(p_instance,capture.target_revision,capture.rule_id,capture.rule_kind,
        capture.read_at,capture.payload,p_observation,p_owner,p_fencing);
END $claim$;

ALTER FUNCTION alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer)
    RENAME TO list_legacy_targets_with_due_alert_work;
CREATE FUNCTION alerting.list_targets_with_due_alert_work(
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
    SELECT count(*) INTO discovered FROM alerting.reconcile_due_evidence_internal(p_max_results);
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    RETURN discovered;
END $reconcile_all$;

ALTER FUNCTION alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer)
    RENAME TO claim_legacy_due_evaluations;
CREATE FUNCTION alerting.claim_due_evaluations(
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
    IF EXISTS(SELECT 1 FROM alerting.rule r WHERE r.instance_id=p_target_id AND r.kind IN (3,4,5))
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
        operation_id:=claimed.operation_id; observations:=claimed.observations; due_at:=claimed.due_at;
        RETURN NEXT;
    END LOOP;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $claim_all$;

REVOKE ALL ON FUNCTION alerting.reconcile_backup_alert_evaluations(uuid,text,uuid,bigint,integer),
    alerting.assert_backup_evaluation_claim(uuid,jsonb,text,uuid,bigint),
    alerting.list_legacy_targets_with_due_alert_work(text,text,uuid,bigint,integer),
    alerting.claim_legacy_due_evaluations(uuid,text,uuid,bigint,integer),
    alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer),
    alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer),
    alerting.reconcile_due_evaluations(uuid,text,uuid,bigint,integer)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer),
    alerting.reconcile_due_evaluations(uuid,text,uuid,bigint,integer),
    alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer) TO sqlobserver_collector;
