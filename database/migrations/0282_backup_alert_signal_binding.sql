-- Derive backup alert signals in the repository before accepting a caller's decision.
-- The private observation/check helpers are for the subsequent queue/commit dispatcher.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.backup_alert_signals(
    p_instance uuid, p_revision bigint, p_read_at timestamptz, p_payload jsonb, p_digest text)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path = pg_catalog SET row_security = on SET TimeZone = 'UTC'
AS $signals$
DECLARE body jsonb; context jsonb; counts jsonb; n integer; expected integer;
    kind integer; state integer; reason integer; affected integer; faults integer;
    result jsonb := '[]'::jsonb;
BEGIN
    IF p_instance IS NULL OR p_instance = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_revision IS NULL OR p_revision <= 0 OR p_read_at IS NULL OR NOT isfinite(p_read_at)
    THEN RAISE EXCEPTION 'Backup alert capture identity is invalid' USING ERRCODE='22023'; END IF;
    IF p_payload IS NULL THEN
        IF p_digest IS NOT NULL THEN RAISE EXCEPTION 'Absent backup capture cannot have a digest' USING ERRCODE='22023'; END IF;
    ELSE
        IF octet_length(p_payload::text)>16384
           OR NOT reporting.backup_cache_keys(p_payload,ARRAY['schemaVersion','summary'])
           OR p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb
           OR p_digest IS DISTINCT FROM encode(sha256(convert_to(p_payload::text,'UTF8')),'hex')
        THEN RAISE EXCEPTION 'Backup alert capture envelope is invalid' USING ERRCODE='22023'; END IF;
        body := p_payload->'summary'; context := body->'context';
        IF NOT reporting.backup_cache_keys(body,ARRAY['context','validUntilUtc','status','full','log','observedFaultDatabases'])
           OR NOT reporting.backup_cache_keys(context,ARRAY['targetId','atUtc','targetRevision','runId','sourceObservedAtUtc',
               'completedAtUtc','receivedAtUtc','sourceZoneId','historyLowerBoundLocal','coverage','evidenceStatus',
               'sourceRows','outputItems','retainedItems','databaseCount','collectorEnabled','policyHistoryCount','qualificationCommandRevision'])
           OR (context->>'targetId')::uuid IS DISTINCT FROM p_instance
           OR (context->>'targetRevision')::bigint IS DISTINCT FROM p_revision
           OR (context->>'atUtc')::timestamptz IS NULL OR NOT isfinite((context->>'atUtc')::timestamptz)
           OR (body->>'validUntilUtc')::timestamptz IS NULL OR NOT isfinite((body->>'validUntilUtc')::timestamptz)
           OR (context->>'atUtc')::timestamptz > p_read_at OR p_read_at >= (body->>'validUntilUtc')::timestamptz
           OR (body->>'validUntilUtc')::timestamptz > (context->>'atUtc')::timestamptz + interval '2 minutes'
           OR coalesce((context->'databaseCount')::text,'') !~ '^(0|[1-9][0-9]{0,2})$'
           OR coalesce((context->'coverage')::text,'') !~ '^[0-6]$'
           OR coalesce((context->'evidenceStatus')::text,'') !~ '^[1-6]$'
           OR jsonb_typeof(context->'collectorEnabled') IS DISTINCT FROM 'boolean'
           OR coalesce((body->'observedFaultDatabases')::text,'') !~ '^(0|[1-9][0-9]{0,2})$'
        THEN RAISE EXCEPTION 'Backup alert capture context is invalid' USING ERRCODE='22023'; END IF;
        n := (context->>'databaseCount')::integer;
        expected := CASE WHEN (context->>'evidenceStatus')::integer=6 THEN n ELSE 0 END;
        faults := (body->>'observedFaultDatabases')::integer;
        IF n>512 OR faults>expected
           OR NOT reporting.backup_cache_counts_valid(body->'full',expected)
           OR NOT reporting.backup_cache_counts_valid(body->'log',expected)
           OR EXISTS(SELECT 1 FROM jsonb_each(body->'full') e
               WHERE e.key<>'unknownReasons' AND e.value::text !~ '^(0|[1-9][0-9]{0,2})$')
           OR EXISTS(SELECT 1 FROM jsonb_each(body->'log') e
               WHERE e.key<>'unknownReasons' AND e.value::text !~ '^(0|[1-9][0-9]{0,2})$')
        THEN RAISE EXCEPTION 'Backup alert capture counts are invalid' USING ERRCODE='22023'; END IF;
        state := CASE WHEN (context->>'evidenceStatus')::integer<>6 THEN 0
            WHEN (body->'full'->>'policyViolated')::integer+(body->'log'->>'policyViolated')::integer>0 THEN 2
            WHEN NOT (context->>'collectorEnabled')::boolean OR (context->>'coverage')::integer<>1
                 OR (body->'full'->>'indeterminate')::integer+(body->'log'->>'indeterminate')::integer>0 THEN 0
            WHEN (body->'full'->>'disabled')::integer+(body->'log'->>'disabled')::integer=2*n THEN 3
            WHEN (body->'full'->>'withinThreshold')::integer+(body->'log'->>'withinThreshold')::integer>0 THEN 1 ELSE 0 END;
        IF body->'status' IS DISTINCT FROM to_jsonb(state)
        THEN RAISE EXCEPTION 'Backup alert aggregate status is invalid' USING ERRCODE='22023'; END IF;
    END IF;
    FOR kind IN 1..3 LOOP
        state := 0; affected := NULL;
        IF p_payload IS NULL THEN reason := 6;
        ELSIF NOT (context->>'collectorEnabled')::boolean THEN reason := 7;
        ELSIF (context->>'evidenceStatus')::integer<>6 THEN reason := 8;
        ELSIF kind=3 THEN
            IF faults>0 THEN state:=1; reason:=4; affected:=faults;
            ELSIF (context->>'coverage')::integer<>1 THEN reason:=9;
            ELSIF (body->'full'->>'indeterminate')::integer+(body->'log'->>'indeterminate')::integer>0 THEN reason:=10;
            ELSIF (body->'full'->>'disabled')::integer+(body->'log'->>'disabled')::integer>0 THEN reason:=11;
            ELSE reason:=5; END IF;
        ELSE
            counts := body->CASE WHEN kind=1 THEN 'full' ELSE 'log' END;
            IF (counts->>'policyViolated')::integer>0 THEN state:=1; reason:=1; affected:=(counts->>'policyViolated')::integer;
            ELSIF (counts->>'disabled')::integer=n THEN state:=3; reason:=11;
            ELSIF (context->>'coverage')::integer<>1 THEN reason:=9;
            ELSIF (counts->>'indeterminate')::integer>0 THEN reason:=10;
            ELSIF (counts->>'disabled')::integer>0 THEN reason:=11;
            ELSIF kind=2 AND (counts->>'notRequired')::integer=n THEN state:=4; reason:=3; affected:=0;
            ELSE state:=2; reason:=2; affected:=0; END IF;
        END IF;
        result := result || jsonb_build_array(jsonb_build_object(
            'kind',kind,'state',state,'reason',reason,'affectedDatabases',affected));
    END LOOP;
    RETURN result;
END $signals$;

CREATE FUNCTION alerting.backup_alert_observation(
    p_instance uuid, p_revision bigint, p_rule uuid, p_kind smallint,
    p_read_at timestamptz, p_payload jsonb, p_digest text)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path = pg_catalog SET row_security = on SET TimeZone = 'UTC'
AS $observation$
DECLARE signal jsonb; source text; reason text; operation uuid; run uuid; value double precision;
BEGIN
    IF p_rule IS NULL OR p_rule='00000000-0000-0000-0000-000000000000'::uuid
       OR p_kind IS NULL OR p_kind NOT IN (3,4,5)
    THEN RAISE EXCEPTION 'Backup alert rule identity is invalid' USING ERRCODE='22023'; END IF;
    signal := alerting.backup_alert_signals(p_instance,p_revision,p_read_at,p_payload,p_digest)->(p_kind-3);
    source := CASE p_kind WHEN 3 THEN 'backup_full_policy_v2' WHEN 4 THEN 'backup_log_policy_v2' ELSE 'backup_observed_faults_v2' END;
    reason := source || '|target=' || p_revision::text || '|state=' || (signal->>'state')
        || '|reason=' || (signal->>'reason') || '|digest=' || coalesce(p_digest,'none');
    operation := alerting.canonical_operation_id(p_instance,p_rule,p_read_at,reason);
    run := (p_payload->'summary'->'context'->>'runId')::uuid;
    value := (signal->>'affectedDatabases')::double precision;
    RETURN jsonb_build_object('TargetId',p_instance,'RuleId',p_rule,'ObservedAtUtc',p_read_at,
        'Value',value,'CollectorHealthy',NULL,'Reason',reason,'SampleId',operation::text,'RunId',run,
        'SourceKind',source,'MetricId',NULL,'SourceCollector','backups.status','SourceVersion','2','SourceSchemaVersion',2,
        'SourceDigest',p_digest,'OperationId',operation,'EvidenceDigest',encode(alerting.canonical_evidence_sha256(
            p_instance,p_rule,source,NULL,'backups.status','2',2,p_digest,p_read_at,operation::text,run,value,NULL,reason),'hex'));
END $observation$;

CREATE FUNCTION alerting.read_backup_alert_capture(p_instance uuid,p_revision bigint,p_owner uuid,p_fencing bigint)
RETURNS TABLE(read_at timestamptz,payload jsonb,source_digest text,signals jsonb)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path = pg_catalog SET row_security = on SET TimeZone = 'UTC'
AS $read$
DECLARE capture record;
BEGIN
    SELECT * INTO STRICT capture FROM alerting.read_backup_fleet_evidence(p_instance,p_revision,p_owner,p_fencing);
    RETURN QUERY SELECT capture.read_at,capture.payload,capture.source_digest,
        alerting.backup_alert_signals(p_instance,p_revision,capture.read_at,capture.payload,capture.source_digest);
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
END $read$;

CREATE FUNCTION alerting.assert_backup_alert_observation(
    p_instance uuid,p_revision bigint,p_rule uuid,p_kind smallint,p_read_at timestamptz,
    p_payload jsonb,p_observation jsonb,p_owner uuid,p_fencing bigint)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path = pg_catalog SET row_security = on SET TimeZone = 'UTC'
AS $assert$
DECLARE current_capture record; expected jsonb;
BEGIN
    -- Freeze publication even when there is no cache row to lock. Publishers,
    -- policy/qualification edits and collector commits take this target lock too.
    -- The dispatcher must use target-before-rule/state lock order and check claim
    -- ownership, persisted rule revision and immutable replay independently.
    IF p_instance IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Backup alert commit target scope is invalid' USING ERRCODE='42501'; END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target target WHERE target.instance_id=p_instance
        AND target.revision=p_revision AND target.lifecycle_state='active' FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Backup alert commit target revision changed' USING ERRCODE='42501'; END IF;
    SELECT * INTO STRICT current_capture FROM alerting.read_backup_fleet_evidence(p_instance,p_revision,p_owner,p_fencing);
    IF p_read_at IS NULL OR NOT isfinite(p_read_at) OR p_read_at>current_capture.read_at
       OR p_read_at<current_capture.read_at-interval '2 minutes'
       OR p_payload IS DISTINCT FROM current_capture.payload
       OR p_observation->>'SourceDigest' IS DISTINCT FROM current_capture.source_digest
    THEN RAISE EXCEPTION 'Backup alert captured source changed or expired' USING ERRCODE='40001'; END IF;
    expected := alerting.backup_alert_observation(p_instance,p_revision,p_rule,p_kind,p_read_at,p_payload,current_capture.source_digest);
    IF coalesce(p_observation->>'ObservedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+]00:00)$'
       OR alerting.canonical_decision_snapshot(jsonb_build_object('Observation',p_observation))->'Observation'
       IS DISTINCT FROM alerting.canonical_decision_snapshot(jsonb_build_object('Observation',expected))->'Observation'
       OR NOT reporting.backup_cache_keys(p_observation,ARRAY['TargetId','RuleId','ObservedAtUtc','Value','CollectorHealthy',
           'Reason','SampleId','RunId','SourceKind','MetricId','SourceCollector','SourceVersion','SourceSchemaVersion','SourceDigest','OperationId','EvidenceDigest'])
    THEN RAISE EXCEPTION 'Backup alert observation differs from captured source' USING ERRCODE='22023'; END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile',p_owner,p_fencing);
END $assert$;

REVOKE ALL ON FUNCTION alerting.backup_alert_signals(uuid,bigint,timestamptz,jsonb,text),
    alerting.backup_alert_observation(uuid,bigint,uuid,smallint,timestamptz,jsonb,text),
    alerting.assert_backup_alert_observation(uuid,bigint,uuid,smallint,timestamptz,jsonb,jsonb,uuid,bigint),
    alerting.read_backup_alert_capture(uuid,bigint,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.read_backup_alert_capture(uuid,bigint,uuid,bigint) TO sqlobserver_collector;
