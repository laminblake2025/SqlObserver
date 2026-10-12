-- Selected-receipt SQL volume planning, with complete bounded canonical lookback accounting.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.sql_volume_planning_manifest(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $manifest$
DECLARE v_runs jsonb;v_count integer;v_volumes bigint;v_complete integer;v_failed integer;v_lost integer;
 v_missing integer;v_empty integer;v_schedule jsonb;v_policy jsonb;v_gap interval;v_boundary boolean;v_epoch text;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'SQL volume planning target scope required' USING ERRCODE='42501'; END IF;
 SELECT jsonb_build_object('targetRevision',s.target_revision,'scheduleRevision',s.schedule_revision,
  'enabled',s.enabled,'collectorVersion',s.collector_version,'interval',s.collection_interval,'timeout',c.execution_timeout),
  greatest(interval '5 minutes',s.collection_interval*2+c.execution_timeout)
 INTO v_schedule,v_gap FROM control.collector_schedule s JOIN control.collector_contract c
 ON c.collector_id=s.collector_id AND c.collector_version=s.collector_version
 WHERE s.instance_id=p_target AND s.collector_id='storage.volume';
 v_gap:=coalesce(v_gap,interval '10 minutes');
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.data_class),'[]'::jsonb) INTO v_policy
 FROM system.retention_policy p WHERE p.data_class IN('sql_volume_capacity','collection_run_canonical');
 -- Refuse the finite source bounds before constructing or hashing full row facts.
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision FROM telemetry.collection_run_v2 r
  JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='storage.volume'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR (r.run_id=p_run AND r.target_revision=p_sample_revision)) LIMIT 10001
 ), bounded_source AS MATERIALIZED(
  SELECT 1 FROM telemetry.sql_volume_snapshot s WHERE s.instance_id=p_target AND s.collected_at<=p_cutoff
   AND s.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
   AND s.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM cohort r)->>s.run_id::text)::bigint
  LIMIT 250001
 ) SELECT (SELECT count(*) FROM cohort),(SELECT count(*) FROM bounded_source) INTO v_count,v_volumes;
 IF v_count>10000 OR v_volumes>250000 THEN
  RAISE EXCEPTION 'SQL volume planning exceeds bounded retained evidence' USING ERRCODE='54000'; END IF;
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.schedule_revision,r.started_at,
   o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,o.inserted_item_count,o.source_row_count,
   o.output_item_count,o.loss_count_exact,o.truncated,o.loss_detected,encode(o.completion_digest,'hex') completion_digest,
   encode(r.request_digest,'hex') request_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='storage.volume'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR (r.run_id=p_run AND r.target_revision=p_sample_revision))
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 10001
 ), source AS MATERIALIZED(
  SELECT s.* FROM telemetry.sql_volume_snapshot s WHERE s.instance_id=p_target AND s.collected_at<=p_cutoff
   AND s.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
   AND s.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM cohort r)->>s.run_id::text)::bigint
  LIMIT 250001
 ), stored AS MATERIALIZED(
  SELECT s.run_id,count(*) volume_count,count(*) FILTER(WHERE total_bytes IS NULL OR available_bytes IS NULL) unknown_count,
   count(*) FILTER(WHERE observed_at>p_cutoff OR abs(extract(epoch FROM observed_at-collected_at))>300) skew_count,
   encode(sha256(convert_to(string_agg(encode(sha256(convert_to(to_jsonb(s)::text,'UTF8')),'hex'),''
    ORDER BY s.volume_key,s.observed_at),'UTF8')),'hex') source_digest
  FROM source s GROUP BY s.run_id
 ), accounted AS MATERIALIZED(
  SELECT r.*,coalesce(s.volume_count,0) volume_count,coalesce(s.unknown_count,0) unknown_count,
   coalesce(s.skew_count,0) skew_count,s.source_digest,
   lag(r.completed_at) OVER(PARTITION BY r.target_revision ORDER BY r.completed_at,r.run_id) prior_completed
  FROM cohort r LEFT JOIN stored s ON s.run_id=r.run_id
 ), classified AS MATERIALIZED(
  SELECT a.*,CASE WHEN outcome NOT IN('succeeded','partial') THEN 'collection_failed'
   WHEN outcome='partial' OR loss_kind IS DISTINCT FROM 'none' OR truncated OR loss_detected THEN 'collection_loss'
   WHEN collector_version<>1 OR output_schema_version<>1 THEN 'source_schema_unsupported'
   WHEN inserted_item_count>0 AND volume_count=0 THEN 'payload_missing'
   WHEN inserted_item_count<>volume_count THEN 'stored_count_mismatch'
   WHEN skew_count>0 THEN 'source_clock_skew' WHEN unknown_count>0 THEN 'capacity_unknown'
   WHEN p_run IS NULL AND (coalesce(completed_at-prior_completed>v_gap,false)
    OR schedule_revision IS DISTINCT FROM (v_schedule->>'scheduleRevision')::bigint
    OR target_revision IS DISTINCT FROM (v_schedule->>'targetRevision')::bigint) THEN 'collection_gap'
   WHEN volume_count=0 THEN 'observed_empty' ELSE 'observed_volumes' END sample_reason
  FROM accounted a
 ) SELECT count(*)::integer,coalesce(sum(volume_count),0),
  count(*) FILTER(WHERE sample_reason IN('observed_empty','observed_volumes'))::integer,
  count(*) FILTER(WHERE outcome NOT IN('succeeded','partial'))::integer,
  count(*) FILTER(WHERE loss_kind<>'none')::integer,
  count(*) FILTER(WHERE inserted_item_count>volume_count)::integer,
  count(*) FILTER(WHERE sample_reason='observed_empty')::integer,
  coalesce(count(DISTINCT target_revision)>1 OR bool_or(target_revision<>p_revision),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
   'outcome',outcome,'lossKind',loss_kind,'minimumLostItems',lost_row_count,'expectedVolumeCount',inserted_item_count,
   'storedVolumeCount',volume_count,'state',CASE WHEN sample_reason IN('observed_empty','observed_volumes') THEN 'available'
    WHEN volume_count>0 THEN 'partial' ELSE 'unavailable' END,'reason',sample_reason,
   'source',jsonb_build_object('startedAtUtc',started_at,'collectorVersion',collector_version,'schemaVersion',output_schema_version,
    'scheduleRevision',schedule_revision,'sourceRowCount',source_row_count,'outputItemCount',output_item_count,
    'lossCountExact',loss_count_exact,'truncated',truncated,'lossDetected',loss_detected,
    'completionDigest',completion_digest,'requestDigest',request_digest,'sourceDigest',source_digest))
   ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_count,v_volumes,v_complete,v_failed,v_lost,v_missing,v_empty,v_boundary,v_runs FROM classified;
 IF v_count>10000 OR v_volumes>250000 OR EXISTS(SELECT 1 FROM jsonb_array_elements(v_runs) r
  WHERE (r->>'storedVolumeCount')::integer>1000 OR (r->>'expectedVolumeCount')::integer>1000) THEN
  RAISE EXCEPTION 'SQL volume planning exceeds bounded retained evidence' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','sql-volume-planning-v1','targetId',p_target,
  'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'runId',p_run,'sampleTargetRevision',p_sample_revision,
  'runs',v_runs,'schedule',CASE WHEN p_run IS NULL THEN v_schedule END,'policies',v_policy)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,'coverage',jsonb_build_object(
  'runCount',v_count,'completeRunCount',v_complete,'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,
  'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,'emptyRunCount',v_empty,'retainedVolumeCount',v_volumes,
  'hasVisibilityGap',v_complete<>v_count OR v_boundary));
END $manifest$;

CREATE FUNCTION reporting.read_sql_volume_planning_window(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_kind text;v_revision bigint;
 v_manifest jsonb;v_epoch text;v_items jsonb;v_receipt jsonb;v_coverage jsonb;v_state text;v_reason text;
 v_before timestamptz;v_after_run uuid;v_after_key text;v_more boolean;v_last jsonb;v_next jsonb;
 v_look_manifest jsonb;v_lookback jsonb;v_look_state text;v_look_reason text;v_asof timestamptz;v_overflow boolean:=false;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'SQL volume planning scope required' USING ERRCODE='42501'; END IF;
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision IS NULL
  OR p_revision NOT BETWEEN 1 AND 9007199254740991 OR p_from IS NULL OR p_to IS NULL
  OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '31 days'
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR ((p_run IS NULL)<>(p_sample_revision IS NULL))
  OR p_run='00000000-0000-0000-0000-000000000000' OR p_sample_revision IS NOT NULL AND p_sample_revision NOT BETWEEN 1 AND p_revision
 THEN RAISE EXCEPTION 'SQL volume planning request invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'SQL volume planning requires repeatable read' USING ERRCODE='55000'; END IF;
 SELECT revision INTO v_revision FROM control.observation_target WHERE instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'SQL volume planning target changed' USING ERRCODE='40001'; END IF;
 v_kind:=CASE WHEN p_run IS NULL THEN 'receipts' ELSE 'snapshot' END;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>14
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('kind','targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','runId','sampleTargetRevision','afterCompletedAtUtc','afterRunId','afterVolumeKey'))
   OR p_cursor->>'kind' IS DISTINCT FROM v_kind OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target
   OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from
   OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit
   OR (p_cursor->>'runId')::uuid IS DISTINCT FROM p_run OR (p_cursor->>'sampleTargetRevision')::bigint IS DISTINCT FROM p_sample_revision
   OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'SQL volume planning cursor invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now THEN
   RAISE EXCEPTION 'SQL volume planning cursor timing invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'SQL volume planning cursor expired' USING ERRCODE='40001'; END IF;
  v_before:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;v_after_key:=p_cursor->>'afterVolumeKey';
  IF v_kind='receipts' AND (v_before IS NULL OR v_after_run IS NULL OR v_after_key IS NOT NULL)
   OR v_kind='snapshot' AND (v_before IS NOT NULL OR v_after_run IS NOT NULL OR coalesce(v_after_key,'') !~ '^[0-9a-f]{64}$') THEN
   RAISE EXCEPTION 'SQL volume planning anchor invalid' USING ERRCODE='22023'; END IF;
 END IF;
 v_manifest:=reporting.sql_volume_planning_manifest(p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample_revision);
 v_coverage:=v_manifest->'coverage';v_epoch:=v_manifest->>'readEpoch';
 IF v_kind='receipts' THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r
   WHERE (r->>'completedAtUtc')::timestamptz=v_before AND (r->>'runId')::uuid=v_after_run) THEN
   RAISE EXCEPTION 'SQL volume receipt anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(r-'source' ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC),'[]'::jsonb) INTO v_items
  FROM(SELECT r FROM jsonb_array_elements(v_manifest->'runs') r
   WHERE v_before IS NULL OR ((r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)<(v_before,v_after_run)
   ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC LIMIT p_limit+1) q;
  v_state:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'unavailable' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
  v_reason:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs' WHEN (v_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary'
   WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_window' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';v_asof:=(v_receipt->>'completedAtUtc')::timestamptz;
  IF v_receipt IS NOT NULL THEN
   BEGIN
    v_look_manifest:=reporting.sql_volume_planning_manifest(p_target,p_sample_revision,v_asof-interval '14 days',v_asof+interval '1 microsecond',v_cutoff);
   EXCEPTION WHEN program_limit_exceeded THEN v_overflow:=true; END;
   v_look_state:=CASE WHEN v_overflow THEN 'unavailable' WHEN v_receipt->>'state'<>'available' OR (v_look_manifest->'coverage'->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
   v_look_reason:=CASE WHEN v_overflow THEN 'lookback_overflow' WHEN v_receipt->>'state'<>'available' THEN 'selected_receipt_unavailable'
    WHEN (v_look_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary' WHEN (v_look_manifest->'coverage'->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_lookback' END;
   v_lookback:=jsonb_build_object('fromUtc',v_asof-interval '14 days','asOfUtc',v_asof,'state',v_look_state,'reason',v_look_reason,'coverage',v_look_manifest->'coverage');
   v_epoch:=encode(sha256(convert_to(jsonb_build_object('snapshotEpoch',v_epoch,'lookbackEpoch',v_look_manifest->>'readEpoch',
    'lookbackRefusal',CASE WHEN v_overflow THEN 'lookback_overflow' END)::text,'UTF8')),'hex');
  END IF;
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM telemetry.sql_volume_snapshot s WHERE s.instance_id=p_target
   AND s.run_id=p_run AND s.target_revision=p_sample_revision AND encode(s.volume_key,'hex')=v_after_key AND s.collected_at<=v_cutoff) THEN
   RAISE EXCEPTION 'SQL volume snapshot anchor unavailable' USING ERRCODE='22023'; END IF;
  WITH selected AS MATERIALIZED(
   SELECT s.* FROM telemetry.sql_volume_snapshot s WHERE s.instance_id=p_target AND s.run_id=p_run
    AND s.target_revision=p_sample_revision AND s.collected_at<=v_cutoff AND v_receipt IS NOT NULL
    AND (v_after_key IS NULL OR encode(s.volume_key,'hex')>v_after_key) ORDER BY s.volume_key LIMIT p_limit+1
  ), history AS MATERIALIZED(
   SELECT s.*,r."completedAtUtc" completed FROM jsonb_to_recordset(coalesce(v_look_manifest->'runs','[]'::jsonb))
     r("runId" uuid,"sampleTargetRevision" bigint,"completedAtUtc" timestamptz)
   JOIN telemetry.sql_volume_snapshot s ON s.run_id=r."runId" AND s.target_revision=r."sampleTargetRevision" AND s.instance_id=p_target
    AND s.collected_at<=v_cutoff WHERE s.volume_key=ANY(ARRAY(SELECT s.volume_key FROM selected s))
  ), qualified AS(
   SELECT s.*,CASE WHEN identity_kind<>'volume_id' THEN 'identity_not_comparable'
    WHEN s.total_bytes IS NULL OR s.available_bytes IS NULL THEN 'capacity_unknown'
    WHEN v_overflow THEN 'lookback_overflow' WHEN v_receipt->>'state'<>'available' THEN 'selected_receipt_unavailable'
    WHEN (v_look_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary'
    WHEN (v_look_manifest->'coverage'->>'hasVisibilityGap')::boolean THEN 'visibility_gaps'
    WHEN EXISTS(SELECT 1 FROM history h WHERE h.volume_key=s.volume_key AND h.identity_kind<>'volume_id') THEN 'identity_not_comparable'
    WHEN EXISTS(SELECT 1 FROM history h WHERE h.volume_key=s.volume_key AND h.total_bytes IS DISTINCT FROM s.total_bytes) THEN 'capacity_changed'
    WHEN (SELECT count(*) FROM history h WHERE h.volume_key=s.volume_key)<>(v_look_manifest->'coverage'->>'runCount')::integer THEN 'visibility_gaps'
    ELSE 'eligible' END planning_state
   FROM selected s
  ), projected AS(
   SELECT s.volume_key,jsonb_build_object('volumeKey',encode(s.volume_key,'hex'),'identityKind',s.identity_kind,
    'mappedFileCount',s.mapped_file_count,'observedAtUtc',s.observed_at,'collectedAtUtc',s.collected_at,
    'totalBytes',s.total_bytes::text,'availableBytes',s.available_bytes::text,'forecast',jsonb_build_object('state',s.planning_state,
     'sampleDays',CASE WHEN s.planning_state='eligible' THEN jsonb_array_length(d.samples) ELSE 0 END,'daysUntilFull',NULL,
     'dailySamples',CASE WHEN s.planning_state='eligible' THEN d.samples ELSE '[]'::jsonb END)) item
   FROM qualified s LEFT JOIN LATERAL(
    SELECT coalesce(jsonb_agg(jsonb_build_object('runId',h.run_id,'completedAtUtc',h.completed,'totalBytes',h.total_bytes::text,
     'availableBytes',h.available_bytes::text) ORDER BY h.completed,h.run_id),'[]'::jsonb) samples
    FROM(SELECT DISTINCT ON((h.completed AT TIME ZONE 'UTC')::date) h.* FROM history h WHERE h.volume_key=s.volume_key
     ORDER BY (h.completed AT TIME ZONE 'UTC')::date,h.completed DESC,h.run_id DESC) h
   ) d ON true
  ) SELECT coalesce(jsonb_agg(item ORDER BY volume_key),'[]'::jsonb) INTO v_items FROM projected;
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','snapshot_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i
   ON i.run_id=r.run_id AND i.started_at=r.started_at JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
   WHERE r.instance_id=p_target AND r.run_id=p_run AND r.target_revision=p_sample_revision AND r.collector_id='storage.volume'
    AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN v_reason:='snapshot_outside_window'; END IF;
 END IF;
 IF p_cursor IS NOT NULL AND v_epoch IS DISTINCT FROM p_cursor->>'readEpoch' THEN
  RAISE EXCEPTION 'SQL volume planning source changed' USING ERRCODE='40001'; END IF;
 v_more:=jsonb_array_length(v_items)>p_limit;
 IF v_more THEN v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('kind',v_kind,'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,
   'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,'runId',p_run,'sampleTargetRevision',p_sample_revision,
   'afterCompletedAtUtc',CASE WHEN v_kind='receipts' THEN v_last->>'completedAtUtc' END,
   'afterRunId',CASE WHEN v_kind='receipts' THEN v_last->>'runId' END,'afterVolumeKey',CASE WHEN v_kind='snapshot' THEN v_last->>'volumeKey' END);
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_epoch,'repositoryTimeUtc',v_now,'state',v_state,'reason',v_reason,'items',v_items,'nextAnchor',v_next)
  || CASE WHEN v_kind='receipts' THEN jsonb_build_object('coverage',v_coverage)
   ELSE jsonb_build_object('runId',p_run,'sampleTargetRevision',p_sample_revision,'receipt',v_receipt,'lookback',v_lookback) END;
END $read$;
REVOKE ALL ON FUNCTION reporting.sql_volume_planning_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.read_sql_volume_planning_window(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_volume_planning_window(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;

-- Compatibility source stays observation-based; only canonical run admission changes.
CREATE OR REPLACE FUNCTION reporting.list_sql_volume_capacity_history(
    p_instance_id uuid, p_target_revision bigint, p_snapshot_run_id uuid,
    p_volume_keys bytea[])
RETURNS TABLE(volume_key text, observed_at timestamptz,
    total_bytes bigint, available_bytes bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
DECLARE
    anchor_time timestamptz;
BEGIN
    IF p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision < 1
       OR p_snapshot_run_id IS NULL OR p_volume_keys IS NULL
       OR cardinality(p_volume_keys) NOT BETWEEN 1 AND 100
       OR EXISTS (SELECT 1 FROM unnest(p_volume_keys) AS keys(value)
                  WHERE keys.value IS NULL OR octet_length(keys.value) <> 32)
       OR (SELECT count(DISTINCT keys.value) FROM unnest(p_volume_keys) AS keys(value))
          <> cardinality(p_volume_keys)
    THEN RAISE EXCEPTION 'SQL volume history bounds are invalid' USING ERRCODE='22023'; END IF;
    IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_instance_id::text
    THEN RAISE EXCEPTION 'SQL volume target scope is required' USING ERRCODE='42501'; END IF;
    IF NOT EXISTS (SELECT 1 FROM control.observation_target AS target
                   WHERE target.instance_id=p_instance_id AND target.revision=p_target_revision)
    THEN RAISE EXCEPTION 'SQL volume target revision changed' USING ERRCODE='22023'; END IF;

    SELECT outcome.completed_at INTO anchor_time
    FROM telemetry.collection_run_v2 AS run
    JOIN telemetry.collection_run_identity AS identity ON identity.run_id=run.run_id AND identity.started_at=run.started_at
    JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
    WHERE run.run_id=p_snapshot_run_id AND run.instance_id=p_instance_id
      AND run.target_revision=p_target_revision AND run.collector_id='storage.volume'
      AND outcome.outcome='succeeded' AND outcome.loss_kind='none';
    IF anchor_time IS NULL THEN RETURN; END IF;

    RETURN QUERY
    WITH daily AS (
        SELECT DISTINCT ON (sample.volume_key,
                            (sample.observed_at AT TIME ZONE 'UTC')::date)
               sample.volume_key, sample.observed_at,
               sample.total_bytes, sample.available_bytes
        FROM telemetry.sql_volume_snapshot AS sample
        JOIN telemetry.collection_run_v2 AS run ON run.run_id=sample.run_id
        JOIN telemetry.collection_run_identity AS identity ON identity.run_id=run.run_id AND identity.started_at=run.started_at
        JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
        WHERE sample.instance_id=p_instance_id
          AND sample.target_revision=p_target_revision
          AND sample.volume_key=ANY(p_volume_keys)
          AND sample.observed_at BETWEEN anchor_time-interval '14 days' AND anchor_time
          AND run.instance_id=p_instance_id AND run.target_revision=p_target_revision
          AND run.collector_id='storage.volume'
          AND outcome.outcome='succeeded' AND outcome.loss_kind='none'
          AND sample.total_bytes IS NOT NULL AND sample.available_bytes IS NOT NULL
        ORDER BY sample.volume_key,
                 (sample.observed_at AT TIME ZONE 'UTC')::date,
                 sample.observed_at DESC, sample.run_id DESC
    )
    SELECT encode(daily.volume_key, 'hex'), daily.observed_at,
           daily.total_bytes, daily.available_bytes
    FROM daily
    ORDER BY daily.volume_key, daily.observed_at;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_sql_volume_capacity_history(
    uuid,bigint,uuid,bytea[]) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_sql_volume_capacity_history(
    uuid,bigint,uuid,bytea[]) TO sqlobserver_server;
