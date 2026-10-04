-- Read-only history of staged captures. No source registration, scheduling or admission.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Explicit whitelist. Source UTC/local strings are copied, never rounded through timestamptz.
-- Signed raw counter/exit bits are text; exit FILETIME is not a liveness assertion.
CREATE FUNCTION reporting.cpu_capture_public_part(p_kind text,p_value jsonb)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog
AS $part$
BEGIN
 IF p_value IS NULL OR p_value='null'::jsonb THEN RETURN NULL; END IF;
 CASE p_kind
 WHEN 'counter' THEN RETURN jsonb_build_object('counterType',p_value->'counterType','cStatus',p_value->'cStatus',
  'firstValue',p_value->>'firstValue','secondValue',p_value->>'secondValue','collectedLocalFileTime100ns',p_value->>'collectedLocalFileTime100ns',
  'multiCount',p_value->'multiCount','apiStatus',p_value->'apiStatus','timeBase',p_value->>'timeBase');
 WHEN 'process' THEN RETURN jsonb_build_object('creationBefore100ns',p_value->>'creationBefore100ns',
  'creationAfter100ns',p_value->>'creationAfter100ns','exitAfter100ns',p_value->>'exitAfter100ns',
  'apiStatus',p_value->'apiStatus','waitResultAfter',p_value->'waitResultAfter');
 WHEN 'sql' THEN RETURN jsonb_build_object('sourceObservedAtUtc',p_value->>'sourceObservedAtUtc',
  'ordinal',p_value->'ordinal','sourceSchemaVersion',p_value->'sourceSchemaVersion','processId',p_value->'engine'->'processId',
  'sourceStartLocal',p_value->'engine'->>'sourceStartLocal','startMsTicks',p_value->'engine'->>'startMsTicks',
  'currentMsTicks',p_value->'engine'->>'currentMsTicks','processUserTimeMs',p_value->>'processUserTimeMs',
  'processKernelTimeMs',p_value->>'processKernelTimeMs','sqlReportedLogicalProcessors',p_value->'sqlReportedLogicalProcessors');
 WHEN 'tcp' THEN RETURN jsonb_build_object('observedAtUtc',p_value->>'observedAtUtc','monotonic100ns',p_value->>'monotonic100ns',
  'af4Status',p_value->'af4Status','af6Status',p_value->'af6Status','rowCount4',p_value->'rowCount4','rowCount6',p_value->'rowCount6',
  'matchingRowCount',jsonb_array_length(p_value->'matchingRows'),
  'sqlProcess',reporting.cpu_capture_public_part('process',p_value->'sqlProcess'),
  'collectorProcess',reporting.cpu_capture_public_part('process',p_value->'collectorProcess'));
 WHEN 'cpu' THEN RETURN jsonb_build_object('observedAtUtc',p_value->>'observedAtUtc','monotonicTicks',p_value->>'monotonicTicks',
  'monotonicFrequency',p_value->>'monotonicFrequency','sqlProcessCreationFileTime100ns',p_value->>'sqlProcessCreationFileTime100ns',
  'logicalProcessorCount',p_value->'logicalProcessorCount','processCounter',reporting.cpu_capture_public_part('counter',p_value->'processCounter'),
  'osCounter',reporting.cpu_capture_public_part('counter',p_value->'osCounter'));
 WHEN 'frame' THEN RETURN jsonb_build_object('processCreationBefore100ns',p_value->>'processCreationBefore100ns',
  'processCreationAfter100ns',p_value->>'processCreationAfter100ns','processExitAfter100ns',p_value->>'processExitAfter100ns',
  'logicalProcessorsBefore',p_value->'logicalProcessorsBefore','logicalProcessorsAfter',p_value->'logicalProcessorsAfter',
  'processInstances',p_value->'processInstances','processMappingAmbiguous',p_value->'processMappingAmbiguous','processWaitResultAfter',p_value->'processWaitResultAfter');
 WHEN 'eligibility' THEN RETURN jsonb_build_object('sqlProcessEligible',p_value->'sqlProcessEligible','operatingSystemEligible',p_value->'operatingSystemEligible',
  'sameNativeInterval',p_value->'sameNativeInterval',
  'sqlProcessReason',(ARRAY['None','Baseline','TargetBoundary','RevisionBoundary','RunBoundary','QueryBoundary','SourceQualificationAbsent',
   'HostBindingMismatch','MachineBoundary','BootBoundary','ProcessRestart','ProcessorTopologyChanged','ClockBoundary','ClockSkew',
   'SourceUnavailable','CounterStatusInvalid','CounterTypeMismatch','CounterTimeBaseMismatch','CounterReset','DenominatorInvalid','IntervalMismatch','CounterRangeInvalid'])[(p_value->>'sqlProcessReason')::integer+1],
  'operatingSystemReason',(ARRAY['None','Baseline','TargetBoundary','RevisionBoundary','RunBoundary','QueryBoundary','SourceQualificationAbsent',
   'HostBindingMismatch','MachineBoundary','BootBoundary','ProcessRestart','ProcessorTopologyChanged','ClockBoundary','ClockSkew',
   'SourceUnavailable','CounterStatusInvalid','CounterTypeMismatch','CounterTimeBaseMismatch','CounterReset','DenominatorInvalid','IntervalMismatch','CounterRangeInvalid'])[(p_value->>'operatingSystemReason')::integer+1],
  'sqlFromNative100ns',p_value->>'sqlFromNative100ns','sqlToNative100ns',p_value->>'sqlToNative100ns',
  'osFromNative100ns',p_value->>'osFromNative100ns','osToNative100ns',p_value->>'osToNative100ns');
 ELSE RAISE EXCEPTION 'unknown CPU capture projection' USING ERRCODE='22023';
 END CASE;
END $part$;

CREATE FUNCTION reporting.cpu_capture_public_detail(p_capture jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog
AS $detail$
 SELECT CASE WHEN p_capture IS NULL OR p_capture='null'::jsonb THEN NULL ELSE jsonb_build_object(
 'sourceSchemaVersion',p_capture->'sourceSchemaVersion','state',CASE WHEN p_capture->>'state'='1' THEN 'partial' ELSE 'unavailable' END,
 'reason',(ARRAY['source_qualification_absent','host_binding_unavailable','host_binding_denied','host_binding_boundary',
  'host_cpu_capability_unavailable','native_capture_incomplete','candidate_association_rejected','numerical_evidence_incomplete'])[(p_capture->>'reason')::integer+1],
 'candidateReason',(ARRAY['None','TargetBoundary','RevisionBoundary','RunBoundary','ConnectionBoundary','SqlSourceIncomplete','SqlEngineBoundary',
  'SourceSchemaBoundary','UnsupportedTransport','SocketWitnessIncomplete','UnsupportedAddress','TupleChanged','CollectorProcessChanged','QueryBoundary',
  'HostWitnessIncomplete','HostBindingMismatch','MachineBoundary','BootBoundary','NativeClockBoundary','CaptureDoesNotBracketCpu','CaptureWindowExceeded',
  'NativeTableUnavailable','ProcessUnavailable','ProcessExited','ProcessLifetimeUnknown','ProcessRestart','ServerSocketMissing','ReverseSocketMissing',
  'SocketNotEstablished','SocketOwnerMismatch','SocketAmbiguous','NativeCaptureIncomplete'])[(p_capture->>'candidateReason')::integer+1],
 'candidateLocalAssociation',p_capture->'candidateLocalAssociation',
 'sourceFrames',(CASE WHEN p_capture->'sqlBefore'<>'null'::jsonb THEN 1 ELSE 0 END)+(CASE WHEN p_capture->'sqlAfter'<>'null'::jsonb THEN 1 ELSE 0 END)
  +(CASE WHEN p_capture->'tcpBefore'<>'null'::jsonb THEN 1 ELSE 0 END)+(CASE WHEN p_capture->'tcpAfter'<>'null'::jsonb THEN 1 ELSE 0 END)
  +(CASE WHEN p_capture->'cpuBefore'<>'null'::jsonb THEN 1 ELSE 0 END)+(CASE WHEN p_capture->'cpuAfter'<>'null'::jsonb THEN 1 ELSE 0 END),
 'sqlBefore',reporting.cpu_capture_public_part('sql',p_capture->'sqlBefore'),'sqlAfter',reporting.cpu_capture_public_part('sql',p_capture->'sqlAfter'),
 'tcpBefore',reporting.cpu_capture_public_part('tcp',p_capture->'tcpBefore'),'tcpAfter',reporting.cpu_capture_public_part('tcp',p_capture->'tcpAfter'),
 'cpuBefore',reporting.cpu_capture_public_part('cpu',p_capture->'cpuBefore'),'cpuAfter',reporting.cpu_capture_public_part('cpu',p_capture->'cpuAfter'),
 'idProcessBefore',reporting.cpu_capture_public_part('counter',p_capture->'idProcessBefore'),'idProcessAfter',reporting.cpu_capture_public_part('counter',p_capture->'idProcessAfter'),
 'frameBefore',reporting.cpu_capture_public_part('frame',p_capture->'frameBefore'),'frameAfter',reporting.cpu_capture_public_part('frame',p_capture->'frameAfter'),
 'processStatusBefore',p_capture->'processStatusBefore','processStatusAfter',p_capture->'processStatusAfter',
 'osStatusBefore',p_capture->'osStatusBefore','osStatusAfter',p_capture->'osStatusAfter',
 'tcpBeforeReadReason',p_capture->'tcpBeforeReadReason','cpuPairReadReason',p_capture->'cpuPairReadReason',
 'tcpAfterReadReason',p_capture->'tcpAfterReadReason','processReadReason',p_capture->'processReadReason',
 'numericalEligibility',reporting.cpu_capture_public_part('eligibility',p_capture->'numericalEligibility'),
 'bytes',jsonb_build_object('tcpBeforeHelperBytes',p_capture->'bytes'->'tcpBeforeHelperBytes','cpuPairHelperBytes',p_capture->'bytes'->'cpuPairHelperBytes',
  'tcpAfterHelperBytes',p_capture->'bytes'->'tcpAfterHelperBytes','sqlLogicalResponseUpperBoundBytes',p_capture->'bytes'->>'sqlLogicalResponseUpperBoundBytes'),
 'nativeQualification','absent','targetComparisonAdmitted',false,'hardwareIdentityVerified',false,'sqlProcessPercent',NULL,'operatingSystemPercent',NULL) END;
$detail$;

CREATE FUNCTION reporting.cpu_capture_window_manifest(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $manifest$
DECLARE v_runs jsonb;v_count integer;v_retained integer;v_complete integer;v_failed integer;v_lost integer;
 v_missing integer;v_boundary boolean;v_schedule jsonb;v_policies jsonb;v_epoch text;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'CPU capture target scope required' USING ERRCODE='42501'; END IF;
 SELECT jsonb_build_object('targetRevision',s.target_revision,'scheduleRevision',s.schedule_revision,
   'enabled',s.enabled,'collectorVersion',s.collector_version,'interval',s.collection_interval) INTO v_schedule FROM control.collector_schedule s
 WHERE s.instance_id=p_target AND s.collector_id='cpu.paired-capture';
 -- Configured policy is epoch provenance, never a guarantee that any payload still exists.
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.data_class),'[]'::jsonb) INTO v_policies
 FROM system.retention_policy p WHERE p.data_class='cpu_paired_captures' OR p.parent_table='collection_run_v2';
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.started_at,r.schedule_revision,
   encode(r.request_digest,'hex') request_digest,o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,
   o.source_row_count,o.output_item_count,o.inserted_item_count,o.truncated,o.loss_detected,o.loss_count_exact,
   encode(o.completion_digest,'hex') completion_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='cpu.paired-capture'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR (r.run_id=p_run AND r.target_revision=p_sample_revision))
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 10001
 ), stored AS MATERIALIZED(
  SELECT c.* FROM telemetry.cpu_paired_capture_receipt c WHERE c.instance_id=p_target AND c.completed_at<=p_cutoff
   AND c.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
 ), accounted AS MATERIALIZED(
  SELECT r.*,c.run_id IS NOT NULL retained,c.payload#>>'{capture,state}' capture_state,c.payload#>>'{capture,reason}' capture_reason,
   CASE WHEN c.has_capture THEN (c.payload->>'sourceFrames')::integer END source_frames,
   encode(c.completion_digest,'hex') receipt_digest,
   c.has_capture AND jsonb_typeof(c.payload->'capture')='object' capture_present
  FROM cohort r LEFT JOIN stored c ON c.run_id=r.run_id AND c.target_revision=r.target_revision AND c.completed_at=r.completed_at
 ), classified AS MATERIALIZED(
  SELECT a.*,coalesce(capture_present,false) has_capture,
   outcome='succeeded' AND loss_kind='none' AND coalesce(capture_present,false)
    AND source_frames=6 AND capture_state='1' AND collector_version=1 AND output_schema_version=1 complete,
   CASE WHEN outcome<>'succeeded' THEN 'collection_failed'
    WHEN NOT coalesce(capture_present,false) THEN 'payload_missing'
    ELSE (ARRAY['source_qualification_absent','host_binding_unavailable','host_binding_denied','host_binding_boundary',
      'host_cpu_capability_unavailable','native_capture_incomplete','candidate_association_rejected','numerical_evidence_incomplete'])[capture_reason::integer+1] END sample_reason
  FROM accounted a
 ) SELECT count(*)::integer,count(*) FILTER(WHERE retained)::integer,count(*) FILTER(WHERE complete)::integer,
  count(*) FILTER(WHERE outcome<>'succeeded')::integer,count(*) FILTER(WHERE loss_kind<>'none')::integer,
  count(*) FILTER(WHERE outcome='succeeded' AND NOT has_capture)::integer,
  count(DISTINCT target_revision)>1 OR coalesce(bool_or(target_revision<>p_revision),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
   'outcome',outcome,'lossKind',loss_kind,'minimumLostItems',lost_row_count,'capturePresent',has_capture,'sourceFrames',source_frames,
   'state',CASE WHEN has_capture AND source_frames=6 THEN 'partial' ELSE 'unavailable' END,'reason',sample_reason,
   'source',jsonb_build_object('startedAtUtc',started_at,'collectorVersion',collector_version,'schemaVersion',output_schema_version,
     'scheduleRevision',schedule_revision,'canonicalReason',reason_code,'sourceRowCount',source_row_count,
     'outputItemCount',output_item_count,'insertedItemCount',inserted_item_count,'truncated',truncated,
     'lossDetected',loss_detected,'lossCountExact',loss_count_exact,'completionDigest',completion_digest,
     'requestDigest',request_digest,'retainedReceipt',retained,'receiptDigest',receipt_digest))
   ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_count,v_retained,v_complete,v_failed,v_lost,v_missing,v_boundary,v_runs FROM classified;
 IF v_count>10000 THEN RAISE EXCEPTION 'CPU capture window exceeds bounded retained attempts; choose a narrower window' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','cpu-capture-window-v1','targetId',p_target,
  'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'runId',p_run,'sampleTargetRevision',p_sample_revision,
  'runs',v_runs,'schedule',v_schedule,'policies',v_policies)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,
  'coverage',jsonb_build_object('runCount',v_count,'retainedReceiptCount',v_retained,'completeRunCount',v_complete,
   'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,
   'hasVisibilityGap',v_count<>v_complete OR v_boundary));
END $manifest$;

CREATE FUNCTION reporting.read_cpu_capture_window_history(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_revision bigint;
 v_manifest jsonb;v_coverage jsonb;v_items jsonb;v_receipt jsonb;v_capture jsonb;v_next jsonb;v_last jsonb;
 v_after_completed timestamptz;v_after_run uuid;v_state text;v_reason text;v_stored telemetry.cpu_paired_capture_receipt%ROWTYPE;v_frames integer;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'CPU capture target scope required' USING ERRCODE='42501'; END IF;
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision IS NULL OR p_revision NOT BETWEEN 1 AND 9007199254740991
  OR p_from IS NULL OR p_to IS NULL OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '31 days'
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR ((p_run IS NULL)<>(p_sample_revision IS NULL))
  OR (p_run IS NOT NULL AND (p_run='00000000-0000-0000-0000-000000000000' OR p_sample_revision IS NULL OR p_sample_revision NOT BETWEEN 1 AND p_revision OR p_cursor IS NOT NULL))
 THEN RAISE EXCEPTION 'CPU capture window request invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'CPU capture history requires repeatable read' USING ERRCODE='55000'; END IF;
 SELECT revision INTO v_revision FROM control.observation_target WHERE instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'CPU capture target revision changed' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>10
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','afterCompletedAtUtc','afterRunId'))
   OR EXISTS(SELECT 1 FROM jsonb_each(p_cursor) x WHERE jsonb_typeof(x.value) IS DISTINCT FROM
     (CASE WHEN x.key IN('targetRevision','limit') THEN 'number' ELSE 'string' END))
   OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision
   OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to
   OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'CPU capture continuation binding invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  v_after_completed:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now
   OR v_after_completed IS NULL OR NOT isfinite(v_after_completed) OR v_after_completed<p_from OR v_after_completed>=p_to
   OR v_after_completed>v_cutoff OR v_after_run IS NULL OR v_after_run='00000000-0000-0000-0000-000000000000'
  THEN RAISE EXCEPTION 'CPU capture continuation clock or anchor invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'CPU capture continuation expired' USING ERRCODE='40001'; END IF;
 END IF;
 v_manifest:=reporting.cpu_capture_window_manifest(p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample_revision);
 v_coverage:=v_manifest->'coverage';
 IF p_cursor IS NOT NULL AND v_manifest->>'readEpoch' IS DISTINCT FROM p_cursor->>'readEpoch' THEN
  RAISE EXCEPTION 'CPU capture retained manifest changed' USING ERRCODE='40001'; END IF;
 IF p_run IS NULL THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r
    WHERE (r->>'completedAtUtc')::timestamptz=v_after_completed AND (r->>'runId')::uuid=v_after_run) THEN
   RAISE EXCEPTION 'CPU capture continuation anchor unavailable' USING ERRCODE='22023'; END IF;
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
  v_state:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'unavailable' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
  v_reason:=CASE WHEN (v_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary' WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs'
    WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_captures' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';
  SELECT c.* INTO v_stored FROM telemetry.cpu_paired_capture_receipt c
   WHERE c.instance_id=p_target AND c.run_id=p_run AND c.target_revision=p_sample_revision
    AND c.completed_at=(v_receipt->>'completedAtUtc')::timestamptz AND c.completed_at<=v_cutoff AND v_receipt IS NOT NULL;
  IF FOUND THEN
   BEGIN
    IF jsonb_typeof(v_stored.payload) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(v_stored.payload))<>9
     OR NOT v_stored.payload ?& ARRAY['schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceFrames','truncated','capture']
     OR v_stored.payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb OR v_stored.payload->>'targetId' IS DISTINCT FROM p_target::text
     OR v_stored.payload->'targetRevision' IS DISTINCT FROM to_jsonb(p_sample_revision) OR v_stored.payload->>'runId' IS DISTINCT FROM p_run::text
     OR v_stored.payload->>'outcome' IS DISTINCT FROM v_receipt->>'outcome' OR v_stored.outcome IS DISTINCT FROM v_receipt->>'outcome'
     OR v_stored.payload->>'reason' IS DISTINCT FROM v_manifest->'runs'->0->'source'->>'canonicalReason'
     OR jsonb_typeof(v_stored.payload->'sourceFrames') IS DISTINCT FROM 'number' OR v_stored.payload->>'sourceFrames' !~ '^[0-6]$'
     OR v_stored.loss_kind IS DISTINCT FROM v_receipt->>'lossKind' OR v_stored.payload->'truncated' IS DISTINCT FROM 'false'::jsonb
     OR v_stored.native_qualification IS DISTINCT FROM 'absent' OR v_stored.target_comparison_admitted
     OR v_stored.sql_process_percent IS NOT NULL OR v_stored.operating_system_percent IS NOT NULL
     OR v_stored.completion_digest IS DISTINCT FROM sha256(convert_to(v_stored.payload::text,'UTF8'))
     OR encode(v_stored.completion_digest,'hex') IS DISTINCT FROM v_manifest->'runs'->0->'source'->>'completionDigest'
     OR v_stored.has_capture IS DISTINCT FROM (v_stored.payload->'capture'<>'null'::jsonb)
     OR v_stored.has_capture IS DISTINCT FROM (v_stored.outcome='succeeded')
    THEN RAISE EXCEPTION 'CPU stored wrapper contract invalid' USING ERRCODE='22023'; END IF;
    IF v_stored.has_capture THEN
     IF v_stored.outcome<>'succeeded' OR v_stored.loss_kind<>'none' THEN RAISE EXCEPTION 'CPU stored outcome contract invalid' USING ERRCODE='22023'; END IF;
     PERFORM control.validate_cpu_capture_json(v_stored.payload->'capture','capture',p_target,p_sample_revision,p_run);
     v_capture:=reporting.cpu_capture_public_detail(v_stored.payload->'capture');v_frames:=(v_capture->>'sourceFrames')::integer;
     IF v_stored.payload->'sourceFrames' IS DISTINCT FROM to_jsonb(v_frames)
      OR v_stored.payload->'capture'->'hostBindingRevision' IS DISTINCT FROM v_stored.payload->'capture'->'hostProfileRevision'
      OR v_stored.payload->'capture'->'state' IS DISTINCT FROM to_jsonb(CASE WHEN v_frames=6 THEN 1 ELSE 0 END)
      OR v_stored.payload->'capture'->'candidateLocalAssociation' IS DISTINCT FROM to_jsonb(v_stored.payload->'capture'->>'candidateReason'='0')
      OR v_stored.payload->'capture'->>'candidateReason'='0' AND v_frames<>6
      OR v_stored.payload->'capture'->'numericalEligibility'<>'null'::jsonb AND v_stored.payload->'capture'->>'candidateReason'<>'0'
      OR v_stored.payload->'capture'->'sqlBefore'<>'null'::jsonb AND v_stored.payload->'capture'->'sqlBefore'->'ordinal'<>'1'::jsonb
      OR v_stored.payload->'capture'->'sqlAfter'<>'null'::jsonb AND v_stored.payload->'capture'->'sqlAfter'->'ordinal'<>'2'::jsonb
     THEN RAISE EXCEPTION 'CPU stored frame contract invalid' USING ERRCODE='22023'; END IF;
    END IF;
   EXCEPTION WHEN query_canceled THEN RAISE;
    WHEN OTHERS THEN
    RAISE EXCEPTION 'CPU retained evidence violates its immutable contract' USING ERRCODE='XX001';
   END;
  END IF;
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','capture_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
   JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id WHERE r.instance_id=p_target AND r.collector_id='cpu.paired-capture'
    AND r.run_id=p_run AND r.target_revision=p_sample_revision AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN
   v_reason:='capture_outside_window'; END IF;
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_manifest->>'readEpoch','repositoryTimeUtc',v_now,'state',v_state,'reason',v_reason,
  'nativeQualification','absent','targetComparisonAdmitted',false,'hardwareIdentityVerified',false,'sqlProcessPercent',NULL,'operatingSystemPercent',NULL)
  || CASE WHEN p_run IS NULL THEN jsonb_build_object('coverage',v_coverage,'items',v_items,'nextAnchor',v_next)
     ELSE jsonb_build_object('runId',p_run,'sampleTargetRevision',p_sample_revision,'receipt',v_receipt,'capture',v_capture) END;
END $read$;

REVOKE ALL ON FUNCTION reporting.cpu_capture_public_part(text,jsonb),reporting.cpu_capture_public_detail(jsonb),
 reporting.cpu_capture_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint),
 reporting.read_cpu_capture_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_cpu_capture_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;
