-- Private atomic deadlock commit; runtime/catalog activation remains separate.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.commit_deadlock_alert_evaluation(p_instance uuid,p_observation jsonb,p_decision jsonb,
    p_work_key text,p_owner uuid,p_fencing bigint)
RETURNS TABLE(evaluated_count integer,changed_count integer,suppressed_count integer,completed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $commit$
DECLARE v_rule alerting.rule%ROWTYPE; v_replay alerting.evaluation_replay%ROWTYPE;
    v_rule_id uuid; v_operation uuid; v_observed timestamptz; v_digest bytea; v_result_digest bytea;
    v_prior jsonb; v_expected jsonb; v_state jsonb; v_candidate jsonb; v_suppressed boolean; v_event smallint;
    v_now timestamptz; v_completed integer; v_source text; v_target_revision bigint; v_lifecycle text;
BEGIN
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Deadlock alert commit scope is invalid' USING ERRCODE='42501'; END IF;
    IF p_work_key IS DISTINCT FROM 'alerts/evaluation/reconcile'
    THEN RAISE EXCEPTION 'Deadlock alert commit coordinator is invalid' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    IF NOT reporting.backup_cache_keys(p_decision,ARRAY['Observation','State','Event','DeliverySuppressed','Reason'])
       OR NOT reporting.backup_cache_keys(p_observation,ARRAY['TargetId','RuleId','ObservedAtUtc','Value','CollectorHealthy',
        'Reason','OperationId','EvidenceDigest','SampleId','RunId','SourceKind','MetricId','SourceCollector','SourceVersion','SourceSchemaVersion','SourceDigest'])
       OR NOT reporting.backup_cache_keys(p_decision->'State',ARRAY['RuleId','TargetId','State','ConsecutiveMatches','ConsecutiveClears',
        'FirstMatchUtc','LastObservedUtc','FiredUtc','AcknowledgedUtc','AlertId','EpisodeId','LastOperationId','EvidenceDigest',
        'Reason','LastReason','DeliverySuppressed','ResolvedUtc','EpisodeStartedUtc','AcknowledgedBy','Revision','LastValue'])
       OR p_decision->'Observation' IS DISTINCT FROM p_observation
       OR (p_observation->>'TargetId')::uuid IS DISTINCT FROM p_instance
       OR coalesce(p_observation->>'ObservedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+]00:00)$'
    THEN RAISE EXCEPTION 'Deadlock alert decision shape is invalid' USING ERRCODE='22023'; END IF;
    v_rule_id:=(p_observation->>'RuleId')::uuid; v_operation:=(p_observation->>'OperationId')::uuid;
    v_observed:=(p_observation->>'ObservedAtUtc')::timestamptz;
    v_digest:=decode(p_observation->>'EvidenceDigest','hex');
    IF v_operation IS NULL OR octet_length(v_digest) IS DISTINCT FROM 32
       OR v_operation IS DISTINCT FROM alerting.canonical_operation_id(p_instance,v_rule_id,v_observed,p_observation->>'Reason')
       OR v_digest IS DISTINCT FROM alerting.canonical_evidence_sha256(p_instance,v_rule_id,p_observation->>'SourceKind',
            p_observation->>'MetricId',p_observation->>'SourceCollector',p_observation->>'SourceVersion',
            (p_observation->>'SourceSchemaVersion')::integer,p_observation->>'SourceDigest',v_observed,
            p_observation->>'SampleId',(p_observation->>'RunId')::uuid,(p_observation->>'Value')::double precision,
            (p_observation->>'CollectorHealthy')::boolean,p_observation->>'Reason')
    THEN RAISE EXCEPTION 'Deadlock alert canonical evidence is invalid' USING ERRCODE='22023'; END IF;
    -- Use the same advisory fence as maintenance/destination administration, then
    -- target-before-rule/queue/state order. Replays retain policy/target bindings.
    PERFORM pg_advisory_xact_lock(hashtextextended(p_instance::text,0));
    SELECT target.revision,target.lifecycle_state INTO v_target_revision,v_lifecycle
        FROM control.observation_target target WHERE target.instance_id=p_instance FOR UPDATE;
    SELECT r.* INTO v_rule FROM alerting.rule r WHERE r.instance_id=p_instance AND r.rule_id=v_rule_id FOR SHARE;
    IF NOT FOUND OR v_rule.kind<>7
    THEN RAISE EXCEPTION 'Deadlock alert rule is invalid' USING ERRCODE='22023'; END IF;
    v_source:='deadlock_count_v1';
    IF p_observation->>'SourceKind' IS DISTINCT FROM v_source
    THEN RAISE EXCEPTION 'Deadlock alert rule/source binding is invalid' USING ERRCODE='22023'; END IF;
    v_candidate:=alerting.canonical_decision_snapshot(p_decision);
    SELECT replay.* INTO v_replay FROM alerting.evaluation_replay replay WHERE replay.operation_id=v_operation;
    IF FOUND THEN
        IF v_replay.instance_id IS DISTINCT FROM p_instance OR v_replay.rule_id IS DISTINCT FROM v_rule_id
           OR v_replay.target_binding IS DISTINCT FROM p_instance OR v_replay.rule_revision IS DISTINCT FROM v_rule.revision
           OR v_replay.rule_binding IS DISTINCT FROM to_jsonb(v_rule) OR v_replay.evidence_digest IS DISTINCT FROM v_digest
           OR v_replay.result_digest IS DISTINCT FROM sha256(convert_to(v_replay.result::text,'UTF8'))
           OR v_replay.result IS DISTINCT FROM v_candidate OR v_lifecycle IS DISTINCT FROM 'active'
           OR v_target_revision IS DISTINCT FROM (substring(split_part(p_observation->>'Reason','|',2) FROM 8))::bigint
        THEN RAISE EXCEPTION 'Divergent deadlock alert replay' USING ERRCODE='40001'; END IF;
        -- No source/capture lookup and no writes after an exact immutable replay.
        RETURN QUERY SELECT 0,0,0,clock_timestamp(); RETURN;
    END IF;
    PERFORM alerting.assert_deadlock_evaluation_claim(p_instance,p_observation,p_work_key,p_owner,p_fencing);
    IF EXISTS(SELECT 1 FROM alerting.evaluation_queue q WHERE q.instance_id=p_instance AND q.rule_id=v_rule_id
        AND q.rule_revision=v_rule.revision AND q.completed_at IS NOT NULL AND q.cancel_reason IS NULL
        AND q.observations->0->>'SourceKind'='deadlock_count_v1'
        AND q.observations->0->>'SourceDigest'=p_observation->>'SourceDigest' AND q.operation_id<>v_operation)
    THEN RAISE EXCEPTION 'Deadlock snapshot was already consumed' USING ERRCODE='40001'; END IF;
    PERFORM 1 FROM alerting.rule_state s WHERE s.instance_id=p_instance AND s.rule_id=v_rule_id FOR UPDATE;
    v_prior:=alerting.backup_alert_state_snapshot(p_instance,v_rule_id);
    v_suppressed:=alerting.maintenance_active_unscoped(p_instance,v_observed);
    v_expected:=alerting.deadlock_alert_transition(v_rule.comparison,v_rule.threshold,v_rule.hysteresis,v_rule.confirmation_count,v_rule.clear_confirmation_count,
        v_rule.confirmation_window,v_prior,p_observation,v_suppressed);
    IF v_candidate IS DISTINCT FROM v_expected
    THEN RAISE EXCEPTION 'Deadlock alert decision differs from repository transition' USING ERRCODE='22023'; END IF;
    v_state:=v_expected->'State'; v_event:=(v_expected->>'Event')::smallint;
    INSERT INTO alerting.rule_state(instance_id,rule_id,state,consecutive_matches,consecutive_clears,first_match_at,
        last_observed_at,fired_at,acknowledged_at,alert_id,alert_episode_id,last_operation_id,evidence_digest,reason,last_reason,
        delivery_suppressed,resolved_at,episode_started_at,acknowledged_by,revision,last_value)
    VALUES(p_instance,v_rule_id,(v_state->>'State')::smallint,(v_state->>'ConsecutiveMatches')::integer,
        (v_state->>'ConsecutiveClears')::integer,(v_state->>'FirstMatchUtc')::timestamptz,(v_state->>'LastObservedUtc')::timestamptz,
        (v_state->>'FiredUtc')::timestamptz,(v_state->>'AcknowledgedUtc')::timestamptz,(v_state->>'AlertId')::uuid,
        (v_state->>'EpisodeId')::uuid,v_operation,v_digest,v_state->>'Reason',v_state->>'LastReason',v_suppressed,
        (v_state->>'ResolvedUtc')::timestamptz,(v_state->>'EpisodeStartedUtc')::timestamptz,v_state->>'AcknowledgedBy',
        (v_state->>'Revision')::bigint,(v_state->>'LastValue')::double precision)
    ON CONFLICT(instance_id,rule_id) DO UPDATE SET state=EXCLUDED.state,consecutive_matches=EXCLUDED.consecutive_matches,
        consecutive_clears=EXCLUDED.consecutive_clears,first_match_at=EXCLUDED.first_match_at,last_observed_at=EXCLUDED.last_observed_at,
        fired_at=EXCLUDED.fired_at,acknowledged_at=EXCLUDED.acknowledged_at,alert_id=EXCLUDED.alert_id,alert_episode_id=EXCLUDED.alert_episode_id,
        last_operation_id=EXCLUDED.last_operation_id,evidence_digest=EXCLUDED.evidence_digest,reason=EXCLUDED.reason,last_reason=EXCLUDED.last_reason,
        delivery_suppressed=EXCLUDED.delivery_suppressed,resolved_at=EXCLUDED.resolved_at,episode_started_at=EXCLUDED.episode_started_at,
        acknowledged_by=EXCLUDED.acknowledged_by,revision=EXCLUDED.revision,last_value=EXCLUDED.last_value;
    v_result_digest:=sha256(convert_to(v_expected::text,'UTF8'));
    INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,
        delivery_suppressed,operation_id,evidence_digest,result_digest)
    VALUES(p_instance,v_rule_id,(v_state->>'AlertId')::uuid,(v_prior->>'State')::smallint,(v_state->>'State')::smallint,
        v_observed,v_state->>'Reason',v_suppressed,v_operation,v_digest,v_result_digest);
    INSERT INTO alerting.evaluation_replay(operation_id,instance_id,rule_id,rule_revision,target_binding,rule_binding,evidence_digest,result_digest,result)
    VALUES(v_operation,p_instance,v_rule_id,v_rule.revision,p_instance,to_jsonb(v_rule),v_digest,v_result_digest,v_expected);
    v_now:=clock_timestamp();
    IF v_event IS NOT NULL THEN
        INSERT INTO alerting.delivery_outbox(delivery_id,alert_id,instance_id,rule_id,destination_id,event_kind,payload,due_at,
            operation_id,evidence_digest,destination_revision,destination_kind,destination_configuration_reference,
            destination_approval_revision,destination_approval_digest,destination_approval_scope,destination_configuration_digest)
        SELECT gen_random_uuid(),(v_state->>'AlertId')::uuid,p_instance,v_rule_id,d.destination_id,v_event,
            jsonb_build_object('schemaVersion',1,'operationId',v_operation,'evidenceDigest',encode(v_digest,'hex'),
                'alertId',(v_state->>'AlertId')::uuid,'ruleId',v_rule_id,'targetId',p_instance,'event',v_event,
                'reasonCode',v_expected->>'Reason','suppressed',v_suppressed),
            CASE WHEN v_suppressed THEN coalesce((SELECT max(m.ends_at) FROM alerting.maintenance_window m
                WHERE m.instance_id=p_instance AND m.cancelled_at IS NULL AND v_observed>=m.starts_at AND v_observed<m.ends_at),v_now) ELSE v_now END,
            v_operation,v_digest,d.revision,d.kind,d.configuration_reference,d.approved_revision,d.approval_digest,d.approval_scope,d.configuration_digest
        FROM alerting.destination d WHERE d.instance_id=p_instance AND d.enabled AND d.approved AND d.approval_scope=p_instance
            AND d.approved_kind=d.kind AND d.approved_configuration_reference=d.configuration_reference
            AND d.approved_revision IS NOT NULL AND d.approval_digest IS NOT NULL
        ON CONFLICT(alert_id,destination_id,event_kind) DO NOTHING;
    END IF;
    INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
        subject_kind,subject_identifier,correlation_id,safe_details)
    VALUES(v_now,gen_random_uuid(),'service','collector','alerting.evaluate','allowed','succeeded','alert_evaluation',
        p_fencing::text,gen_random_uuid(),jsonb_build_object('evaluated_count',1,'changed_count',
            CASE WHEN v_prior->'State' IS DISTINCT FROM v_state->'State' THEN 1 ELSE 0 END,'source_kind','deadlock_count_v1'));
    -- Recheck source expiry and claim ownership after state/outbox/audit work,
    -- immediately before consuming the operation. Any failure rolls it all back.
    PERFORM alerting.assert_deadlock_evaluation_claim(p_instance,p_observation,p_work_key,p_owner,p_fencing);
    UPDATE alerting.evaluation_queue q SET completed_at=v_now,claimed_until=NULL,claim_work_key=NULL,
        claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=v_now+v_rule.evaluation_interval,due_at=v_now+v_rule.evaluation_interval
    WHERE q.operation_id=v_operation AND q.instance_id=p_instance AND q.rule_id=v_rule_id AND q.completed_at IS NULL
        AND q.claimed_until>clock_timestamp() AND q.claim_work_key=p_work_key AND q.claim_owner_execution_id=p_owner
        AND q.claim_fencing=p_fencing AND q.evidence_digest=v_digest;
    GET DIAGNOSTICS v_completed=ROW_COUNT;
    IF v_completed<>1 THEN RAISE EXCEPTION 'Deadlock alert claim expired before commit' USING ERRCODE='40001'; END IF;
    DELETE FROM alerting.deadlock_evaluation_capture c WHERE c.operation_id=v_operation;
    PERFORM control.assert_worker_lease(p_work_key,p_owner,p_fencing);
    RETURN QUERY SELECT 1,CASE WHEN v_prior->'State' IS DISTINCT FROM v_state->'State' THEN 1 ELSE 0 END,
        CASE WHEN v_suppressed THEN 1 ELSE 0 END,v_now;
END $commit$;

REVOKE ALL ON FUNCTION alerting.commit_deadlock_alert_evaluation(uuid,jsonb,jsonb,text,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
