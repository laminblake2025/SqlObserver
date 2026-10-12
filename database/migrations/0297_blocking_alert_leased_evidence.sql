-- Bounded blocking-duration evidence; no rule/catalog activation in this migration.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.read_blocking_alert_evidence(
    p_instance uuid,p_revision bigint,p_owner uuid,p_fencing bigint)
RETURNS TABLE(read_at timestamptz,payload jsonb,source_digest text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $read$
DECLARE v_now timestamptz; v_health record; v_outcome record; v_rows record;
    v_reason text:='source_unavailable'; v_until timestamptz; v_duration bigint;
    v_ttl interval; v_body jsonb; v_run uuid; v_schedule bigint; v_completed timestamptz;
    v_observed timestamptz; v_count integer; v_completion text;
BEGIN
    IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'::uuid
       OR p_revision IS NULL OR p_revision<=0
       OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Blocking alert target scope is invalid' USING ERRCODE='42501'; END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance AND t.revision=p_revision
        AND t.lifecycle_state='active' FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Blocking alert current target revision is required' USING ERRCODE='42501'; END IF;
    PERFORM 1 FROM control.collector_schedule s WHERE s.instance_id=p_instance
        AND s.collector_id='blocking.current' FOR SHARE;
    v_now:=clock_timestamp();
    SELECT h.* INTO v_health FROM reporting.collector_health_projection h
        WHERE h.instance_id=p_instance AND h.collector_id='blocking.current';
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
                    OR v_outcome.rejected_item_count<>0
                THEN v_reason:='source_incomplete';
                ELSE
                    -- The (target,run,edge identity) index bounds the lookup. A
                    -- look-ahead row detects overflow; never infer zero from a page.
                    SELECT count(*)::integer AS n,max(e.wait_duration_ms) AS maximum_wait,
                        min(e.observed_at) AS first_observed,max(e.observed_at) AS last_observed,
                        bool_and(e.target_revision=p_revision AND e.blocked_session_id IS DISTINCT FROM e.blocker_session_id
                            AND e.wait_duration_ms BETWEEN 0 AND 9007199254740991) AS valid
                    INTO v_rows FROM (SELECT b.* FROM events.blocking_edge b
                        WHERE b.instance_id=p_instance AND b.collection_run_id=v_run
                        ORDER BY b.blocked_session_id,b.blocker_kind,b.blocker_session_id,b.wait_type LIMIT 1025) e;
                    v_count:=v_rows.n; v_observed:=v_rows.last_observed;
                    IF v_rows.n>1024 OR v_rows.n<>v_outcome.output_item_count
                        OR v_outcome.source_row_count<>v_outcome.output_item_count
                        OR v_outcome.inserted_item_count+v_outcome.duplicate_item_count<>v_outcome.output_item_count
                        OR v_rows.n>0 AND v_rows.valid IS NOT TRUE
                    THEN v_reason:='source_inconsistent';
                    ELSIF v_rows.n>0 AND (v_rows.first_observed+v_ttl<=v_now OR v_rows.last_observed>v_now)
                    THEN v_reason:='source_stale';
                    ELSE
                        v_reason:='current'; v_duration:=coalesce(v_rows.maximum_wait,0);
                        v_until:=least(v_completed+v_ttl,coalesce(v_rows.first_observed+v_ttl,v_completed+v_ttl));
                    END IF;
                END IF;
            END IF;
        END IF;
    END IF;
    v_body:=jsonb_build_object('schemaVersion',1,'targetId',p_instance,'targetRevision',p_revision,
        'validUntilUtc',v_until,'scheduleRevision',v_schedule,'runId',v_run,
        'completedAtUtc',v_completed,'sourceObservedAtUtc',v_observed,'retainedEdges',v_count,
        'maximumWaitMilliseconds',v_duration,'reason',v_reason,'completionDigest',v_completion);
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    RETURN QUERY SELECT v_now,v_body,encode(sha256(convert_to(v_body::text,'UTF8')),'hex');
END $read$;
REVOKE ALL ON FUNCTION alerting.read_blocking_alert_evidence(uuid,bigint,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.read_blocking_alert_evidence(uuid,bigint,uuid,bigint) TO sqlobserver_collector;
