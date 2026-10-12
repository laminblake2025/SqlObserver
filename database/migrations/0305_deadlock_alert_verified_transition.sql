-- Independently derive deadlock count transitions, including unknown evidence.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.deadlock_alert_transition(p_comparison smallint,p_threshold double precision,p_hysteresis double precision,
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
       OR p_observation->>'SourceVersion' IS DISTINCT FROM '1' OR p_observation->'SourceSchemaVersion' IS DISTINCT FROM '1'::jsonb
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
    v_expected_digest:=encode(alerting.canonical_evidence_sha256(v_target,v_rule,'deadlock_count_v1',NULL,'deadlocks.system-health','1',1,
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
