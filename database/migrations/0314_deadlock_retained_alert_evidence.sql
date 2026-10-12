-- Retained-window evidence and explicit v2 provenance, without collector activation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.read_deadlock_alert_evidence_v1(
    p_instance uuid,p_revision bigint,p_owner uuid,p_fencing bigint)
RETURNS TABLE(read_at timestamptz,payload jsonb,source_digest text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $read$
DECLARE v_now timestamptz; v_health record; v_outcome record; v_scan record; v_rows record;
    v_reason text:='source_unavailable'; v_until timestamptz; v_ttl interval; v_body jsonb;
    v_run uuid; v_schedule bigint; v_completed timestamptz; v_start timestamptz;
    v_count integer; v_retained integer; v_completion text;
BEGIN
    IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'::uuid
       OR p_revision IS NULL OR p_revision<=0
       OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Deadlock alert target scope is invalid' USING ERRCODE='42501'; END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance
        AND t.revision=p_revision AND t.lifecycle_state='active' FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock alert current target revision is required' USING ERRCODE='42501'; END IF;
    PERFORM 1 FROM control.collector_schedule s WHERE s.instance_id=p_instance
        AND s.collector_id='deadlocks.system-health' FOR SHARE;
    v_now:=clock_timestamp();
    SELECT h.* INTO v_health FROM reporting.collector_health_projection h
        WHERE h.instance_id=p_instance AND h.collector_id='deadlocks.system-health';
    IF FOUND THEN
        v_run:=v_health.run_id; v_schedule:=v_health.schedule_revision; v_completed:=v_health.completed_at;
        v_ttl:=least(v_health.collection_interval*2,interval '2 minutes');
        IF NOT v_health.enabled THEN v_reason:='collector_disabled';
        ELSIF v_health.collector_version<>1 OR v_health.output_schema_version<>1
            OR v_health.schedule_target_revision<>p_revision OR v_health.target_revision IS DISTINCT FROM p_revision
            OR v_run IS NULL OR v_completed IS NULL OR v_completed>v_now
            OR v_health.health_state NOT IN ('current','stale','degraded') THEN v_reason:='source_unavailable';
        ELSIF v_completed+v_ttl<=v_now THEN v_reason:='source_stale';
        ELSE
            SELECT o.* INTO v_outcome FROM telemetry.collection_run_outcome o WHERE o.run_id=v_run;
            IF NOT FOUND OR v_outcome.completion_digest IS NULL THEN v_reason:='source_unavailable';
            ELSE
                v_completion:=encode(v_outcome.completion_digest,'hex');
                IF v_outcome.outcome<>'succeeded' OR v_outcome.truncated OR v_outcome.loss_detected
                    OR v_outcome.loss_kind<>'none' OR NOT v_outcome.loss_count_exact
                    OR v_outcome.lost_row_count<>0 OR v_outcome.lost_byte_count<>0
                    OR v_outcome.rejected_item_count<>0 THEN v_reason:='source_incomplete';
                ELSE
                    SELECT s.* INTO v_scan FROM alerting.deadlock_source_snapshot s WHERE s.instance_id=p_instance;
                    IF NOT FOUND OR v_scan.run_id<>v_run OR v_scan.target_revision<>p_revision
                        OR v_scan.schedule_revision<>v_schedule OR v_scan.completed_at<>v_completed
                        OR v_scan.completion_digest<>v_outcome.completion_digest THEN v_reason:='source_unavailable';
                    ELSE
                        v_retained:=cardinality(v_scan.event_ids); v_start:=v_completed-interval '5 minutes';
                        SELECT count(DISTINCT e.id)::integer AS n,
                            count(*) FILTER(WHERE e.at>=v_start AND e.at<=v_completed)::integer AS recent,
                            bool_and(NOT e.truncated AND isfinite(e.at) AND e.at<=v_completed
                                AND e.at>=v_completed-interval '33 days') AS valid
                        INTO v_rows FROM unnest(v_scan.event_ids,v_scan.occurred_ats,v_scan.parse_truncated) e(id,at,truncated);
                        IF v_retained<>v_outcome.output_item_count OR v_rows.n<>v_retained
                            OR v_outcome.inserted_item_count+v_outcome.duplicate_item_count<>v_retained
                            OR (v_retained>0 AND v_outcome.source_row_count<>v_retained)
                            OR (v_retained=0 AND v_outcome.source_row_count NOT IN (0,1))
                            OR (v_retained>0 AND v_rows.valid IS NOT TRUE) THEN v_reason:='source_inconsistent';
                        ELSE
                            v_reason:='current'; v_count:=v_rows.recent;
                            v_until:=v_completed+v_ttl;
                        END IF;
                    END IF;
                END IF;
            END IF;
        END IF;
    END IF;
    v_body:=jsonb_build_object('schemaVersion',1,'targetId',p_instance,'targetRevision',p_revision,
        'validUntilUtc',v_until,'scheduleRevision',v_schedule,'runId',v_run,'completedAtUtc',v_completed,
        'windowFromUtc',CASE WHEN v_reason='current' THEN v_start ELSE NULL END,
        'windowToUtc',CASE WHEN v_reason='current' THEN v_completed ELSE NULL END,
        'retainedEvents',v_retained,'deadlockCount',v_count,'reason',v_reason,'completionDigest',v_completion);
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    RETURN QUERY SELECT v_now,v_body,encode(sha256(convert_to(v_body::text,'UTF8')),'hex');
END $read$;
REVOKE ALL ON FUNCTION alerting.read_deadlock_alert_evidence_v1(uuid,bigint,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION alerting.read_deadlock_alert_evidence_v2(
    p_instance uuid,p_revision bigint,p_owner uuid,p_fencing bigint)
RETURNS TABLE(read_at timestamptz,payload jsonb,source_digest text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $read$
DECLARE v_now timestamptz; v_health record; v_run record; v_outcome record; v_cache record; v_receipt record; v_scan record;
    v_reason text:='source_unavailable'; v_until timestamptz; v_ttl interval; v_body jsonb;
    v_run_id uuid; v_schedule bigint; v_completed timestamptz; v_start timestamptz;
    v_count integer; v_retained integer; v_completion text; v_checkpoint text;
BEGIN
    IF p_instance IS NULL OR p_revision IS NULL OR p_revision<=0
       OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Deadlock alert target scope is invalid' USING ERRCODE='42501'; END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance
        AND t.revision=p_revision AND t.lifecycle_state='active' FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock alert current target revision is required' USING ERRCODE='42501'; END IF;
    -- Canonical commit updates the schedule before publishing its checkpoint.
    -- This shared schedule lock keeps every dependent read on the same commit.
    PERFORM 1 FROM control.collector_schedule s WHERE s.instance_id=p_instance
        AND s.collector_id='deadlocks.system-health' FOR SHARE;
    v_now:=clock_timestamp();
    SELECT h.* INTO v_health FROM reporting.collector_health_projection h
        WHERE h.instance_id=p_instance AND h.collector_id='deadlocks.system-health';
    IF FOUND THEN
        v_run_id:=v_health.run_id; v_schedule:=v_health.schedule_revision; v_completed:=v_health.completed_at;
        v_ttl:=least(v_health.collection_interval*2,interval '2 minutes');
        IF NOT v_health.enabled THEN v_reason:='collector_disabled';
        ELSIF v_health.collector_version<>2 OR v_health.output_schema_version<>2
            OR v_health.schedule_target_revision<>p_revision OR v_health.target_revision IS DISTINCT FROM p_revision
            OR v_run_id IS NULL OR v_completed IS NULL OR v_completed>v_now
            OR v_health.health_state NOT IN ('current','stale','degraded') THEN v_reason:='source_unavailable';
        ELSIF v_completed+v_ttl<=v_now THEN v_reason:='source_stale';
        ELSE
            SELECT r.* INTO v_run FROM telemetry.lookup_collection_run_runtime(v_run_id) r;
            SELECT o.* INTO v_outcome FROM telemetry.collection_run_outcome o WHERE o.run_id=v_run_id;
            SELECT c.* INTO v_cache FROM control.deadlock_read_checkpoint c WHERE c.instance_id=p_instance;
            SELECT r.* INTO v_receipt FROM events.deadlock_read_receipt r WHERE r.completed_at=v_completed AND r.run_id=v_run_id;
            SELECT s.* INTO v_scan FROM alerting.deadlock_source_snapshot s WHERE s.instance_id=p_instance;
            v_completion:=encode(v_outcome.completion_digest,'hex'); v_checkpoint:=encode(v_cache.checkpoint_digest,'hex');
            IF v_run.instance_id IS DISTINCT FROM p_instance OR v_run.target_revision IS DISTINCT FROM p_revision
                OR v_run.collector_id IS DISTINCT FROM 'deadlocks.system-health' OR v_run.collector_version IS DISTINCT FROM 2
                OR v_run.output_schema_version IS DISTINCT FROM 2 OR v_run.schedule_revision IS DISTINCT FROM v_schedule
                OR octet_length(v_run.request_digest) IS DISTINCT FROM 32
                OR v_outcome.run_id IS DISTINCT FROM v_run_id OR v_outcome.completed_at IS DISTINCT FROM v_completed
                OR octet_length(v_outcome.completion_digest) IS DISTINCT FROM 32
                OR v_cache.run_id IS DISTINCT FROM v_run_id OR v_cache.target_revision IS DISTINCT FROM p_revision
                OR v_cache.schedule_revision IS DISTINCT FROM v_schedule OR v_cache.completed_at IS DISTINCT FROM v_completed
                OR v_cache.completion_digest IS DISTINCT FROM v_outcome.completion_digest
                OR v_receipt.run_id IS DISTINCT FROM v_run_id OR v_receipt.instance_id IS DISTINCT FROM p_instance
                OR v_receipt.completion_digest IS DISTINCT FROM v_outcome.completion_digest
                OR v_receipt.checkpoint_digest IS DISTINCT FROM v_cache.checkpoint_digest
                OR v_scan.run_id IS DISTINCT FROM v_run_id OR v_scan.target_revision IS DISTINCT FROM p_revision
                OR v_scan.schedule_revision IS DISTINCT FROM v_schedule OR v_scan.completed_at IS DISTINCT FROM v_completed
                OR v_scan.completion_digest IS DISTINCT FROM v_outcome.completion_digest
            THEN v_reason:='source_inconsistent';
            ELSE
                BEGIN
                    IF v_cache.window_state IS NOT NULL THEN
                        PERFORM control.validate_deadlock_read_window(v_cache.window_state);
                        v_retained:=jsonb_array_length(v_cache.window_state->'events');
                    END IF;
                    IF v_cache.source_state<>'ready' OR v_cache.window_state IS NULL
                        OR v_cache.window_state->>'reason'<>'current'
                        OR v_outcome.outcome<>'succeeded' OR v_outcome.truncated OR v_outcome.loss_detected
                        OR v_outcome.loss_kind<>'none' OR NOT v_outcome.loss_count_exact
                        OR v_outcome.lost_row_count<>0 OR v_outcome.lost_byte_count<>0 OR v_outcome.rejected_item_count<>0
                    THEN v_reason:='source_incomplete';
                    ELSE
                        v_start:=v_completed-interval '5 minutes';
                        IF (v_cache.window_state->>'targetId')::uuid IS DISTINCT FROM p_instance
                            OR (v_cache.window_state->>'targetRevision')::bigint IS DISTINCT FROM p_revision
                            OR v_cache.window_state->>'sourceEpoch' IS DISTINCT FROM encode(v_cache.source_epoch,'hex')
                            OR (v_cache.window_state->>'readAtUtc')::timestamptz IS DISTINCT FROM v_completed
                            OR cardinality(v_scan.event_ids) IS DISTINCT FROM v_outcome.output_item_count
                            OR v_outcome.inserted_item_count+v_outcome.duplicate_item_count<>v_outcome.output_item_count
                            OR v_outcome.source_row_count NOT BETWEEN 0 AND 8193
                            OR v_outcome.response_bytes<128+64*v_outcome.source_row_count OR v_outcome.response_bytes>1048576
                            OR EXISTS(SELECT 1 FROM jsonb_array_elements(v_cache.window_state->'events') e
                                WHERE NOT EXISTS(SELECT 1 FROM events.deadlock_summary d WHERE d.instance_id=p_instance
                                    AND d.event_id=(e->>'eventId')::uuid AND d.occurred_at=(e->>'occurredAtUtc')::timestamptz
                                    AND d.parse_truncated=(e->>'parseTruncated')::boolean))
                            OR (SELECT count(DISTINCT e.id) FROM unnest(v_scan.event_ids) e(id))<>cardinality(v_scan.event_ids)
                            OR EXISTS(SELECT 1 FROM unnest(v_scan.event_ids,v_scan.occurred_ats,v_scan.parse_truncated) e(id,at,truncated)
                                WHERE e.id IS NULL OR e.at IS NULL OR e.truncated IS DISTINCT FROM false OR NOT isfinite(e.at)
                                    OR e.at>v_completed OR e.at<v_completed-interval '33 days'
                                    OR (e.at>=v_start AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_cache.window_state->'events') retained
                                        WHERE (retained->>'eventId')::uuid=e.id AND (retained->>'occurredAtUtc')::timestamptz=e.at
                                            AND (retained->>'parseTruncated')::boolean=e.truncated)))
                        THEN v_reason:='source_inconsistent';
                        ELSE v_reason:='current'; v_count:=v_retained; v_until:=v_completed+v_ttl; END IF;
                    END IF;
                EXCEPTION WHEN invalid_parameter_value OR invalid_text_representation OR datetime_field_overflow THEN
                    v_reason:='source_inconsistent'; v_count:=NULL; v_retained:=NULL; v_until:=NULL;
                END;
            END IF;
        END IF;
    END IF;
    v_body:=jsonb_build_object('schemaVersion',2,'targetId',p_instance,'targetRevision',p_revision,
        'validUntilUtc',v_until,'scheduleRevision',v_schedule,'runId',v_run_id,'completedAtUtc',v_completed,
        'windowFromUtc',CASE WHEN v_reason='current' THEN v_start ELSE NULL END,
        'windowToUtc',CASE WHEN v_reason='current' THEN v_completed ELSE NULL END,
        'retainedEvents',v_retained,'deadlockCount',v_count,'reason',v_reason,'completionDigest',v_completion,
        'collectorVersion',2,'outputSchemaVersion',2,'checkpointDigest',v_checkpoint);
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    RETURN QUERY SELECT v_now,v_body,encode(sha256(convert_to(v_body::text,'UTF8')),'hex');
END $read$;
REVOKE ALL ON FUNCTION alerting.read_deadlock_alert_evidence_v2(uuid,bigint,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION alerting.read_deadlock_alert_evidence(
    p_instance uuid,p_revision bigint,p_owner uuid,p_fencing bigint)
RETURNS TABLE(read_at timestamptz,payload jsonb,source_digest text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $read$
DECLARE v_version integer;
BEGIN
    IF p_instance IS NULL OR p_revision IS NULL OR p_revision<=0
        OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Deadlock alert target scope is invalid' USING ERRCODE='42501'; END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance AND t.revision=p_revision AND t.lifecycle_state='active' FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock alert current target revision is required' USING ERRCODE='42501'; END IF;
    SELECT s.collector_version INTO v_version FROM control.collector_schedule s
        WHERE s.instance_id=p_instance AND s.collector_id='deadlocks.system-health' FOR SHARE;
    IF v_version=2 THEN RETURN QUERY SELECT * FROM alerting.read_deadlock_alert_evidence_v2(p_instance,p_revision,p_owner,p_fencing);
    ELSE RETURN QUERY SELECT * FROM alerting.read_deadlock_alert_evidence_v1(p_instance,p_revision,p_owner,p_fencing); END IF;
END $read$;
REVOKE ALL ON FUNCTION alerting.read_deadlock_alert_evidence(uuid,bigint,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.read_deadlock_alert_evidence(uuid,bigint,uuid,bigint) TO sqlobserver_collector;

CREATE OR REPLACE FUNCTION alerting.deadlock_alert_observation(
    p_instance uuid,p_revision bigint,p_rule uuid,p_read_at timestamptz,p_payload jsonb,p_digest text)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $observation$
DECLARE reason text; operation uuid; run uuid; value double precision; completed timestamptz; v_schema integer;
BEGIN
    v_schema:=CASE WHEN p_payload->'schemaVersion'='2'::jsonb THEN 2 ELSE 1 END;
    IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'::uuid
       OR p_rule IS NULL OR p_rule='00000000-0000-0000-0000-000000000000'::uuid
       OR p_revision IS NULL OR p_revision<=0 OR p_read_at IS NULL OR NOT isfinite(p_read_at)
       OR p_payload IS NULL OR octet_length(p_payload::text)>2048
       OR ((p_payload->'schemaVersion'='1'::jsonb AND reporting.backup_cache_keys(p_payload,
            ARRAY['schemaVersion','targetId','targetRevision','validUntilUtc','scheduleRevision','runId','completedAtUtc',
                'windowFromUtc','windowToUtc','retainedEvents','deadlockCount','reason','completionDigest']))
           OR (p_payload->'schemaVersion'='2'::jsonb AND reporting.backup_cache_keys(p_payload,
            ARRAY['schemaVersion','targetId','targetRevision','validUntilUtc','scheduleRevision','runId','completedAtUtc',
                'windowFromUtc','windowToUtc','retainedEvents','deadlockCount','reason','completionDigest',
                'collectorVersion','outputSchemaVersion','checkpointDigest'])
            AND p_payload->'collectorVersion'='2'::jsonb AND p_payload->'outputSchemaVersion'='2'::jsonb
            AND (p_payload->'checkpointDigest'='null'::jsonb OR
                (jsonb_typeof(p_payload->'checkpointDigest')='string' AND p_payload->>'checkpointDigest' ~ '^[0-9a-f]{64}$'))
            AND (p_payload->>'reason'<>'current' OR coalesce(p_payload->>'checkpointDigest','') ~ '^[0-9a-f]{64}$'))) IS DISTINCT FROM true
       OR (p_payload->>'targetId')::uuid IS DISTINCT FROM p_instance
       OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_revision
       OR p_digest IS DISTINCT FROM encode(sha256(convert_to(p_payload::text,'UTF8')),'hex')
       OR coalesce(p_payload->>'reason','') NOT IN ('current','collector_disabled','source_unavailable',
           'source_stale','source_incomplete','source_inconsistent')
    THEN RAISE EXCEPTION 'Deadlock alert capture envelope is invalid' USING ERRCODE='22023'; END IF;
    IF p_payload->>'reason'='current' THEN
        completed:=(p_payload->>'completedAtUtc')::timestamptz;
        IF (p_payload->>'validUntilUtc')::timestamptz IS NULL
           OR NOT isfinite((p_payload->>'validUntilUtc')::timestamptz)
           OR (p_payload->>'validUntilUtc')::timestamptz<=p_read_at
           OR (p_payload->>'validUntilUtc')::timestamptz>p_read_at+interval '2 minutes'
           OR (p_payload->>'runId')::uuid IS NULL OR (p_payload->>'runId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
           OR coalesce(p_payload->>'scheduleRevision','') !~ '^[1-9][0-9]*$'
           OR completed IS NULL OR NOT isfinite(completed) OR completed>p_read_at
           OR (p_payload->>'windowToUtc')::timestamptz IS DISTINCT FROM completed
           OR (p_payload->>'windowFromUtc')::timestamptz IS DISTINCT FROM completed-interval '5 minutes'
           OR coalesce(p_payload->>'completionDigest','') !~ '^[0-9a-f]{64}$'
           OR coalesce(p_payload->'retainedEvents','null'::jsonb)::text !~ '^(0|[1-9][0-9]{0,2})$'
           OR (p_payload->>'retainedEvents')::integer>256
           OR coalesce(p_payload->'deadlockCount','null'::jsonb)::text !~ '^(0|[1-9][0-9]{0,2})$'
           OR (p_payload->>'deadlockCount')::integer>(p_payload->>'retainedEvents')::integer
        THEN RAISE EXCEPTION 'Deadlock alert current count is invalid' USING ERRCODE='22023'; END IF;
        value:=(p_payload->>'deadlockCount')::double precision;
    ELSIF p_payload->'deadlockCount' IS DISTINCT FROM 'null'::jsonb
       OR p_payload->'validUntilUtc' IS DISTINCT FROM 'null'::jsonb
       OR p_payload->'windowFromUtc' IS DISTINCT FROM 'null'::jsonb
       OR p_payload->'windowToUtc' IS DISTINCT FROM 'null'::jsonb
    THEN RAISE EXCEPTION 'Unknown deadlock evidence cannot have a count' USING ERRCODE='22023'; END IF;
    run:=(p_payload->>'runId')::uuid;
    reason:='deadlock_count_v1|target='||p_revision::text||'|reason='||(p_payload->>'reason')||'|digest='||p_digest;
    operation:=alerting.canonical_operation_id(p_instance,p_rule,p_read_at,reason);
    RETURN jsonb_build_object('TargetId',p_instance,'RuleId',p_rule,'ObservedAtUtc',p_read_at,
        'Value',value,'CollectorHealthy',NULL,'Reason',reason,'SampleId',operation::text,'RunId',run,
        'SourceKind','deadlock_count_v1','MetricId',NULL,'SourceCollector','deadlocks.system-health','SourceVersion',v_schema::text,
        'SourceSchemaVersion',v_schema,'SourceDigest',p_digest,'OperationId',operation,
        'EvidenceDigest',encode(alerting.canonical_evidence_sha256(p_instance,p_rule,'deadlock_count_v1',NULL,
            'deadlocks.system-health',v_schema::text,v_schema,p_digest,p_read_at,operation::text,run,value,NULL,reason),'hex'));
END $observation$;

REVOKE ALL ON FUNCTION alerting.deadlock_alert_observation(uuid,bigint,uuid,timestamptz,jsonb,text) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION alerting.deadlock_alert_transition(p_comparison smallint,p_threshold double precision,p_hysteresis double precision,
    p_confirm integer,p_clear integer,p_window interval,p_prior jsonb,p_observation jsonb,p_suppressed boolean)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $transition$
DECLARE v_previous integer; v_next integer; v_event integer; v_count integer; v_clears integer;
    v_value double precision; v_observed timestamptz; v_first timestamptz; v_last timestamptz;
    v_active boolean; v_matches boolean; v_partial boolean; v_fresh boolean;
    v_alert uuid; v_episode uuid; v_operation uuid; v_target uuid; v_rule uuid; v_expected_digest text; v_state jsonb;
BEGIN
    IF p_comparison IS NULL OR p_comparison NOT IN (1,2) OR p_threshold IS NULL OR (p_threshold NOT BETWEEN 0 AND 256 OR p_threshold<>trunc(p_threshold::numeric))
       OR p_hysteresis IS NULL OR (p_hysteresis NOT BETWEEN 0 AND p_threshold OR p_hysteresis<>trunc(p_hysteresis::numeric))
       OR p_confirm IS NULL OR p_confirm NOT BETWEEN 1 AND 100 OR p_clear IS NULL OR p_clear NOT BETWEEN 1 AND 100
       OR p_window IS NULL OR p_window<interval '0 seconds' OR p_window>interval '7 days' OR p_suppressed IS NULL
    THEN RAISE EXCEPTION 'Deadlock transition policy is invalid' USING ERRCODE='22023'; END IF;
    IF NOT reporting.backup_cache_keys(p_prior,ARRAY['RuleId','TargetId','State','ConsecutiveMatches','ConsecutiveClears',
        'FirstMatchUtc','LastObservedUtc','FiredUtc','AcknowledgedUtc','AlertId','EpisodeId','LastOperationId','EvidenceDigest',
        'Reason','LastReason','DeliverySuppressed','ResolvedUtc','EpisodeStartedUtc','AcknowledgedBy','Revision','LastValue'])
       OR NOT reporting.backup_cache_keys(p_observation,ARRAY['TargetId','RuleId','ObservedAtUtc','Value','CollectorHealthy',
        'Reason','OperationId','EvidenceDigest','SampleId','RunId','SourceKind','MetricId','SourceCollector','SourceVersion','SourceSchemaVersion','SourceDigest'])
       OR coalesce(p_observation->>'ObservedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+]00:00)$'
       OR jsonb_typeof(p_observation->'Value') NOT IN ('number','null')
       OR p_observation->'CollectorHealthy' IS DISTINCT FROM 'null'::jsonb OR p_observation->'MetricId' IS DISTINCT FROM 'null'::jsonb
       OR p_observation->>'SourceKind' IS DISTINCT FROM 'deadlock_count_v1'
       OR p_observation->>'SourceCollector' IS DISTINCT FROM 'deadlocks.system-health'
       OR ((p_observation->>'SourceVersion'='1' AND p_observation->'SourceSchemaVersion'='1'::jsonb)
           OR (p_observation->>'SourceVersion'='2' AND p_observation->'SourceSchemaVersion'='2'::jsonb)) IS DISTINCT FROM true
       OR coalesce(p_observation->>'SourceDigest','') !~ '^[0-9a-f]{64}$'
       OR coalesce(p_observation->>'EvidenceDigest','') !~ '^[0-9a-f]{64}$'
       OR coalesce(p_observation->>'Reason','') !~ '^deadlock_count_v1[|]target=[1-9][0-9]{0,18}[|]reason=(current|collector_disabled|source_unavailable|source_stale|source_incomplete|source_inconsistent)[|]digest=[0-9a-f]{64}$'
       OR split_part(p_observation->>'Reason','|',4) IS DISTINCT FROM 'digest='||(p_observation->>'SourceDigest')
       OR coalesce((p_prior->'State')::text,'') !~ '^[1-5]$'
       OR coalesce((p_prior->'ConsecutiveMatches')::text,'') !~ '^(0|[1-9][0-9]?|100)$'
       OR coalesce((p_prior->'ConsecutiveClears')::text,'') !~ '^(0|[1-9][0-9]?|100)$'
       OR (p_prior->>'Revision')::bigint IS NULL OR (p_prior->>'Revision')::bigint<=0
    THEN RAISE EXCEPTION 'Deadlock transition observation or state shape is invalid' USING ERRCODE='22023'; END IF;
    v_target:=(p_observation->>'TargetId')::uuid; v_rule:=(p_observation->>'RuleId')::uuid;
    v_operation:=(p_observation->>'OperationId')::uuid; v_observed:=(p_observation->>'ObservedAtUtc')::timestamptz;
    v_value:=(p_observation->>'Value')::double precision; v_previous:=(p_prior->>'State')::integer;
    v_last:=(p_prior->>'LastObservedUtc')::timestamptz;
    IF v_target IS NULL OR v_rule IS NULL OR v_operation IS NULL OR NOT isfinite(v_observed)
       OR v_target='00000000-0000-0000-0000-000000000000'::uuid OR v_rule='00000000-0000-0000-0000-000000000000'::uuid
       OR v_target IS DISTINCT FROM (p_prior->>'TargetId')::uuid OR v_rule IS DISTINCT FROM (p_prior->>'RuleId')::uuid
       OR v_value IS NOT NULL AND (v_value NOT BETWEEN 0 AND 256 OR v_value<>trunc(v_value::numeric) OR (p_observation->>'RunId')::uuid IS NULL)
       OR (v_value IS NOT NULL) IS DISTINCT FROM (split_part(p_observation->>'Reason','|',3)='reason=current')
       OR (substring(split_part(p_observation->>'Reason','|',2) FROM 8))::bigint<=0
       OR v_last IS NOT NULL AND (NOT isfinite(v_last) OR v_observed<=v_last)
       OR p_observation->>'SampleId' IS DISTINCT FROM v_operation::text
       OR v_operation IS DISTINCT FROM alerting.canonical_operation_id(v_target,v_rule,v_observed,p_observation->>'Reason')
       OR (p_observation->>'RunId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
       OR (p_prior->>'ConsecutiveClears')::integer>0 AND v_previous NOT IN (3,4)
    THEN RAISE EXCEPTION 'Deadlock transition source or time binding is invalid' USING ERRCODE='22023'; END IF;
    v_expected_digest:=encode(alerting.canonical_evidence_sha256(v_target,v_rule,'deadlock_count_v1',NULL,'deadlocks.system-health',p_observation->>'SourceVersion',(p_observation->>'SourceSchemaVersion')::integer,
        p_observation->>'SourceDigest',v_observed,p_observation->>'SampleId',(p_observation->>'RunId')::uuid,
        v_value,NULL,p_observation->>'Reason'),'hex');
    IF p_observation->>'EvidenceDigest' IS DISTINCT FROM v_expected_digest
    THEN RAISE EXCEPTION 'Deadlock transition canonical digest is invalid' USING ERRCODE='22023'; END IF;
    IF v_value IS NULL THEN
        v_state:=p_prior||jsonb_build_object('State',CASE WHEN v_previous=2 THEN 1 ELSE v_previous END,
            'ConsecutiveMatches',0,'ConsecutiveClears',0,'FirstMatchUtc',NULL,'LastObservedUtc',v_observed,
            'LastOperationId',v_operation,'EvidenceDigest',v_expected_digest,'LastValue',NULL,
            'Reason',p_observation->>'Reason','LastReason',p_observation->>'Reason',
            'DeliverySuppressed',p_suppressed,'Revision',(p_prior->>'Revision')::bigint+1);
        RETURN alerting.canonical_decision_snapshot(jsonb_build_object('Observation',p_observation,'State',v_state,'Event',NULL,
            'DeliverySuppressed',p_suppressed,'Reason',CASE WHEN p_suppressed THEN 'maintenance' ELSE 'evidence_unknown' END));
    END IF;
    v_active:=v_previous IN (3,4);
    v_matches:=CASE p_comparison WHEN 1 THEN v_value>p_threshold-CASE WHEN v_active THEN p_hysteresis ELSE 0 END
        ELSE v_value>=p_threshold-CASE WHEN v_active THEN p_hysteresis ELSE 0 END END;
    v_clears:=CASE WHEN v_active AND NOT v_matches THEN
        CASE WHEN v_last IS NOT NULL AND v_observed-v_last>p_window THEN 1
             ELSE least(p_clear,(p_prior->>'ConsecutiveClears')::integer+1) END ELSE 0 END;
    v_partial:=v_clears>0 AND v_clears<p_clear;
    v_count:=CASE WHEN v_matches THEN least(p_confirm,(p_prior->>'ConsecutiveMatches')::integer+1)
        WHEN v_partial THEN least(p_confirm,(p_prior->>'ConsecutiveMatches')::integer) ELSE 0 END;
    v_first:=CASE WHEN v_matches THEN coalesce((p_prior->>'FirstMatchUtc')::timestamptz,v_observed)
        WHEN v_partial THEN (p_prior->>'FirstMatchUtc')::timestamptz ELSE NULL END;
    IF v_matches AND NOT v_active AND v_observed-v_first>p_window THEN v_count:=1; v_first:=v_observed; END IF;
    v_next:=CASE WHEN NOT v_matches THEN CASE WHEN v_partial THEN v_previous WHEN v_active THEN 5 ELSE 1 END
        WHEN v_active THEN v_previous WHEN v_count>=p_confirm THEN 3 ELSE 2 END;
    v_event:=CASE WHEN v_next=3 AND NOT v_active THEN 1 WHEN v_next=5 AND v_active THEN 2 ELSE NULL END;
    v_fresh:=v_previous=5;
    v_alert:=(p_prior->>'AlertId')::uuid; v_episode:=(p_prior->>'EpisodeId')::uuid;
    IF v_next IN (1,2) THEN v_alert:=NULL; v_episode:=NULL;
    ELSIF v_next=3 AND (v_fresh OR v_alert IS NULL) THEN
        v_episode:=v_operation; v_alert:=alerting.sha_uuid('alert|'||v_target::text||'|'||v_rule::text||'|'||v_operation::text);
    END IF;
    v_state:=p_prior||jsonb_build_object('State',v_next,'ConsecutiveMatches',v_count,'ConsecutiveClears',CASE WHEN v_partial THEN v_clears ELSE 0 END,
        'FirstMatchUtc',v_first,'LastObservedUtc',v_observed,'AlertId',v_alert,'EpisodeId',v_episode,
        'FiredUtc',CASE WHEN v_next=3 THEN CASE WHEN v_fresh THEN v_observed ELSE coalesce((p_prior->>'FiredUtc')::timestamptz,v_observed) END
            WHEN v_fresh THEN NULL ELSE (p_prior->>'FiredUtc')::timestamptz END,
        'AcknowledgedUtc',CASE WHEN v_fresh THEN NULL ELSE (p_prior->>'AcknowledgedUtc')::timestamptz END,
        'ResolvedUtc',CASE WHEN v_fresh THEN NULL WHEN v_next=5 THEN v_observed ELSE (p_prior->>'ResolvedUtc')::timestamptz END,
        'EpisodeStartedUtc',CASE WHEN v_next IN (1,2) THEN NULL
            WHEN v_next=3 AND (v_fresh OR p_prior->>'EpisodeStartedUtc' IS NULL) THEN v_observed ELSE (p_prior->>'EpisodeStartedUtc')::timestamptz END,
        'AcknowledgedBy',CASE WHEN v_fresh THEN NULL ELSE p_prior->>'AcknowledgedBy' END,
        'LastOperationId',v_operation,'EvidenceDigest',v_expected_digest,'Reason',p_observation->>'Reason','LastReason',p_observation->>'Reason',
        'DeliverySuppressed',p_suppressed,'Revision',(p_prior->>'Revision')::bigint+1,'LastValue',v_value);
    RETURN alerting.canonical_decision_snapshot(jsonb_build_object('Observation',p_observation,'State',v_state,'Event',v_event,
        'DeliverySuppressed',p_suppressed,'Reason',CASE WHEN p_suppressed THEN 'maintenance' ELSE 'evaluated' END));
END $transition$;
REVOKE ALL ON FUNCTION alerting.deadlock_alert_transition(smallint,double precision,double precision,integer,integer,interval,jsonb,jsonb,boolean)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
