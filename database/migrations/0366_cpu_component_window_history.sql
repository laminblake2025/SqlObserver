-- Scoped retained CPU component series and independent exact observation reads.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.cpu_component_public_observation(p_value jsonb,p_target uuid,p_revision bigint,p_run uuid)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog SET TimeZone='UTC'
AS $public$
BEGIN
 PERFORM control.validate_cpu_component_observation(p_value,p_target,p_revision,p_run);
 RETURN jsonb_build_object('sourceSchemaVersion',p_value->'sourceSchemaVersion','state',p_value->'state','reason',p_value->'reason',
  'sourceAssociationVerified',p_value->'sourceAssociationVerified','associationReason',p_value->'associationReason',
  'exactPairEligible',p_value->'exactPairEligible',
  'sqlProcess',(p_value->'sqlProcess')-'rawBefore'-'rawAfter',
  'operatingSystem',(p_value->'operatingSystem')-'rawBefore'-'rawAfter');
EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'CPU retained observation violates its immutable contract' USING ERRCODE='XX001';
END $public$;

CREATE FUNCTION reporting.cpu_component_checked_observation(p_payload jsonb,p_target uuid,p_revision bigint,p_run uuid,
 p_outcome text,p_reason text,p_frames integer,p_loss text,p_has boolean,p_digest bytea,p_canonical_digest bytea)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $checked$
BEGIN
 IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_payload))<>9 OR
  NOT p_payload ?& ARRAY['schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceFrames','truncated','observation'] OR
  p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb OR p_payload->>'targetId' IS DISTINCT FROM p_target::text OR
  p_payload->'targetRevision' IS DISTINCT FROM to_jsonb(p_revision) OR p_payload->>'runId' IS DISTINCT FROM p_run::text OR
  p_payload->>'outcome' IS DISTINCT FROM p_outcome OR p_payload->>'reason' IS DISTINCT FROM p_reason OR
  p_payload->'sourceFrames' IS DISTINCT FROM to_jsonb(p_frames) OR p_payload->'truncated' IS DISTINCT FROM 'false'::jsonb OR
  p_digest IS DISTINCT FROM p_canonical_digest OR p_digest IS DISTINCT FROM sha256(convert_to(p_payload::text,'UTF8')) OR
  p_has IS DISTINCT FROM (p_outcome='succeeded') OR p_has IS DISTINCT FROM (p_payload->'observation'<>'null'::jsonb) OR
  p_has AND (p_loss<>'none' OR p_payload#>'{observation,sourceFrames}' IS DISTINCT FROM to_jsonb(p_frames)) THEN
  RAISE EXCEPTION 'CPU retained completion integrity invalid' USING ERRCODE='XX001'; END IF;
 IF p_has THEN RETURN reporting.cpu_component_public_observation(p_payload->'observation',p_target,p_revision,p_run); END IF;
 RETURN NULL;
END $checked$;

CREATE FUNCTION reporting.cpu_component_window_manifest(
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
 WHERE s.instance_id=p_target AND s.collector_id='cpu.components';
 -- Configured policy is epoch provenance, never a guarantee that any payload still exists.
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.data_class),'[]'::jsonb) INTO v_policies
 FROM system.retention_policy p WHERE p.data_class='cpu_components' OR p.parent_table='collection_run_v2';
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.started_at,r.schedule_revision,
   encode(r.request_digest,'hex') request_digest,o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,
   o.source_row_count,o.output_item_count,o.inserted_item_count,o.truncated,o.loss_detected,o.loss_count_exact,
   encode(o.completion_digest,'hex') completion_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='cpu.components'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR (r.run_id=p_run AND r.target_revision=p_sample_revision))
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 10001
 ), stored AS MATERIALIZED(
  SELECT c.* FROM telemetry.cpu_component_receipt c WHERE c.instance_id=p_target AND c.completed_at<=p_cutoff
   AND c.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
 ), accounted AS MATERIALIZED(
  SELECT r.*,c.run_id IS NOT NULL retained,c.payload#>>'{observation,state}' observation_state,
   c.payload#>>'{observation,reason}' observation_reason,encode(c.completion_digest,'hex') receipt_digest,
   c.has_capture AND jsonb_typeof(c.payload->'observation')='object' observation_present,
   CASE WHEN c.run_id IS NOT NULL THEN reporting.cpu_component_checked_observation(c.payload,p_target,r.target_revision,r.run_id,r.outcome,r.reason_code,r.source_row_count,r.loss_kind,c.has_capture,c.completion_digest,decode(r.completion_digest,'hex')) END observation
  FROM cohort r LEFT JOIN stored c ON c.run_id=r.run_id AND c.target_revision=r.target_revision AND c.completed_at=r.completed_at
 ), classified AS MATERIALIZED(
  SELECT a.*,coalesce(observation_present,false) has_observation,
   outcome='succeeded' AND loss_kind='none' AND coalesce(observation_present,false)
    AND observation_state='available' AND collector_version=1 AND output_schema_version=1 complete,
   CASE WHEN outcome<>'succeeded' THEN 'collection_failed'
    WHEN NOT coalesce(observation_present,false) THEN 'payload_missing'
    ELSE observation_reason END sample_reason
  FROM accounted a
 ) SELECT count(*)::integer,count(*) FILTER(WHERE retained)::integer,count(*) FILTER(WHERE complete)::integer,
  count(*) FILTER(WHERE outcome<>'succeeded')::integer,count(*) FILTER(WHERE loss_kind<>'none')::integer,
  count(*) FILTER(WHERE outcome='succeeded' AND NOT has_observation)::integer,
  count(DISTINCT target_revision)>1 OR coalesce(bool_or(target_revision<>p_revision),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
   'outcome',outcome,'lossKind',loss_kind,'minimumLostItems',lost_row_count,'observationPresent',has_observation,'observation',observation,
   'state',CASE WHEN has_observation THEN observation_state ELSE 'unavailable' END,'reason',sample_reason,
   'source',jsonb_build_object('startedAtUtc',started_at,'collectorVersion',collector_version,'schemaVersion',output_schema_version,
     'scheduleRevision',schedule_revision,'canonicalReason',reason_code,'sourceRowCount',source_row_count,
     'outputItemCount',output_item_count,'insertedItemCount',inserted_item_count,'truncated',truncated,
     'lossDetected',loss_detected,'lossCountExact',loss_count_exact,'completionDigest',completion_digest,
     'requestDigest',request_digest,'retainedReceipt',retained,'receiptDigest',receipt_digest))
   ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_count,v_retained,v_complete,v_failed,v_lost,v_missing,v_boundary,v_runs FROM classified;
 IF v_count>10000 THEN RAISE EXCEPTION 'CPU capture window exceeds bounded retained attempts; choose a narrower window' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','cpu-component-window-v1','targetId',p_target,
  'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'runId',p_run,'sampleTargetRevision',p_sample_revision,
  'runs',v_runs,'schedule',v_schedule,'policies',v_policies)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,
  'coverage',jsonb_build_object('runCount',v_count,'retainedReceiptCount',v_retained,'completeRunCount',v_complete,
   'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,
   'hasVisibilityGap',v_count<>v_complete OR v_boundary));
END $manifest$;

CREATE FUNCTION reporting.read_cpu_component_window_history(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_revision bigint;
 v_manifest jsonb;v_coverage jsonb;v_items jsonb;v_receipt jsonb;v_capture jsonb;v_next jsonb;v_last jsonb;
 v_after_completed timestamptz;v_after_run uuid;v_state text;v_reason text;v_stored telemetry.cpu_component_receipt%ROWTYPE;v_frames integer;
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
 v_manifest:=reporting.cpu_component_window_manifest(p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample_revision);
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
    WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_observations' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','observation_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
   JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id WHERE r.instance_id=p_target AND r.collector_id='cpu.components'
    AND r.run_id=p_run AND r.target_revision=p_sample_revision AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN
   v_reason:='observation_outside_window'; END IF;
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_manifest->>'readEpoch','repositoryTimeUtc',v_now,'state',v_state,'reason',v_reason)
  || CASE WHEN p_run IS NULL THEN jsonb_build_object('coverage',v_coverage,'items',v_items,'nextAnchor',v_next)
     ELSE jsonb_build_object('runId',p_run,'sampleTargetRevision',p_sample_revision,'receipt',v_receipt) END;
END $read$;

REVOKE ALL ON FUNCTION reporting.cpu_component_checked_observation(jsonb,uuid,bigint,uuid,text,text,integer,text,boolean,bytea,bytea),
 reporting.cpu_component_public_observation(jsonb,uuid,bigint,uuid),
 reporting.cpu_component_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint),
 reporting.read_cpu_component_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_cpu_component_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;
