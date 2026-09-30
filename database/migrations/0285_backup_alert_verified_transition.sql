-- Independently derive every backup transition from committed prior state.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.backup_alert_transition(p_kind smallint,p_confirm integer,p_clear integer,p_window interval,
    p_prior jsonb,p_observation jsonb,p_suppressed boolean)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $transition$
DECLARE v_previous integer; v_next integer; v_event integer; v_count integer; v_clears integer;
    v_value double precision; v_observed timestamptz; v_first timestamptz; v_last timestamptz;
    v_active boolean; v_matches boolean; v_partial boolean; v_fresh boolean;
    v_alert uuid; v_episode uuid; v_operation uuid; v_target uuid; v_rule uuid; v_expected_digest text;
    v_state jsonb; v_source text;
BEGIN
    IF p_kind IS NULL OR p_kind NOT IN (3,4,5) OR p_confirm IS NULL OR p_confirm NOT BETWEEN 1 AND 100
       OR p_clear IS NULL OR p_clear NOT BETWEEN 1 AND 100 OR p_window IS NULL
       OR p_window<interval '0 seconds' OR p_window>interval '7 days' OR p_suppressed IS NULL
    THEN RAISE EXCEPTION 'Backup transition policy is invalid' USING ERRCODE='22023'; END IF;
    IF p_observation->'Value'='null'::jsonb THEN
        RETURN alerting.backup_unknown_transition(p_kind,p_prior,p_observation,p_suppressed);
    END IF;
    IF NOT reporting.backup_cache_keys(p_prior,ARRAY['RuleId','TargetId','State','ConsecutiveMatches','ConsecutiveClears',
        'FirstMatchUtc','LastObservedUtc','FiredUtc','AcknowledgedUtc','AlertId','EpisodeId','LastOperationId','EvidenceDigest',
        'Reason','LastReason','DeliverySuppressed','ResolvedUtc','EpisodeStartedUtc','AcknowledgedBy','Revision','LastValue'])
       OR NOT reporting.backup_cache_keys(p_observation,ARRAY['TargetId','RuleId','ObservedAtUtc','Value','CollectorHealthy',
        'Reason','OperationId','EvidenceDigest','SampleId','RunId','SourceKind','MetricId','SourceCollector','SourceVersion','SourceSchemaVersion','SourceDigest'])
       OR coalesce(p_observation->>'ObservedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+]00:00)$'
       OR coalesce((p_observation->'Value')::text,'') !~ '^(0|[1-9][0-9]{0,2})$'
       OR p_observation->'CollectorHealthy' IS DISTINCT FROM 'null'::jsonb
       OR p_observation->'MetricId' IS DISTINCT FROM 'null'::jsonb
       OR p_observation->>'SourceCollector' IS DISTINCT FROM 'backups.status'
       OR p_observation->>'SourceVersion' IS DISTINCT FROM '2' OR p_observation->'SourceSchemaVersion' IS DISTINCT FROM '2'::jsonb
       OR coalesce(p_observation->>'SourceDigest','') !~ '^[a-f0-9]{64}$'
       OR coalesce(p_observation->>'EvidenceDigest','') !~ '^[a-f0-9]{64}$'
       OR coalesce((p_prior->'State')::text,'') !~ '^[1-5]$'
       OR coalesce((p_prior->'ConsecutiveMatches')::text,'') !~ '^(0|[1-9][0-9]?|100)$'
       OR coalesce((p_prior->'ConsecutiveClears')::text,'') !~ '^(0|[1-9][0-9]?|100)$'
       OR (p_prior->>'Revision')::bigint IS NULL OR (p_prior->>'Revision')::bigint<=0
    THEN RAISE EXCEPTION 'Backup transition observation or state shape is invalid' USING ERRCODE='22023'; END IF;
    v_target:=(p_observation->>'TargetId')::uuid; v_rule:=(p_observation->>'RuleId')::uuid;
    v_operation:=(p_observation->>'OperationId')::uuid; v_observed:=(p_observation->>'ObservedAtUtc')::timestamptz;
    v_value:=(p_observation->>'Value')::double precision; v_previous:=(p_prior->>'State')::integer;
    v_last:=(p_prior->>'LastObservedUtc')::timestamptz;
    v_source:=CASE p_kind WHEN 3 THEN 'backup_full_policy_v2' WHEN 4 THEN 'backup_log_policy_v2' ELSE 'backup_observed_faults_v2' END;
    IF v_target IS NULL OR v_rule IS NULL OR v_operation IS NULL OR NOT isfinite(v_observed)
       OR v_target='00000000-0000-0000-0000-000000000000'::uuid OR v_rule='00000000-0000-0000-0000-000000000000'::uuid
       OR v_target IS DISTINCT FROM (p_prior->>'TargetId')::uuid OR v_rule IS DISTINCT FROM (p_prior->>'RuleId')::uuid
       OR v_value>512 OR p_kind=5 AND v_value=0 OR v_last IS NOT NULL AND v_observed<=v_last
       OR p_observation->>'SourceKind' IS DISTINCT FROM v_source
       OR p_observation->>'SampleId' IS DISTINCT FROM v_operation::text
       OR v_operation IS DISTINCT FROM alerting.canonical_operation_id(v_target,v_rule,v_observed,p_observation->>'Reason')
       OR (p_observation->>'RunId')::uuid IS NULL OR (p_observation->>'RunId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
       OR (p_prior->>'ConsecutiveClears')::integer>0 AND v_previous NOT IN (3,4)
    THEN RAISE EXCEPTION 'Backup transition source or time binding is invalid' USING ERRCODE='22023'; END IF;
    v_expected_digest:=encode(alerting.canonical_evidence_sha256(v_target,v_rule,v_source,NULL,'backups.status','2',2,
        p_observation->>'SourceDigest',v_observed,p_observation->>'SampleId',(p_observation->>'RunId')::uuid,
        v_value,NULL,p_observation->>'Reason'),'hex');
    IF p_observation->>'EvidenceDigest' IS DISTINCT FROM v_expected_digest
    THEN RAISE EXCEPTION 'Backup transition canonical digest is invalid' USING ERRCODE='22023'; END IF;
    v_active:=v_previous IN (3,4); v_matches:=v_value>0;
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
REVOKE ALL ON FUNCTION alerting.backup_alert_transition(smallint,integer,integer,interval,jsonb,jsonb,boolean)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
