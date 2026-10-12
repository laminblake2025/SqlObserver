-- Retained sampled engine detections. This does not activate the staged collector.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.server_lifecycle_event_json(p_event events.server_lifecycle_event)
RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog SET TimeZone='UTC'
AS $event$
 SELECT jsonb_build_object('eventId',p_event.event_id,'targetId',p_event.instance_id,
  'sampleTargetRevision',p_event.sample_target_revision,'beforeRunId',p_event.before_run_id,
  'afterRunId',p_event.after_run_id,'kind',p_event.kind,
  'affectedIdentityFingerprint',encode(p_event.affected_identity_fingerprint,'hex'),
  'beforeObservedAtUtc',p_event.before_observed_at,'afterObservedAtUtc',p_event.after_observed_at,
  'detectedAtUtc',p_event.detected_at,'occurredAtUtc',NULL,'sourceSchemaVersion',p_event.source_schema_version)
$event$;

CREATE FUNCTION reporting.server_lifecycle_engine_proof_json(p_engine jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog
AS $proof$
 SELECT jsonb_build_object('physicalNodeFingerprint',p_engine->>'physicalNodeFingerprint',
  'logicalInstanceFingerprint',p_engine->>'logicalInstanceFingerprint',
  'startupIncarnationFingerprint',p_engine->>'startupIncarnationFingerprint',
  'processId',p_engine->'processId','sourceStartLocal',p_engine->>'sourceStartLocal',
  'startMsTicks',p_engine->>'startMsTicks','currentMsTicks',p_engine->>'currentMsTicks','coverage','complete')
$proof$;

-- Entry points check native scope and revision before calling this owner-only helper.
-- Seals include every retained source receipt, even failures; no latest-state substitution.
CREATE FUNCTION reporting.server_lifecycle_history_manifest(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $manifest$
DECLARE
 v_runs jsonb; v_events jsonb; v_capture timestamptz; v_coverage jsonb;
 v_count integer; v_retained integer; v_complete integer; v_failed integer; v_lost integer;
 v_boundaries integer; v_event_count integer; v_gap boolean; v_cadence jsonb;
 v_interval interval; v_timeout interval; v_schedule_revision bigint; v_schedule_target bigint;
 v_version integer; v_enabled boolean; v_spacing_gap boolean; v_cadence_qualified boolean;
 v_epoch text;
BEGIN
 SELECT s.collection_interval,c.execution_timeout,s.schedule_revision,s.target_revision,s.collector_version,s.enabled
 INTO v_interval,v_timeout,v_schedule_revision,v_schedule_target,v_version,v_enabled
 FROM control.collector_schedule s JOIN control.collector_contract c
 ON c.collector_id=s.collector_id AND c.collector_version=s.collector_version
 WHERE s.instance_id=p_target AND s.collector_id='server.lifecycle';
 v_cadence:=jsonb_build_object('interval',v_interval,'executionTimeout',v_timeout,
  'scheduleRevision',v_schedule_revision,'targetRevision',v_schedule_target,'collectorVersion',v_version,'enabled',v_enabled);

 WITH bounded AS MATERIALIZED (
  SELECT r.run_id,r.target_revision,r.schedule_revision,r.collector_version,r.output_schema_version,r.started_at,
   o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.loss_detected,o.completion_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i
   ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='server.lifecycle'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
  ORDER BY o.completed_at,r.run_id LIMIT 10001
 ), sealed AS (
  SELECT b.*,x.source_observed_at,x.source_schema_version,x.identity_version,x.engine_coverage,
   x.comparison_boundary_reason,x.completion_digest AS receipt_digest,
   (x.run_id IS NOT NULL AND x.source_observed_at IS NOT NULL) AS retained_snapshot,
   COALESCE(x.run_id IS NOT NULL AND x.completion_digest=b.completion_digest AND x.completed_at=b.completed_at
    AND x.target_revision=b.target_revision AND x.outcome=b.outcome AND x.loss_kind=b.loss_kind
    AND b.collector_version=1 AND b.output_schema_version=1 AND x.source_schema_version=1 AND x.identity_version=1
    AND x.engine_coverage=1 AND x.payload->'truncated'='false'::jsonb
    AND (b.outcome='succeeded' AND b.loss_kind='none' OR b.outcome='partial'
     AND b.reason_code='visibility_incomplete' AND b.loss_kind='visibility_incomplete')
    AND x.source_observed_at<=b.completed_at
    AND abs(extract(epoch FROM x.source_observed_at-b.completed_at))<=300,false) AS qualified
  FROM bounded b LEFT JOIN telemetry.server_lifecycle_receipt x
   ON x.instance_id=p_target AND x.run_id=b.run_id AND x.completed_at=b.completed_at
 ), ordered AS (
  SELECT s.*,lag(s.completed_at) OVER(ORDER BY s.completed_at,s.run_id) AS prior_completed FROM sealed s
 )
 SELECT COALESCE(jsonb_agg(jsonb_build_object('runId',run_id,'targetRevision',target_revision,
  'scheduleRevision',schedule_revision,'collectorVersion',collector_version,'outputSchemaVersion',output_schema_version,
  'startedAt',started_at,'completedAt',completed_at,'outcome',outcome,'reason',reason_code,
  'lossKind',loss_kind,'lossDetected',loss_detected,'completionDigest',encode(completion_digest,'hex'),
  'receiptDigest',encode(receipt_digest,'hex'),'sourceObservedAt',source_observed_at,
  'sourceSchemaVersion',source_schema_version,'identityVersion',identity_version,'engineCoverage',engine_coverage,
  'boundary',comparison_boundary_reason,'qualified',qualified) ORDER BY completed_at,run_id),'[]'::jsonb),
  count(*)::integer,count(*) FILTER(WHERE retained_snapshot)::integer,count(*) FILTER(WHERE qualified)::integer,
  count(*) FILTER(WHERE outcome NOT IN ('succeeded','partial'))::integer,count(*) FILTER(WHERE loss_detected)::integer,
  count(*) FILTER(WHERE comparison_boundary_reason IS DISTINCT FROM 'comparable')::integer,
  COALESCE(bool_or(NOT qualified OR target_revision<>p_revision OR
   comparison_boundary_reason IS NULL OR comparison_boundary_reason NOT IN ('initial','comparable')),false),
  COALESCE(bool_and(schedule_revision=v_schedule_revision AND target_revision=v_schedule_target
   AND collector_version=v_version),false),
  COALESCE(bool_or(prior_completed IS NOT NULL AND completed_at-prior_completed>v_interval*2+v_timeout),false)
 INTO v_runs,v_count,v_retained,v_complete,v_failed,v_lost,v_boundaries,v_gap,v_cadence_qualified,v_spacing_gap
 FROM ordered;
 IF v_count>10000 THEN RAISE EXCEPTION 'lifecycle run window exceeds bounds; choose a narrower window' USING ERRCODE='54000'; END IF;

 SELECT min(o.completed_at) INTO v_capture
 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
 JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
 WHERE r.instance_id=p_target AND r.collector_id='server.lifecycle' AND o.completed_at<=p_cutoff;
 -- This is a recorded capture-attempt boundary, not proof the source was healthy.
 v_gap:=v_gap OR v_count=0 OR v_capture IS NULL OR p_from<v_capture;
 IF v_count>0 THEN
  v_gap:=v_gap OR NOT COALESCE(v_cadence_qualified AND v_enabled AND v_schedule_target=p_revision,false)
   OR v_spacing_gap
   OR (v_runs->0->>'completedAt')::timestamptz-p_from>v_interval*2+v_timeout
   OR LEAST(p_to,p_cutoff)-(v_runs->(v_count-1)->>'completedAt')::timestamptz>v_interval*2+v_timeout;
 END IF;

 SELECT COALESCE(jsonb_agg(jsonb_build_object('event',reporting.server_lifecycle_event_json(e::events.server_lifecycle_event),
  'beforeEngine',e.before_engine,'afterEngine',e.after_engine,
  'beforeCompletionDigest',encode(e.before_completion_digest,'hex'),
  'afterCompletionDigest',encode(e.after_completion_digest,'hex')) ORDER BY e.detected_at,e.event_id),'[]'::jsonb),count(*)::integer
 INTO v_events,v_event_count FROM (
  SELECT * FROM events.server_lifecycle_event e WHERE e.instance_id=p_target
   AND e.detected_at>=p_from AND e.detected_at<p_to AND e.detected_at<=p_cutoff
  ORDER BY e.detected_at,e.event_id LIMIT 10001
 ) e;
 IF v_event_count>10000 THEN RAISE EXCEPTION 'lifecycle event window exceeds bounds; choose a narrower window' USING ERRCODE='54000'; END IF;
 IF v_event_count>v_count THEN RAISE EXCEPTION 'canonical lifecycle event proof is unavailable' USING ERRCODE='55000'; END IF;
 v_coverage:=jsonb_build_object('runCount',v_count,'retainedSnapshotCount',v_retained,
  'completeRunCount',v_complete,'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,
  'lostRunCount',v_lost,'retiredRunCount',0,'comparisonBoundaryCount',v_boundaries,'eventCount',v_event_count,
  'captureBoundaryUtc',v_capture,'hasVisibilityGap',v_gap,'availabilityQualification','unqualified','sourceConsistencyVerified',false);
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('domain','sqlobserver-server-lifecycle-history-v1',
  'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',p_cutoff,
  'cadence',v_cadence,'coverage',v_coverage,'runs',v_runs,'events',v_events)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('coverage',v_coverage,'events',v_events,'readEpoch',v_epoch);
END $manifest$;

CREATE FUNCTION reporting.server_lifecycle_history_guard(p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET row_security=on
AS $guard$
DECLARE v_revision bigint;
BEGIN
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000'::uuid OR p_revision IS NULL
  OR p_revision NOT BETWEEN 1 AND 9007199254740991 OR p_from IS NULL OR p_to IS NULL
  OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '31 days'
  OR p_from<timestamptz '0001-01-01Z' OR p_to>=timestamptz '10000-01-01Z' THEN
  RAISE EXCEPTION 'lifecycle target revision or window rejected' USING ERRCODE='22023'; END IF;
 IF p_target::text IS DISTINCT FROM current_setting('sqlobserver.target_scope',true) THEN
  RAISE EXCEPTION 'lifecycle target scope rejected' USING ERRCODE='42501'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'lifecycle reads require repeatable read' USING ERRCODE='55000'; END IF;
 SELECT t.revision INTO v_revision FROM control.observation_target t WHERE t.instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN false; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'lifecycle target revision changed' USING ERRCODE='40001'; END IF;
 RETURN true;
END $guard$;

CREATE FUNCTION reporting.list_server_lifecycle_history(p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,
 p_limit integer,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $list$
DECLARE
 v_now timestamptz:=clock_timestamp(); v_cutoff timestamptz; v_issued timestamptz;
 v_after timestamptz; v_after_id uuid; v_manifest jsonb; v_coverage jsonb; v_epoch text;
 v_items jsonb; v_next jsonb; v_count integer; v_last jsonb; v_name text; v_value text;
BEGIN
 IF NOT reporting.server_lifecycle_history_guard(p_target,p_revision,p_from,p_to) THEN RETURN NULL; END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'lifecycle page bound rejected' USING ERRCODE='22023'; END IF;
 v_cutoff:=v_now; v_issued:=v_now;
 IF p_cursor IS NOT NULL THEN
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>10
   OR NOT p_cursor ?& ARRAY['targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','afterDetectedAtUtc','afterEventId'] THEN
   RAISE EXCEPTION 'lifecycle cursor shape rejected' USING ERRCODE='22023'; END IF;
  FOREACH v_name IN ARRAY ARRAY['fromUtc','toUtc','cutoffUtc','issuedAtUtc','afterDetectedAtUtc'] LOOP
   v_value:=p_cursor->>v_name;
   IF jsonb_typeof(p_cursor->v_name) IS DISTINCT FROM 'string' OR v_value !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,6}|\.\d{6}0)?(Z|\+00:00)$' THEN
    RAISE EXCEPTION 'lifecycle cursor timestamp rejected' USING ERRCODE='22023'; END IF;
  END LOOP;
  IF jsonb_typeof(p_cursor->'targetId') IS DISTINCT FROM 'string' OR jsonb_typeof(p_cursor->'afterEventId') IS DISTINCT FROM 'string'
   OR jsonb_typeof(p_cursor->'readEpoch') IS DISTINCT FROM 'string'
   OR p_cursor->>'targetRevision' !~ '^[1-9][0-9]{0,15}$' OR p_cursor->>'limit' !~ '^[1-9][0-9]{0,2}$'
   OR jsonb_typeof(p_cursor->'targetRevision') IS DISTINCT FROM 'number' OR jsonb_typeof(p_cursor->'limit') IS DISTINCT FROM 'number'
   OR p_cursor->>'readEpoch' !~ '^[0-9a-f]{64}$' THEN
   RAISE EXCEPTION 'lifecycle cursor fields rejected' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz; v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  v_after:=(p_cursor->>'afterDetectedAtUtc')::timestamptz; v_after_id:=(p_cursor->>'afterEventId')::uuid;
  IF (p_cursor->>'targetId')::uuid<>p_target OR (p_cursor->>'targetRevision')::bigint<>p_revision
   OR (p_cursor->>'fromUtc')::timestamptz<>p_from OR (p_cursor->>'toUtc')::timestamptz<>p_to
   OR (p_cursor->>'limit')::integer<>p_limit OR v_after_id='00000000-0000-0000-0000-000000000000'::uuid
   OR v_cutoff>v_issued OR v_after<p_from OR v_after>=p_to OR v_after>v_cutoff THEN
   RAISE EXCEPTION 'lifecycle cursor binding rejected' USING ERRCODE='22023'; END IF;
  IF v_issued>v_now OR v_now>=v_issued+interval '15 minutes' THEN
   RAISE EXCEPTION 'lifecycle cursor lifetime elapsed' USING ERRCODE='40001'; END IF;
 END IF;
 v_manifest:=reporting.server_lifecycle_history_manifest(p_target,p_revision,p_from,p_to,v_cutoff);
 v_coverage:=v_manifest->'coverage'; v_epoch:=v_manifest->>'readEpoch';
 IF p_cursor IS NOT NULL THEN
  IF v_epoch IS DISTINCT FROM p_cursor->>'readEpoch' THEN RAISE EXCEPTION 'retained lifecycle manifest changed' USING ERRCODE='40001'; END IF;
  IF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'events') j WHERE
   (j->'event'->>'detectedAtUtc')::timestamptz=v_after AND (j->'event'->>'eventId')::uuid=v_after_id) THEN
   RAISE EXCEPTION 'lifecycle cursor anchor unavailable' USING ERRCODE='22023'; END IF;
 END IF;
 SELECT COALESCE(jsonb_agg(e ORDER BY (e->>'detectedAtUtc')::timestamptz,(e->>'eventId')::uuid),'[]'::jsonb),count(*)::integer
 INTO v_items,v_count FROM (
  SELECT j->'event' e FROM jsonb_array_elements(v_manifest->'events') j
  WHERE v_after IS NULL OR ((j->'event'->>'detectedAtUtc')::timestamptz,(j->'event'->>'eventId')::uuid)>(v_after,v_after_id)
  ORDER BY (j->'event'->>'detectedAtUtc')::timestamptz,(j->'event'->>'eventId')::uuid LIMIT p_limit+1
 ) candidates;
 IF v_count>p_limit THEN
  v_items:=v_items-(v_count-1); v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,
   'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,
   'afterDetectedAtUtc',v_last->'detectedAtUtc','afterEventId',v_last->'eventId');
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,
  'cutoffUtc',v_cutoff,'readEpoch',v_epoch,'repositoryTimeUtc',v_now,
  'state',CASE WHEN (v_coverage->>'retainedSnapshotCount')::integer=0 THEN 'unavailable'
   WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END,
  'reason',CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_source_receipts'
   WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'source_visibility_gap' ELSE 'retained_source_coverage' END,
  'coverage',v_coverage,'items',v_items,'nextAnchor',v_next);
END $list$;

CREATE FUNCTION reporting.get_server_lifecycle_history_event(p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,
 p_event uuid,p_sample_revision bigint,p_after_run uuid,p_detected timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $exact$
DECLARE v_now timestamptz:=clock_timestamp(); v_event events.server_lifecycle_event%ROWTYPE;
 v_item jsonb; v_before jsonb; v_after jsonb; v_epoch text;
BEGIN
 IF NOT reporting.server_lifecycle_history_guard(p_target,p_revision,p_from,p_to) THEN RETURN NULL; END IF;
 IF p_event IS NULL OR p_event='00000000-0000-0000-0000-000000000000'::uuid OR p_after_run IS NULL
  OR p_after_run='00000000-0000-0000-0000-000000000000'::uuid OR p_sample_revision IS NULL
  OR p_sample_revision NOT BETWEEN 1 AND p_revision OR p_detected IS NULL OR NOT isfinite(p_detected)
  OR p_detected<p_from OR p_detected>=p_to THEN
  RAISE EXCEPTION 'exact lifecycle event tuple rejected' USING ERRCODE='22023'; END IF;
 SELECT e.* INTO v_event FROM events.server_lifecycle_event e WHERE e.instance_id=p_target
  AND e.event_id=p_event AND e.sample_target_revision=p_sample_revision AND e.after_run_id=p_after_run
  AND e.detected_at=p_detected AND e.detected_at<=v_now;
 IF FOUND THEN
  v_item:=reporting.server_lifecycle_event_json(v_event);
  v_before:=reporting.server_lifecycle_engine_proof_json(v_event.before_engine);
  v_after:=reporting.server_lifecycle_engine_proof_json(v_event.after_engine);
 END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('domain','sqlobserver-server-lifecycle-exact-v1',
  'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'eventId',p_event,
  'sampleTargetRevision',p_sample_revision,'afterRunId',p_after_run,'detectedAtUtc',p_detected,
  'event',v_item,'beforeEngine',v_before,'afterEngine',v_after)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,
  'cutoffUtc',v_now,'readEpoch',v_epoch,'repositoryTimeUtc',v_now,
  'state',CASE WHEN v_item IS NULL THEN 'unavailable' ELSE 'available' END,
  'reason',CASE WHEN v_item IS NULL THEN 'event_not_retained' ELSE 'retained_lifecycle_event' END,
  'event',v_item,'beforeEngine',v_before,'afterEngine',v_after);
END $exact$;

REVOKE ALL ON FUNCTION reporting.server_lifecycle_event_json(events.server_lifecycle_event),
 reporting.server_lifecycle_engine_proof_json(jsonb),
 reporting.server_lifecycle_history_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz),
 reporting.server_lifecycle_history_guard(uuid,bigint,timestamptz,timestamptz),
 reporting.list_server_lifecycle_history(uuid,bigint,timestamptz,timestamptz,integer,jsonb),
 reporting.get_server_lifecycle_history_event(uuid,bigint,timestamptz,timestamptz,uuid,bigint,uuid,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_lifecycle_history(uuid,bigint,timestamptz,timestamptz,integer,jsonb),
 reporting.get_server_lifecycle_history_event(uuid,bigint,timestamptz,timestamptz,uuid,bigint,uuid,timestamptz)
 TO sqlobserver_server;
