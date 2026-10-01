-- Preserve the exact latest deadlock scan, including duplicate events, for
-- bounded alert evidence. One private row per target; no rule activation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE alerting.deadlock_source_snapshot
(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id) ON DELETE CASCADE,
    target_revision bigint NOT NULL CHECK(target_revision>0),
    schedule_revision bigint NOT NULL CHECK(schedule_revision>0),
    run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id) ON DELETE CASCADE,
    completed_at timestamptz NOT NULL,
    completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32),
    event_ids uuid[] NOT NULL,
    occurred_ats timestamptz[] NOT NULL,
    parse_truncated boolean[] NOT NULL,
    CHECK(cardinality(event_ids) BETWEEN 0 AND 256
      AND cardinality(occurred_ats)=cardinality(event_ids)
      AND cardinality(parse_truncated)=cardinality(event_ids)
      AND array_position(event_ids,NULL) IS NULL
      AND array_position(occurred_ats,NULL) IS NULL
      AND array_position(parse_truncated,NULL) IS NULL)
);
ALTER TABLE alerting.deadlock_source_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.deadlock_source_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlock_source_snapshot_owner ON alerting.deadlock_source_snapshot
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.deadlock_source_snapshot
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

ALTER FUNCTION control.commit_deadlock_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[])
    RENAME TO commit_deadlock_collection_run_core;
REVOKE ALL ON FUNCTION control.commit_deadlock_collection_run_core(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[])
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.commit_deadlock_collection_run
(
    p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_id text,
    p_collector_version integer,p_output_schema_version integer,p_schedule_revision bigint,
    p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
    p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,
    p_attempt_count integer,p_source_row_count integer,p_output_item_count integer,
    p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
    p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,
    p_next_consecutive_failures integer,p_occurred_ats timestamptz[],p_event_ids uuid[],
    p_fingerprints bytea[],p_participant_counts integer[],p_relation_counts integer[],
    p_parse_truncated boolean[],p_participant_json jsonb[],p_relation_json jsonb[],p_sizes integer[]
)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,
    rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $commit$
DECLARE v_result record; v_digest bytea;
BEGIN
    SELECT r.* INTO STRICT v_result FROM control.commit_deadlock_collection_run_core(
        p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,
        p_output_schema_version,p_schedule_revision,p_scheduled_at,p_work_key,
        p_owner_execution_id,p_fencing_token,p_request_digest,p_outcome,p_reason_code,
        p_duration_ms,p_attempt_count,p_source_row_count,p_output_item_count,p_response_bytes,
        p_output_bytes,p_loss_kind,p_minimum_lost_items,p_loss_count_is_exact,p_minimum_lost_bytes,
        p_next_circuit_state,p_next_consecutive_failures,p_occurred_ats,p_event_ids,p_fingerprints,
        p_participant_counts,p_relation_counts,p_parse_truncated,p_participant_json,p_relation_json,p_sizes) r;
    IF v_result.result_status='committed' THEN
        SELECT o.completion_digest INTO STRICT v_digest
            FROM telemetry.collection_run_outcome o WHERE o.run_id=p_run_id;
        INSERT INTO alerting.deadlock_source_snapshot(instance_id,target_revision,schedule_revision,
            run_id,completed_at,completion_digest,event_ids,occurred_ats,parse_truncated)
        VALUES(p_instance_id,p_target_revision,p_schedule_revision,p_run_id,v_result.committed_at,
            v_digest,p_event_ids,p_occurred_ats,p_parse_truncated)
        ON CONFLICT(instance_id) DO UPDATE SET target_revision=EXCLUDED.target_revision,
            schedule_revision=EXCLUDED.schedule_revision,run_id=EXCLUDED.run_id,
            completed_at=EXCLUDED.completed_at,completion_digest=EXCLUDED.completion_digest,
            event_ids=EXCLUDED.event_ids,occurred_ats=EXCLUDED.occurred_ats,
            parse_truncated=EXCLUDED.parse_truncated;
        -- Snapshot and canonical outcome roll back together on a lost fence.
        PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    END IF;
    -- An old exact replay must never replace the latest snapshot.
    RETURN QUERY SELECT v_result.result_status,v_result.inserted_count,v_result.duplicate_count,
        v_result.rejected_count,v_result.persisted_bytes,v_result.committed_at;
END $commit$;
REVOKE ALL ON FUNCTION control.commit_deadlock_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[])
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_deadlock_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[])
    TO sqlobserver_collector;

CREATE FUNCTION alerting.read_deadlock_alert_evidence(
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
REVOKE ALL ON FUNCTION alerting.read_deadlock_alert_evidence(uuid,bigint,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.read_deadlock_alert_evidence(uuid,bigint,uuid,bigint) TO sqlobserver_collector;
