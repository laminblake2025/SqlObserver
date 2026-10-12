-- Private deterministic transition for fresh unknown backup evidence.
-- The future dispatcher must validate the exact claim/context and handle immutable replay first.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.backup_unknown_transition(
    p_kind smallint, p_prior jsonb, p_observation jsonb, p_suppressed boolean)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER
SET search_path = pg_catalog SET row_security = on SET TimeZone = 'UTC'
AS $transition$
DECLARE state integer; observed timestamptz; previous timestamptz; expected_source text;
    computed bytea; result_state jsonb;
BEGIN
    IF p_kind IS NULL OR p_kind NOT IN (3,4,5) OR p_suppressed IS NULL
       OR NOT reporting.backup_cache_keys(p_prior, ARRAY[
          'RuleId','TargetId','State','ConsecutiveMatches','ConsecutiveClears','FirstMatchUtc','LastObservedUtc',
          'FiredUtc','AcknowledgedUtc','AlertId','EpisodeId','LastOperationId','EvidenceDigest','Reason','LastReason',
          'DeliverySuppressed','ResolvedUtc','EpisodeStartedUtc','AcknowledgedBy','Revision','LastValue'])
       OR NOT reporting.backup_cache_keys(p_observation, ARRAY[
          'TargetId','RuleId','ObservedAtUtc','Value','CollectorHealthy','Reason','OperationId','EvidenceDigest',
          'SampleId','RunId','SourceKind','MetricId','SourceCollector','SourceVersion','SourceSchemaVersion','SourceDigest'])
    THEN RAISE EXCEPTION 'Unknown backup transition shape is invalid' USING ERRCODE='22023'; END IF;
    expected_source := CASE p_kind WHEN 3 THEN 'backup_full_policy_v2'
        WHEN 4 THEN 'backup_log_policy_v2' ELSE 'backup_observed_faults_v2' END;
    IF p_observation->'Value' IS DISTINCT FROM 'null'::jsonb
       OR p_observation->'CollectorHealthy' IS DISTINCT FROM 'null'::jsonb
       OR p_observation->'MetricId' IS DISTINCT FROM 'null'::jsonb
       OR p_observation->>'SourceKind' IS DISTINCT FROM expected_source
       OR p_observation->>'SourceCollector' IS DISTINCT FROM 'backups.status'
       OR p_observation->>'SourceVersion' IS DISTINCT FROM '2'
       OR p_observation->'SourceSchemaVersion' IS DISTINCT FROM '2'::jsonb
       OR p_observation->>'TargetId' IS DISTINCT FROM p_prior->>'TargetId'
       OR p_observation->>'RuleId' IS DISTINCT FROM p_prior->>'RuleId'
       OR (p_observation->>'TargetId')::uuid IS NULL OR (p_observation->>'RuleId')::uuid IS NULL
       OR (p_observation->>'TargetId')::uuid = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_observation->>'RuleId')::uuid = '00000000-0000-0000-0000-000000000000'::uuid
       OR coalesce(p_observation->>'SampleId','') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
       OR (p_observation->>'SampleId')::uuid = '00000000-0000-0000-0000-000000000000'::uuid
       OR (p_observation->>'OperationId')::uuid IS NULL
       OR (p_observation->>'OperationId')::uuid = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_observation->>'SourceDigest' IS NOT NULL AND p_observation->>'SourceDigest' !~ '^[0-9a-f]{64}$'
       OR p_observation->>'SourceDigest' IS NULL AND p_observation->>'RunId' IS NOT NULL
       OR coalesce(p_observation->>'RunId','') = '00000000-0000-0000-0000-000000000000'
       OR coalesce(p_observation->>'ObservedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+]00:00)$'
    THEN RAISE EXCEPTION 'Unknown backup observation binding is invalid' USING ERRCODE='22023'; END IF;
    computed := alerting.canonical_evidence_sha256(
        (p_observation->>'TargetId')::uuid, (p_observation->>'RuleId')::uuid,
        p_observation->>'SourceKind', NULL, p_observation->>'SourceCollector', p_observation->>'SourceVersion',
        2, p_observation->>'SourceDigest', (p_observation->>'ObservedAtUtc')::timestamptz,
        p_observation->>'SampleId', (p_observation->>'RunId')::uuid, NULL, NULL, p_observation->>'Reason');
    IF p_observation->>'EvidenceDigest' IS DISTINCT FROM encode(computed,'hex')
    THEN RAISE EXCEPTION 'Unknown backup canonical digest is invalid' USING ERRCODE='22023'; END IF;
    state := (p_prior->>'State')::integer;
    observed := (p_observation->>'ObservedAtUtc')::timestamptz;
    previous := (p_prior->>'LastObservedUtc')::timestamptz;
    IF state IS NULL OR state NOT BETWEEN 1 AND 5 OR (p_prior->>'Revision')::bigint IS NULL
       OR (p_prior->>'Revision')::bigint <= 0 OR NOT isfinite(observed)
       OR previous IS NOT NULL AND observed <= previous
       OR coalesce((p_prior->'ConsecutiveMatches')::text,'') !~ '^(0|[1-9][0-9]?|100)$'
       OR coalesce((p_prior->'ConsecutiveClears')::text,'') !~ '^(0|[1-9][0-9]?|100)$'
       OR (p_prior->>'ConsecutiveClears')::integer > 0 AND state NOT IN (3,4)
    THEN RAISE EXCEPTION 'Unknown backup prior state or time is invalid' USING ERRCODE='22023'; END IF;
    result_state := p_prior || jsonb_build_object(
        'State', CASE WHEN state=2 THEN 1 ELSE state END,
        'ConsecutiveMatches', 0, 'ConsecutiveClears', 0, 'FirstMatchUtc', NULL,
        'LastObservedUtc', observed, 'LastOperationId', (p_observation->>'OperationId')::uuid,
        'EvidenceDigest', p_observation->>'EvidenceDigest', 'LastValue', NULL,
        'Reason', p_observation->>'Reason', 'LastReason', p_observation->>'Reason',
        'DeliverySuppressed', p_suppressed, 'Revision', (p_prior->>'Revision')::bigint + 1);
    RETURN alerting.canonical_decision_snapshot(jsonb_build_object(
        'Observation', p_observation, 'State', result_state, 'Event', NULL,
        'DeliverySuppressed', p_suppressed,
        'Reason', CASE WHEN p_suppressed THEN 'maintenance' ELSE 'evidence_unknown' END));
END $transition$;
REVOKE ALL ON FUNCTION alerting.backup_unknown_transition(smallint,jsonb,jsonb,boolean)
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;
