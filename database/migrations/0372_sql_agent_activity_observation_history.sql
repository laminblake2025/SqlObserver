-- Scoped retained Agent source history. Lists contain only bounded receipt metadata.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.sql_agent_activity_gap(p_value jsonb)
RETURNS text LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $gap$
DECLARE b jsonb:=p_value->'before';a jsonb:=p_value->'after';
BEGIN
 IF (p_value->>'truncated')::boolean THEN RETURN 'SourceTruncated'; END IF;
 IF b#>>'{service,matchingServiceCount}'='0' OR a#>>'{service,matchingServiceCount}'='0' THEN RETURN 'ServiceMissing'; END IF;
 IF b#>>'{service,matchingServiceCount}'<>'1' OR a#>>'{service,matchingServiceCount}'<>'1' THEN RETURN 'ServiceAmbiguous'; END IF;
 IF b#>'{service,status}'='null'::jsonb OR a#>'{service,status}'='null'::jsonb THEN RETURN 'ServiceStateUnavailable'; END IF;
 IF b#>>'{service,status}'<>'4' OR a#>>'{service,status}'<>'4' THEN RETURN 'ServiceNotRunning'; END IF;
 IF coalesce((b#>>'{service,processId}')::integer,0)=0 OR coalesce((a#>>'{service,processId}')::integer,0)=0
  OR b#>'{service,startupAtUtc}'='null'::jsonb OR a#>'{service,startupAtUtc}'='null'::jsonb THEN RETURN 'ServiceEpochUnavailable'; END IF;
 IF b#>'{service,processId}' IS DISTINCT FROM a#>'{service,processId}' OR b#>'{service,startupAtUtc}' IS DISTINCT FROM a#>'{service,startupAtUtc}' THEN RETURN 'ServiceEpochChanged'; END IF;
 IF control.agent_activity_utc_ticks(b#>>'{service,startupAtUtc}')>control.agent_activity_utc_ticks(b->>'sourceObservedAtUtc')
  OR control.agent_activity_utc_ticks(a#>>'{service,startupAtUtc}')>control.agent_activity_utc_ticks(a->>'sourceObservedAtUtc') THEN RETURN 'ServiceClockBoundary'; END IF;
 IF b#>'{session,sessionId}'='null'::jsonb OR a#>'{session,sessionId}'='null'::jsonb
  OR b#>'{session,agentStartLocal}'='null'::jsonb OR a#>'{session,agentStartLocal}'='null'::jsonb THEN RETURN 'SessionUnavailable'; END IF;
 IF b->'session' IS DISTINCT FROM a->'session' THEN RETURN 'SessionChanged'; END IF;
 RETURN 'None';
END $gap$;

CREATE FUNCTION reporting.sql_agent_activity_public_header(p_value jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $header$
 SELECT jsonb_build_object('sourceObservedAtUtc',p_value->>'sourceObservedAtUtc',
  'service',jsonb_build_object('matchingServiceCount',p_value#>'{service,matchingServiceCount}','status',p_value#>'{service,status}',
   'processId',p_value#>'{service,processId}','startupAtUtc',p_value#>>'{service,startupAtUtc}'),
  'session',jsonb_build_object('sessionId',p_value#>'{session,sessionId}','agentStartLocal',p_value#>>'{session,agentStartLocal}'));
$header$;
CREATE FUNCTION reporting.sql_agent_activity_public_detail(p_value jsonb)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $detail$
DECLARE v_gap text:=reporting.sql_agent_activity_gap(p_value);v_jobs jsonb;
BEGIN
 SELECT coalesce(jsonb_agg(jsonb_build_object('jobId',j->>'jobId','sessionId',j->'sessionId',
  'requestedLocal',j->>'requestedLocal','queuedLocal',j->>'queuedLocal','startExecutionLocal',j->>'startExecutionLocal',
  'stopExecutionLocal',j->>'stopExecutionLocal','lastExecutedStepId',j->'lastExecutedStepId','lastExecutedStepLocal',j->>'lastExecutedStepLocal',
  'candidateState',CASE WHEN v_gap<>'None' THEN 'Unknown'
   WHEN j->'startExecutionLocal'<>'null'::jsonb AND j->'stopExecutionLocal'='null'::jsonb THEN 'Running'
   WHEN j->'startExecutionLocal'='null'::jsonb AND j->'stopExecutionLocal'='null'::jsonb
    AND (j->'requestedLocal'<>'null'::jsonb OR j->'queuedLocal'<>'null'::jsonb) THEN 'Queued'
   WHEN j->'startExecutionLocal'<>'null'::jsonb AND j->'stopExecutionLocal'<>'null'::jsonb THEN 'Stopped' ELSE 'Unknown' END)
  ORDER BY (j->>'jobId') COLLATE "C"),'[]'::jsonb) INTO v_jobs FROM jsonb_array_elements(p_value->'jobs') j;
 RETURN jsonb_build_object('sourceSchemaVersion',1,'enrollmentRevision',p_value->'enrollmentRevision',
  'before',reporting.sql_agent_activity_public_header(p_value->'before'),'after',reporting.sql_agent_activity_public_header(p_value->'after'),
  'jobs',v_jobs,'sourceRowsRead',p_value->'sourceRowsRead','truncated',p_value->'truncated','gap',v_gap,
  'productionAdmitted',false,'serviceSessionAssociationQualified',false,'nativeQualification','Absent');
END $detail$;

-- Validate immutable envelopes against the canonical completion before either summary or detail is served.
-- Return metadata only: no source jobs or private enrollment identity enter the window manifest.
CREATE FUNCTION reporting.sql_agent_activity_checked_receipt(p_stored telemetry.sql_agent_activity_receipt,p_canonical jsonb,p_full boolean)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $checked$
DECLARE v jsonb:=p_stored.payload;o jsonb:=v->'observation';has_source boolean:=p_stored.outcome IN('succeeded','partial');h jsonb;k text;
BEGIN
 IF NOT control.agent_activity_object_shape(v,ARRAY['schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceRowsRead','truncated','observation'])
  OR octet_length(v::text)>530000 OR v->'schemaVersion' IS DISTINCT FROM '1'::jsonb OR p_stored.source_schema_version<>1
  OR v->>'targetId' IS DISTINCT FROM p_stored.instance_id::text OR v->'targetRevision' IS DISTINCT FROM to_jsonb(p_stored.target_revision)
  OR v->>'runId' IS DISTINCT FROM p_stored.run_id::text OR v->>'outcome' IS DISTINCT FROM p_canonical->>'outcome'
  OR p_stored.outcome IS DISTINCT FROM p_canonical->>'outcome' OR p_stored.loss_kind IS DISTINCT FROM p_canonical->>'loss_kind'
  OR v->>'reason' IS DISTINCT FROM p_canonical->>'reason_code' OR v->'sourceRowsRead' IS DISTINCT FROM p_canonical->'source_row_count'
  OR v->'truncated' IS DISTINCT FROM p_canonical->'truncated' OR p_stored.has_capture IS DISTINCT FROM has_source
  OR p_stored.has_capture IS DISTINCT FROM (jsonb_typeof(o)='object')
  OR p_stored.completion_digest IS DISTINCT FROM sha256(convert_to(v::text,'UTF8'))
  OR encode(p_stored.completion_digest,'hex') IS DISTINCT FROM p_canonical->>'completion_digest'
  OR p_canonical->'collector_version' IS DISTINCT FROM '1'::jsonb OR p_canonical->'output_schema_version' IS DISTINCT FROM '1'::jsonb
 THEN RAISE EXCEPTION 'Agent retained receipt wrapper invalid' USING ERRCODE='22023'; END IF;
 IF has_source THEN
  -- Summaries disclose no jobs. The digest matches the immutable canonical
  -- completion originally validated by0370; per-job validation is exact-only.
  IF p_full THEN
   PERFORM control.validate_sql_agent_activity_observation(o,p_stored.instance_id,p_stored.target_revision,p_stored.run_id);
  ELSE
   IF NOT control.agent_activity_object_shape(o,ARRAY['sourceSchemaVersion','enrollmentRevision','enrollmentId','evidenceSha256','before','after','jobs',
    'sourceRowsRead','truncated','productionAdmitted','serviceSessionAssociationQualified','nativeQualification'])
    OR o->'sourceSchemaVersion' IS DISTINCT FROM '1'::jsonb OR octet_length(o::text)>524288
    OR NOT control.agent_activity_integer(o->'enrollmentRevision',1,9007199254740991)
    OR o->'productionAdmitted' IS DISTINCT FROM 'false'::jsonb OR o->'serviceSessionAssociationQualified' IS DISTINCT FROM 'false'::jsonb
    OR o->'nativeQualification' IS DISTINCT FROM '"Absent"'::jsonb OR jsonb_typeof(o->'jobs') IS DISTINCT FROM 'array'
    OR NOT control.agent_activity_integer(o->'sourceRowsRead',0,513)
    OR jsonb_array_length(o->'jobs')<>least((o->>'sourceRowsRead')::integer,512)
   THEN RAISE EXCEPTION 'Agent summary source wrapper invalid' USING ERRCODE='22023'; END IF;
   FOREACH k IN ARRAY ARRAY['before','after'] LOOP
    h:=o->k;
    IF NOT control.agent_activity_object_shape(h,ARRAY['sourceObservedAtUtc','service','session'])
     OR h->'sourceObservedAtUtc'='null'::jsonb OR NOT control.agent_activity_date(h->'sourceObservedAtUtc',false)
     OR NOT control.agent_activity_object_shape(h->'service',ARRAY['matchingServiceCount','status','processId','startupAtUtc'])
     OR NOT control.agent_activity_object_shape(h->'session',ARRAY['sessionId','agentStartLocal']) THEN
     RAISE EXCEPTION 'Agent summary header invalid' USING ERRCODE='22023'; END IF;
   END LOOP;
  END IF;
  IF v->'sourceRowsRead' IS DISTINCT FROM o->'sourceRowsRead' OR v->'truncated' IS DISTINCT FROM o->'truncated'
   OR p_canonical->'output_item_count' IS DISTINCT FROM '1'::jsonb OR p_canonical->'inserted_item_count' IS DISTINCT FROM '1'::jsonb
   OR p_canonical->'response_bytes' IS DISTINCT FROM to_jsonb(4096+(o->>'sourceRowsRead')::integer*768)
   OR p_canonical->'output_bytes' IS DISTINCT FROM to_jsonb(4096+jsonb_array_length(o->'jobs')*768)
   OR p_canonical->'persisted_bytes' IS DISTINCT FROM to_jsonb(4096+jsonb_array_length(o->'jobs')*768)
   OR p_stored.outcome='succeeded' AND (p_stored.loss_kind<>'none' OR v->>'reason'<>'completed' OR o->'truncated'<>'false'::jsonb)
   OR p_stored.outcome='partial' AND (p_stored.loss_kind<>'source_row_limit' OR v->>'reason'<>'source_row_limit' OR o->'truncated'<>'true'::jsonb)
  THEN RAISE EXCEPTION 'Agent retained source accounting invalid' USING ERRCODE='22023'; END IF;
 ELSE
  IF o IS DISTINCT FROM 'null'::jsonb OR v->'sourceRowsRead' IS DISTINCT FROM '0'::jsonb OR v->'truncated' IS DISTINCT FROM 'false'::jsonb
   OR p_canonical->'output_item_count' IS DISTINCT FROM '0'::jsonb OR p_canonical->'inserted_item_count' IS DISTINCT FROM '0'::jsonb
   OR p_canonical->'response_bytes' IS DISTINCT FROM '0'::jsonb OR p_canonical->'output_bytes' IS DISTINCT FROM '0'::jsonb
   OR p_canonical->'persisted_bytes' IS DISTINCT FROM '0'::jsonb THEN
   RAISE EXCEPTION 'Agent failure fabricates source' USING ERRCODE='22023'; END IF;
 END IF;
 RETURN jsonb_build_object('capturePresent',has_source,'sourceRowsRead',CASE WHEN has_source THEN o->'sourceRowsRead' END,
  'retainedJobCount',CASE WHEN has_source THEN jsonb_array_length(o->'jobs') END,'truncated',CASE WHEN has_source THEN o->'truncated' END);
EXCEPTION WHEN query_canceled THEN RAISE;
 WHEN OTHERS THEN RAISE EXCEPTION 'Agent retained evidence violates its immutable contract' USING ERRCODE='XX001';
END $checked$;

-- Whole-window accounting reads scalar columns only. Toasted source payloads are
-- checked for the emitted page or selected exact run, never the entire cohort.
CREATE FUNCTION reporting.sql_agent_activity_checked_metadata(p_schema integer,p_outcome text,p_loss text,p_capture boolean,p_digest bytea,p_canonical jsonb)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $metadata$
DECLARE has_source boolean:=p_outcome IN('succeeded','partial');n integer:=(p_canonical->>'source_row_count')::integer;
BEGIN
 IF p_schema IS DISTINCT FROM 1 OR p_outcome IS DISTINCT FROM p_canonical->>'outcome' OR p_loss IS DISTINCT FROM p_canonical->>'loss_kind'
  OR p_capture IS DISTINCT FROM has_source OR encode(p_digest,'hex') IS DISTINCT FROM p_canonical->>'completion_digest'
  OR p_canonical->'collector_version' IS DISTINCT FROM '1'::jsonb OR p_canonical->'output_schema_version' IS DISTINCT FROM '1'::jsonb
  OR n NOT BETWEEN 0 AND 513 OR has_source AND (p_canonical->'truncated' IS DISTINCT FROM to_jsonb(n>512)
   OR p_canonical->'output_item_count' IS DISTINCT FROM '1'::jsonb OR p_canonical->'inserted_item_count' IS DISTINCT FROM '1'::jsonb)
  OR p_outcome='succeeded' AND (n>512 OR p_loss<>'none' OR p_canonical->>'reason_code'<>'completed')
  OR p_outcome='partial' AND (n<>513 OR p_loss<>'source_row_limit' OR p_canonical->>'reason_code'<>'source_row_limit')
  OR NOT has_source AND (n<>0 OR p_canonical->'truncated' IS DISTINCT FROM 'false'::jsonb
   OR p_canonical->'output_item_count' IS DISTINCT FROM '0'::jsonb OR p_canonical->'inserted_item_count' IS DISTINCT FROM '0'::jsonb)
 THEN RAISE EXCEPTION 'Agent retained metadata contradicts canonical completion' USING ERRCODE='XX001'; END IF;
 RETURN jsonb_build_object('capturePresent',has_source,'sourceRowsRead',CASE WHEN has_source THEN n END,
  'retainedJobCount',CASE WHEN has_source THEN least(n,512) END,'truncated',CASE WHEN has_source THEN p_canonical->'truncated' END);
END $metadata$;

CREATE FUNCTION reporting.sql_agent_activity_validate_selected(p_target uuid,p_revision bigint,p_run uuid,p_completed timestamptz,p_full boolean)
RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog SET row_security=on AS $selected$
DECLARE v_stored telemetry.sql_agent_activity_receipt%ROWTYPE;v_canonical jsonb;
BEGIN
 SELECT c.* INTO v_stored FROM telemetry.sql_agent_activity_receipt c
  WHERE c.instance_id=p_target AND c.target_revision=p_revision AND c.run_id=p_run AND c.completed_at=p_completed;
 IF NOT FOUND THEN RETURN; END IF;
 SELECT to_jsonb(o)||jsonb_build_object('completion_digest',encode(o.completion_digest,'hex'),
  'collector_version',r.collector_version,'output_schema_version',r.output_schema_version) INTO v_canonical
 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
 JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
 WHERE r.instance_id=p_target AND r.target_revision=p_revision AND r.run_id=p_run
  AND r.collector_id='sql-agent.activity-observations' AND o.completed_at=p_completed;
 IF NOT FOUND THEN RAISE EXCEPTION 'Agent retained receipt has no canonical completion' USING ERRCODE='XX001'; END IF;
 PERFORM reporting.sql_agent_activity_checked_receipt(v_stored,v_canonical,p_full);
END $selected$;

CREATE FUNCTION reporting.sql_agent_activity_window_manifest(p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,
 p_cutoff timestamptz,p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $manifest$
DECLARE v_runs jsonb;v_count integer;v_retained integer;v_capture integer;v_complete integer;v_failed integer;v_lost integer;
 v_missing integer;v_boundary boolean;v_schedule jsonb;v_policies jsonb;v_retirement jsonb;v_enrollment bigint;v_epoch text;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'Agent history target scope required' USING ERRCODE='42501'; END IF;
 SELECT jsonb_build_object('targetRevision',s.target_revision,'scheduleRevision',s.schedule_revision,'enabled',s.enabled,
  'collectorVersion',s.collector_version,'interval',s.collection_interval) INTO v_schedule FROM control.collector_schedule s
  WHERE s.instance_id=p_target AND s.collector_id='sql-agent.activity-observations';
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.data_class),'[]'::jsonb) INTO v_policies FROM system.retention_policy p
  WHERE p.data_class='sql_agent_activity_observations' OR p.parent_table='collection_run_v2';
 SELECT coalesce(max(command_revision),0) INTO v_enrollment FROM control.sql_agent_module_enrollment_command
  WHERE instance_id=p_target AND target_revision=p_revision;
 -- Retirement batches bind the epoch but cannot prove membership of a selected deleted run.
 SELECT jsonb_build_object('count',count(*),'lastRetiredAt',max(retired_at)) INTO v_retirement
  FROM system.collection_run_retirement WHERE instance_id=p_target;
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.started_at,r.schedule_revision,
   encode(r.request_digest,'hex') request_digest,o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,
   o.source_row_count,o.output_item_count,o.inserted_item_count,o.truncated,o.loss_detected,o.loss_count_exact,
   o.response_bytes,o.output_bytes,o.persisted_bytes,o.attempt_count,o.retry_count,o.duration_ms,o.duplicate_item_count,o.rejected_item_count,o.lost_byte_count,
   encode(o.completion_digest,'hex') completion_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
   JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='sql-agent.activity-observations'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR (r.run_id=p_run AND r.target_revision=p_sample_revision))
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 10001
 ), accounted AS MATERIALIZED(
  SELECT r.*,c.run_id IS NOT NULL retained,encode(c.completion_digest,'hex') receipt_digest,
   CASE WHEN c.run_id IS NOT NULL THEN reporting.sql_agent_activity_checked_metadata(c.source_schema_version,c.outcome,c.loss_kind,c.has_capture,c.completion_digest,to_jsonb(r))
    ELSE jsonb_build_object('capturePresent',false,'sourceRowsRead',NULL,'retainedJobCount',NULL,'truncated',NULL) END evidence
  FROM cohort r LEFT JOIN telemetry.sql_agent_activity_receipt c
   ON c.instance_id=p_target AND c.run_id=r.run_id AND c.target_revision=r.target_revision AND c.completed_at=r.completed_at
 ), classified AS MATERIALIZED(
  SELECT a.*,(evidence->>'capturePresent')::boolean has_capture,
   outcome='succeeded' AND loss_kind='none' AND (evidence->>'capturePresent')::boolean complete,
   CASE WHEN outcome NOT IN('succeeded','partial') THEN 'collection_failed'
    WHEN NOT (evidence->>'capturePresent')::boolean THEN 'payload_missing'
    WHEN outcome='partial' THEN 'source_truncated' ELSE 'source_complete' END sample_reason FROM accounted a
 ) SELECT count(*)::integer,count(*) FILTER(WHERE retained)::integer,count(*) FILTER(WHERE has_capture)::integer,
  count(*) FILTER(WHERE complete)::integer,count(*) FILTER(WHERE outcome NOT IN('succeeded','partial'))::integer,
  count(*) FILTER(WHERE loss_kind<>'none')::integer,count(*) FILTER(WHERE outcome IN('succeeded','partial') AND NOT has_capture)::integer,
  count(DISTINCT target_revision)>1 OR coalesce(bool_or(target_revision<>p_revision),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
   'outcome',outcome,'canonicalReason',reason_code,'lossKind',loss_kind,'minimumLostItems',lost_row_count,'countIsExact',loss_count_exact,
   'receiptRetained',retained,'capturePresent',has_capture,'sourceRowsRead',evidence->'sourceRowsRead',
   'retainedJobCount',evidence->'retainedJobCount','truncated',evidence->'truncated',
   'state',CASE WHEN NOT has_capture THEN 'unavailable' WHEN outcome='partial' THEN 'partial' ELSE 'available' END,'reason',sample_reason,
   'source',jsonb_build_object('startedAtUtc',started_at,'collectorVersion',collector_version,'schemaVersion',output_schema_version,
    'scheduleRevision',schedule_revision,'canonicalReason',reason_code,'sourceRowCount',source_row_count,'outputItemCount',output_item_count,
    'insertedItemCount',inserted_item_count,'truncated',truncated,'lossDetected',loss_detected,'lossCountExact',loss_count_exact,
    'responseBytes',response_bytes,'outputBytes',output_bytes,'persistedBytes',persisted_bytes,'attemptCount',attempt_count,'retryCount',retry_count,
    'durationMs',duration_ms,'duplicateItemCount',duplicate_item_count,'rejectedItemCount',rejected_item_count,'lostByteCount',lost_byte_count,
    'completionDigest',completion_digest,'requestDigest',request_digest,'receiptDigest',receipt_digest))
   ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_count,v_retained,v_capture,v_complete,v_failed,v_lost,v_missing,v_boundary,v_runs FROM classified;
 IF v_count>10000 THEN RAISE EXCEPTION 'Agent history window exceeds bounded retained attempts' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','agent-activity-window-v1','targetId',p_target,'targetRevision',p_revision,
  'fromUtc',p_from,'toUtc',p_to,'runId',p_run,'sampleTargetRevision',p_sample_revision,'runs',v_runs,'schedule',v_schedule,
  'policies',v_policies,'enrollmentRevision',v_enrollment,'retirement',v_retirement)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,'coverage',jsonb_build_object(
  'runCount',v_count,'retainedReceiptCount',v_retained,'retainedCaptureCount',v_capture,'completeSourceRunCount',v_complete,
  'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,
  'hasVisibilityGap',v_count<>v_complete OR v_boundary));
END $manifest$;

CREATE FUNCTION reporting.read_sql_agent_activity_window_history(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_revision bigint;
 v_manifest jsonb;v_coverage jsonb;v_items jsonb;v_receipt jsonb;v_capture jsonb;v_next jsonb;v_last jsonb;
 v_after_completed timestamptz;v_after_run uuid;v_state text;v_reason text;v_stored telemetry.sql_agent_activity_receipt%ROWTYPE;
 v_item jsonb;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'Agent activity target scope required' USING ERRCODE='42501'; END IF;
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision IS NULL OR p_revision NOT BETWEEN 1 AND 9007199254740991
  OR p_from IS NULL OR p_to IS NULL OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '31 days'
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR ((p_run IS NULL)<>(p_sample_revision IS NULL))
  OR (p_run IS NOT NULL AND (p_run='00000000-0000-0000-0000-000000000000' OR p_sample_revision IS NULL OR p_sample_revision NOT BETWEEN 1 AND p_revision OR p_cursor IS NOT NULL))
 THEN RAISE EXCEPTION 'Agent activity window request invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'Agent activity history requires repeatable read' USING ERRCODE='55000'; END IF;
 SELECT revision INTO v_revision FROM control.observation_target WHERE instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'Agent activity target revision changed' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>10
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','afterCompletedAtUtc','afterRunId'))
   OR EXISTS(SELECT 1 FROM jsonb_each(p_cursor) x WHERE jsonb_typeof(x.value) IS DISTINCT FROM
     (CASE WHEN x.key IN('targetRevision','limit') THEN 'number' ELSE 'string' END))
   OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision
   OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to
   OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'Agent activity continuation binding invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  v_after_completed:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now
   OR v_after_completed IS NULL OR NOT isfinite(v_after_completed) OR v_after_completed<p_from OR v_after_completed>=p_to
   OR v_after_completed>v_cutoff OR v_after_run IS NULL OR v_after_run='00000000-0000-0000-0000-000000000000'
  THEN RAISE EXCEPTION 'Agent activity continuation clock or anchor invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'Agent activity continuation expired' USING ERRCODE='40001'; END IF;
 END IF;
 v_manifest:=reporting.sql_agent_activity_window_manifest(p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample_revision);
 v_coverage:=v_manifest->'coverage';
 IF p_cursor IS NOT NULL AND v_manifest->>'readEpoch' IS DISTINCT FROM p_cursor->>'readEpoch' THEN
  RAISE EXCEPTION 'Agent activity retained manifest changed' USING ERRCODE='40001'; END IF;
 IF p_run IS NULL THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r
    WHERE (r->>'completedAtUtc')::timestamptz=v_after_completed AND (r->>'runId')::uuid=v_after_run) THEN
   RAISE EXCEPTION 'Agent activity continuation anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(r-'source' ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC),'[]'::jsonb) INTO v_items
  FROM(SELECT r FROM jsonb_array_elements(v_manifest->'runs') r WHERE v_after_completed IS NULL
   OR ((r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)<(v_after_completed,v_after_run)
   ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC LIMIT p_limit+1) page;
  IF jsonb_array_length(v_items)>p_limit THEN
   v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
   v_next:=jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
    'issuedAtUtc',v_issued,'readEpoch',v_manifest->>'readEpoch','limit',p_limit,
    'afterCompletedAtUtc',v_last->>'completedAtUtc','afterRunId',v_last->>'runId');
  END IF;
  FOR v_item IN SELECT value FROM jsonb_array_elements(v_items) LOOP
   PERFORM reporting.sql_agent_activity_validate_selected(p_target,(v_item->>'sampleTargetRevision')::bigint,
    (v_item->>'runId')::uuid,(v_item->>'completedAtUtc')::timestamptz,false);
  END LOOP;
  v_state:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'unavailable' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
  v_reason:=CASE WHEN (v_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary' WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs'
    WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_observations' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';
  IF v_receipt IS NOT NULL THEN
   PERFORM reporting.sql_agent_activity_validate_selected(p_target,p_sample_revision,p_run,(v_receipt->>'completedAtUtc')::timestamptz,true);
  END IF;
  SELECT c.* INTO v_stored FROM telemetry.sql_agent_activity_receipt c
   WHERE c.instance_id=p_target AND c.run_id=p_run AND c.target_revision=p_sample_revision
    AND c.completed_at=(v_receipt->>'completedAtUtc')::timestamptz AND c.completed_at<=v_cutoff AND v_receipt IS NOT NULL;
  IF FOUND AND v_stored.has_capture THEN
   -- The selected receipt has been fully checked against its canonical completion.
   v_capture:=reporting.sql_agent_activity_public_detail(v_stored.payload->'observation');
  END IF;
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','observation_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
   JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id WHERE r.instance_id=p_target AND r.collector_id='sql-agent.activity-observations'
    AND r.run_id=p_run AND r.target_revision=p_sample_revision AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN
   v_reason:='observation_outside_window'; END IF;
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_manifest->>'readEpoch','repositoryTimeUtc',v_now,'state',v_state,'reason',v_reason,
  'nativeQualification','Absent','productionAdmitted',false,'serviceSessionAssociationQualified',false)
  || CASE WHEN p_run IS NULL THEN jsonb_build_object('coverage',v_coverage,'items',v_items,'nextAnchor',v_next)
     ELSE jsonb_build_object('runId',p_run,'sampleTargetRevision',p_sample_revision,'receipt',v_receipt,'capture',v_capture) END;
END $read$;

REVOKE ALL ON FUNCTION reporting.sql_agent_activity_gap(jsonb),reporting.sql_agent_activity_public_header(jsonb),
 reporting.sql_agent_activity_public_detail(jsonb),reporting.sql_agent_activity_checked_receipt(telemetry.sql_agent_activity_receipt,jsonb,boolean),
 reporting.sql_agent_activity_checked_metadata(integer,text,text,boolean,bytea,jsonb),
 reporting.sql_agent_activity_validate_selected(uuid,bigint,uuid,timestamptz,boolean),
 reporting.sql_agent_activity_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint),
 reporting.read_sql_agent_activity_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_agent_activity_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;
