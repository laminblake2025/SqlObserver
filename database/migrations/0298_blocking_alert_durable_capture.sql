-- Private blocking queue foundation. Catalog and runtime dispatch remain gated
-- until a subsequent migration independently verifies state/outbox transitions.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE alerting.rule DROP CONSTRAINT rule_kind_check;
ALTER TABLE alerting.rule DROP CONSTRAINT rule_metric_binding;
ALTER TABLE alerting.rule ADD CONSTRAINT rule_kind_check CHECK(kind BETWEEN 1 AND 6);
ALTER TABLE alerting.rule ADD CONSTRAINT rule_metric_binding CHECK(
    (kind=1 AND metric_id IS NOT NULL) OR (kind BETWEEN 2 AND 6 AND metric_id IS NULL));
ALTER TABLE alerting.rule ADD CONSTRAINT rule_blocking_confirmation CHECK(kind<>6 OR (
    comparison IN (1,2) AND threshold BETWEEN 0 AND 86400 AND hysteresis BETWEEN 0 AND threshold
    AND evaluation_interval BETWEEN interval '5 seconds' AND interval '1 minute'
    AND confirmation_window>=evaluation_interval*(greatest(confirmation_count,clear_confirmation_count)-1)));

CREATE TABLE alerting.blocking_evaluation_capture(
    operation_id uuid PRIMARY KEY REFERENCES alerting.evaluation_queue(operation_id) ON DELETE CASCADE,
    instance_id uuid NOT NULL,
    rule_id uuid NOT NULL,
    target_revision bigint NOT NULL CHECK(target_revision>0),
    rule_revision bigint NOT NULL CHECK(rule_revision>0),
    read_at timestamptz NOT NULL CHECK(isfinite(read_at)),
    payload jsonb NOT NULL CHECK(octet_length(payload::text)<=2048),
    source_digest text NOT NULL CHECK(source_digest=encode(sha256(convert_to(payload::text,'UTF8')),'hex')),
    FOREIGN KEY(instance_id,rule_id) REFERENCES alerting.rule(instance_id,rule_id)
);
ALTER TABLE alerting.blocking_evaluation_capture ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.blocking_evaluation_capture FORCE ROW LEVEL SECURITY;
CREATE POLICY blocking_evaluation_capture_owner ON alerting.blocking_evaluation_capture
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.blocking_evaluation_capture FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE INDEX blocking_evaluation_capture_rule_time ON alerting.blocking_evaluation_capture(instance_id,rule_id,read_at DESC);
CREATE UNIQUE INDEX blocking_evaluation_one_pending_rule ON alerting.evaluation_queue(instance_id,rule_id)
    WHERE completed_at IS NULL AND observations->0->>'SourceKind'='blocking_duration_v1';
-- The completed operation ledger survives capture-body cleanup. Re-polling a
-- committed snapshot cannot manufacture a second fire/clear confirmation.
CREATE INDEX blocking_evaluation_consumed_source ON alerting.evaluation_queue(
    instance_id,rule_id,rule_revision,(observations->0->>'SourceDigest'))
    WHERE completed_at IS NOT NULL AND cancel_reason IS NULL
      AND observations->0->>'SourceKind'='blocking_duration_v1';

CREATE FUNCTION alerting.blocking_alert_observation(
    p_instance uuid,p_revision bigint,p_rule uuid,p_read_at timestamptz,p_payload jsonb,p_digest text)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $observation$
DECLARE reason text; operation uuid; run uuid; value double precision;
BEGIN
    IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'::uuid
       OR p_rule IS NULL OR p_rule='00000000-0000-0000-0000-000000000000'::uuid
       OR p_revision IS NULL OR p_revision<=0 OR p_read_at IS NULL OR NOT isfinite(p_read_at)
       OR p_payload IS NULL OR octet_length(p_payload::text)>2048
       OR NOT reporting.backup_cache_keys(p_payload,ARRAY['schemaVersion','targetId','targetRevision','validUntilUtc',
           'scheduleRevision','runId','completedAtUtc','sourceObservedAtUtc','retainedEdges','maximumWaitMilliseconds','reason','completionDigest'])
       OR p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb
       OR (p_payload->>'targetId')::uuid IS DISTINCT FROM p_instance
       OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_revision
       OR p_digest IS DISTINCT FROM encode(sha256(convert_to(p_payload::text,'UTF8')),'hex')
       OR coalesce(p_payload->>'reason','') NOT IN ('current','collector_disabled','source_unavailable',
           'source_stale','source_incomplete','source_inconsistent')
    THEN RAISE EXCEPTION 'Blocking alert capture envelope is invalid' USING ERRCODE='22023'; END IF;
    IF p_payload->>'reason'='current' THEN
        IF (p_payload->>'validUntilUtc')::timestamptz IS NULL
           OR NOT isfinite((p_payload->>'validUntilUtc')::timestamptz)
           OR (p_payload->>'validUntilUtc')::timestamptz<=p_read_at
           OR (p_payload->>'validUntilUtc')::timestamptz>p_read_at+interval '2 minutes'
           OR (p_payload->>'runId')::uuid IS NULL OR (p_payload->>'runId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
           OR coalesce(p_payload->>'scheduleRevision','') !~ '^[1-9][0-9]*$'
           OR (p_payload->>'completedAtUtc')::timestamptz IS NULL
           OR NOT isfinite((p_payload->>'completedAtUtc')::timestamptz)
           OR (p_payload->>'completedAtUtc')::timestamptz>p_read_at
           OR coalesce(p_payload->>'completionDigest','') !~ '^[0-9a-f]{64}$'
           OR coalesce(p_payload->'retainedEdges','null'::jsonb)::text !~ '^(0|[1-9][0-9]{0,3})$'
           OR (p_payload->>'retainedEdges')::integer>1024
           OR coalesce(p_payload->'maximumWaitMilliseconds','null'::jsonb)::text !~ '^(0|[1-9][0-9]{0,15})$'
           OR (p_payload->>'maximumWaitMilliseconds')::bigint>9007199254740991
           OR (p_payload->>'retainedEdges')::integer=0 AND (
               p_payload->'maximumWaitMilliseconds' IS DISTINCT FROM '0'::jsonb OR p_payload->'sourceObservedAtUtc' IS DISTINCT FROM 'null'::jsonb)
           OR (p_payload->>'retainedEdges')::integer>0 AND (
               (p_payload->>'sourceObservedAtUtc')::timestamptz IS NULL
               OR NOT isfinite((p_payload->>'sourceObservedAtUtc')::timestamptz)
               OR (p_payload->>'sourceObservedAtUtc')::timestamptz>p_read_at)
        THEN RAISE EXCEPTION 'Blocking alert current value is invalid' USING ERRCODE='22023'; END IF;
        value:=(p_payload->>'maximumWaitMilliseconds')::double precision/1000;
    ELSIF p_payload->'maximumWaitMilliseconds' IS DISTINCT FROM 'null'::jsonb
       OR p_payload->'validUntilUtc' IS DISTINCT FROM 'null'::jsonb
    THEN RAISE EXCEPTION 'Unknown blocking evidence cannot have a value' USING ERRCODE='22023'; END IF;
    run:=(p_payload->>'runId')::uuid;
    reason:='blocking_duration_v1|target='||p_revision::text||'|reason='||(p_payload->>'reason')||'|digest='||p_digest;
    operation:=alerting.canonical_operation_id(p_instance,p_rule,p_read_at,reason);
    RETURN jsonb_build_object('TargetId',p_instance,'RuleId',p_rule,'ObservedAtUtc',p_read_at,
        'Value',value,'CollectorHealthy',NULL,'Reason',reason,'SampleId',operation::text,'RunId',run,
        'SourceKind','blocking_duration_v1','MetricId',NULL,'SourceCollector','blocking.current','SourceVersion','1',
        'SourceSchemaVersion',1,'SourceDigest',p_digest,'OperationId',operation,
        'EvidenceDigest',encode(alerting.canonical_evidence_sha256(p_instance,p_rule,'blocking_duration_v1',NULL,
            'blocking.current','1',1,p_digest,p_read_at,operation::text,run,value,NULL,reason),'hex'));
END $observation$;

CREATE FUNCTION alerting.reconcile_blocking_alert_evaluations(
    p_instance uuid,p_work_key text,p_owner uuid,p_fencing bigint,p_limit integer)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $reconcile$
DECLARE v_revision bigint; lifecycle text; capture record; rule record; observation jsonb; operation uuid; inserted integer:=0;
BEGIN
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Blocking queue target scope is invalid' USING ERRCODE='42501'; END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile' OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Blocking queue coordinator or limit is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    SELECT t.revision,t.lifecycle_state INTO v_revision,lifecycle FROM control.observation_target t
        WHERE t.instance_id=p_instance FOR UPDATE;
    IF v_revision IS NULL THEN RAISE EXCEPTION 'Blocking queue target is required' USING ERRCODE='42501'; END IF;
    IF lifecycle<>'active' THEN
        WITH inactive AS (
            SELECT q.operation_id FROM alerting.evaluation_queue q WHERE q.instance_id=p_instance AND q.completed_at IS NULL
                AND (q.claimed_until IS NULL OR q.claimed_until<=clock_timestamp())
                AND q.observations->0->>'SourceKind'='blocking_duration_v1'
            ORDER BY q.observed_at,q.operation_id LIMIT p_limit FOR UPDATE OF q SKIP LOCKED
        ) UPDATE alerting.evaluation_queue q SET completed_at=clock_timestamp(),cancel_reason='target_not_active',
            claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=NULL
          FROM inactive WHERE q.operation_id=inactive.operation_id;
        WITH retired AS (
            SELECT c.operation_id FROM alerting.blocking_evaluation_capture c JOIN alerting.evaluation_queue q ON q.operation_id=c.operation_id
            WHERE c.instance_id=p_instance AND q.completed_at IS NOT NULL ORDER BY q.completed_at,c.operation_id LIMIT p_limit FOR UPDATE OF c SKIP LOCKED
        ) DELETE FROM alerting.blocking_evaluation_capture c USING retired WHERE c.operation_id=retired.operation_id;
        PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing); RETURN 0;
    END IF;
    SELECT * INTO STRICT capture FROM alerting.read_blocking_alert_evidence(p_instance,v_revision,p_owner,p_fencing);
    -- Target-before-rule-before-queue ordering freezes administration and source
    -- publication. Superseding work never steals another unexpired claim.
    PERFORM r.rule_id FROM alerting.rule r WHERE r.instance_id=p_instance AND r.kind=6 ORDER BY r.rule_id FOR UPDATE;
    WITH stale AS (
        SELECT q.operation_id,CASE WHEN NOT r.enabled THEN 'rule_disabled'
            WHEN r.revision<>c.rule_revision OR r.kind<>6 THEN 'rule_revision_stale'
            WHEN c.target_revision<>v_revision THEN 'target_revision_stale'
            WHEN c.read_at<=capture.read_at-interval '2 minutes' THEN 'blocking_capture_expired'
            ELSE 'blocking_source_superseded' END AS reason
        FROM alerting.evaluation_queue q JOIN alerting.blocking_evaluation_capture c ON c.operation_id=q.operation_id
        JOIN alerting.rule r ON r.instance_id=c.instance_id AND r.rule_id=c.rule_id
        WHERE q.instance_id=p_instance AND c.instance_id=p_instance AND q.completed_at IS NULL
            AND (q.claimed_until IS NULL OR q.claimed_until<=capture.read_at)
            AND (NOT r.enabled OR r.kind<>6 OR r.revision<>c.rule_revision OR c.target_revision<>v_revision
                OR c.read_at<=capture.read_at-interval '2 minutes' OR c.payload IS DISTINCT FROM capture.payload
                OR c.source_digest IS DISTINCT FROM capture.source_digest)
        ORDER BY q.observed_at,q.operation_id LIMIT p_limit FOR UPDATE OF q SKIP LOCKED
    ) UPDATE alerting.evaluation_queue q SET completed_at=capture.read_at,cancel_reason=stale.reason,
        claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=NULL
      FROM stale WHERE q.operation_id=stale.operation_id;
    FOR rule IN SELECT r.* FROM alerting.rule r WHERE r.instance_id=p_instance AND r.enabled AND r.kind=6
        AND NOT EXISTS(SELECT 1 FROM alerting.evaluation_queue q WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id AND q.completed_at IS NULL)
        AND NOT EXISTS(SELECT 1 FROM alerting.evaluation_queue q WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id
            AND q.rule_revision=r.revision AND q.completed_at IS NOT NULL AND q.cancel_reason IS NULL
            AND q.observations->0->>'SourceKind'='blocking_duration_v1' AND q.observations->0->>'SourceDigest'=capture.source_digest)
        AND coalesce((SELECT max(q.observed_at)+r.evaluation_interval FROM alerting.evaluation_queue q
            WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at)<=capture.read_at
        AND coalesce((SELECT s.last_observed_at+r.evaluation_interval FROM alerting.rule_state s
            WHERE s.instance_id=p_instance AND s.rule_id=r.rule_id),r.updated_at)<=capture.read_at
        ORDER BY coalesce((SELECT max(q.observed_at) FROM alerting.evaluation_queue q
            WHERE q.instance_id=p_instance AND q.rule_id=r.rule_id AND q.cancel_reason IS NULL),r.updated_at),r.rule_id LIMIT p_limit
    LOOP
        observation:=alerting.blocking_alert_observation(p_instance,v_revision,rule.rule_id,capture.read_at,capture.payload,capture.source_digest);
        operation:=(observation->>'OperationId')::uuid;
        INSERT INTO alerting.evaluation_queue(operation_id,instance_id,rule_id,observed_at,observations,due_at,next_due_at,
            evidence_digest,sample_id,run_id,rule_revision)
        VALUES(operation,p_instance,rule.rule_id,capture.read_at,jsonb_build_array(observation),capture.read_at,
            capture.read_at+rule.evaluation_interval,decode(observation->>'EvidenceDigest','hex'),observation->>'SampleId',
            (observation->>'RunId')::uuid,rule.revision);
        INSERT INTO alerting.blocking_evaluation_capture(operation_id,instance_id,rule_id,target_revision,rule_revision,read_at,payload,source_digest)
        VALUES(operation,p_instance,rule.rule_id,v_revision,rule.revision,capture.read_at,capture.payload,capture.source_digest);
        inserted:=inserted+1;
    END LOOP;
    WITH retired AS (
        SELECT c.operation_id FROM alerting.blocking_evaluation_capture c JOIN alerting.evaluation_queue q ON q.operation_id=c.operation_id
        WHERE c.instance_id=p_instance AND q.completed_at IS NOT NULL ORDER BY q.completed_at,c.operation_id LIMIT p_limit FOR UPDATE OF c SKIP LOCKED
    ) DELETE FROM alerting.blocking_evaluation_capture c USING retired WHERE c.operation_id=retired.operation_id;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing); RETURN inserted;
END $reconcile$;

CREATE FUNCTION alerting.assert_blocking_evaluation_claim(
    p_instance uuid,p_observation jsonb,p_work_key text,p_owner uuid,p_fencing bigint)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $assert$
DECLARE capture record; rule record; queued record; current_source record; expected jsonb;
BEGIN
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Blocking claim target scope is invalid' USING ERRCODE='42501'; END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile'
    THEN RAISE EXCEPTION 'Blocking claim coordinator is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance FOR UPDATE;
    SELECT c.* INTO capture FROM alerting.blocking_evaluation_capture c
        WHERE c.operation_id=(p_observation->>'OperationId')::uuid AND c.instance_id=p_instance;
    IF NOT FOUND THEN RAISE EXCEPTION 'Blocking claim capture is missing' USING ERRCODE='40001'; END IF;
    SELECT r.* INTO rule FROM alerting.rule r WHERE r.instance_id=p_instance AND r.rule_id=capture.rule_id FOR SHARE;
    SELECT q.* INTO queued FROM alerting.evaluation_queue q WHERE q.operation_id=capture.operation_id FOR UPDATE;
    IF NOT rule.enabled OR rule.kind IS DISTINCT FROM 6 OR rule.revision IS DISTINCT FROM capture.rule_revision
        OR queued.instance_id IS DISTINCT FROM p_instance OR queued.rule_id IS DISTINCT FROM capture.rule_id
        OR queued.rule_revision IS DISTINCT FROM capture.rule_revision OR queued.observed_at IS DISTINCT FROM capture.read_at
        OR queued.completed_at IS NOT NULL OR queued.claimed_until IS NULL OR queued.claimed_until<=clock_timestamp()
        OR queued.claim_work_key IS DISTINCT FROM p_work_key OR queued.claim_owner_execution_id IS DISTINCT FROM p_owner
        OR queued.claim_fencing IS DISTINCT FROM p_fencing
    THEN RAISE EXCEPTION 'Blocking evaluation is not a current claim' USING ERRCODE='40001'; END IF;
    SELECT * INTO STRICT current_source FROM alerting.read_blocking_alert_evidence(p_instance,capture.target_revision,p_owner,p_fencing);
    IF capture.read_at>current_source.read_at OR capture.read_at<=current_source.read_at-interval '2 minutes'
       OR capture.payload IS DISTINCT FROM current_source.payload OR capture.source_digest IS DISTINCT FROM current_source.source_digest
    THEN RAISE EXCEPTION 'Blocking captured source changed or expired' USING ERRCODE='40001'; END IF;
    expected:=alerting.blocking_alert_observation(p_instance,capture.target_revision,capture.rule_id,capture.read_at,capture.payload,capture.source_digest);
    IF NOT reporting.backup_cache_keys(p_observation,ARRAY['TargetId','RuleId','ObservedAtUtc','Value','CollectorHealthy',
           'Reason','SampleId','RunId','SourceKind','MetricId','SourceCollector','SourceVersion','SourceSchemaVersion','SourceDigest','OperationId','EvidenceDigest'])
       OR alerting.canonical_decision_snapshot(jsonb_build_object('Observation',p_observation))->'Observation'
          IS DISTINCT FROM alerting.canonical_decision_snapshot(jsonb_build_object('Observation',expected))->'Observation'
       OR queued.observations IS DISTINCT FROM jsonb_build_array(expected)
       OR queued.evidence_digest IS DISTINCT FROM decode(expected->>'EvidenceDigest','hex')
       OR queued.sample_id IS DISTINCT FROM expected->>'SampleId'
       OR queued.run_id IS DISTINCT FROM (expected->>'RunId')::uuid
    THEN RAISE EXCEPTION 'Blocking observation differs from captured source' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
END $assert$;

CREATE FUNCTION alerting.claim_blocking_alert_evaluations(
    p_instance uuid,p_work_key text,p_owner uuid,p_fencing bigint,p_limit integer)
RETURNS TABLE(operation_id uuid,observations jsonb,due_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $claim$
DECLARE claimed record;
BEGIN
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Blocking queue claim scope is invalid' USING ERRCODE='42501'; END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile' OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100
    THEN RAISE EXCEPTION 'Blocking queue claim coordinator or limit is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance FOR UPDATE;
    PERFORM r.rule_id FROM alerting.rule r WHERE r.instance_id=p_instance AND r.kind=6 ORDER BY r.rule_id FOR SHARE;
    FOR claimed IN WITH candidates AS MATERIALIZED (
        SELECT q.operation_id FROM alerting.evaluation_queue q JOIN alerting.blocking_evaluation_capture c ON c.operation_id=q.operation_id
        JOIN alerting.rule r ON r.instance_id=c.instance_id AND r.rule_id=c.rule_id
        WHERE q.instance_id=p_instance AND c.instance_id=p_instance AND r.enabled AND r.kind=6 AND r.revision=c.rule_revision
            AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<=clock_timestamp())
        ORDER BY q.observed_at,q.operation_id LIMIT p_limit FOR UPDATE OF q SKIP LOCKED
    ), leased AS (
        UPDATE alerting.evaluation_queue q SET claimed_until=clock_timestamp()+interval '30 seconds',
            claim_work_key=p_work_key,claim_owner_execution_id=p_owner,claim_fencing=p_fencing
        WHERE q.operation_id IN (SELECT c.operation_id FROM candidates c) RETURNING q.operation_id,q.observations,q.due_at
    ) SELECT l.* FROM leased l ORDER BY l.due_at,l.operation_id LOOP
        PERFORM alerting.assert_blocking_evaluation_claim(p_instance,claimed.observations->0,p_work_key,p_owner,p_fencing);
        operation_id:=claimed.operation_id; observations:=claimed.observations; due_at:=claimed.due_at; RETURN NEXT;
    END LOOP;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
END $claim$;
REVOKE ALL ON FUNCTION alerting.blocking_alert_observation(uuid,bigint,uuid,timestamptz,jsonb,text),
    alerting.reconcile_blocking_alert_evaluations(uuid,text,uuid,bigint,integer),
    alerting.assert_blocking_evaluation_claim(uuid,jsonb,text,uuid,bigint),
    alerting.claim_blocking_alert_evaluations(uuid,text,uuid,bigint,integer)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
