-- Canonical completion-window TempDB reads; summary and files are one immutable source run.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.tempdb_window_manifest(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $manifest$
DECLARE v_runs jsonb;v_run_count integer;v_files bigint;v_complete integer;v_failed integer;v_lost integer;
 v_missing integer;v_empty integer;v_schedule jsonb;v_policy jsonb;v_gap interval;v_epoch text;v_boundary boolean;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'TempDB target scope required' USING ERRCODE='42501'; END IF;
 SELECT jsonb_build_object('targetRevision',s.target_revision,'scheduleRevision',s.schedule_revision,
  'enabled',s.enabled,'collectorVersion',s.collector_version,'interval',s.collection_interval,'timeout',c.execution_timeout),
  greatest(interval '5 minutes',s.collection_interval*2+c.execution_timeout)
 INTO v_schedule,v_gap FROM control.collector_schedule s JOIN control.collector_contract c
 ON c.collector_id=s.collector_id AND c.collector_version=s.collector_version
 WHERE s.instance_id=p_target AND s.collector_id='tempdb.health';
 v_gap:=coalesce(v_gap,interval '10 minutes');
 -- Configured policy is provenance, not a promise that a particular source remains retained.
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.data_class),'[]'::jsonb) INTO v_policy
 FROM system.retention_policy p WHERE p.parent_table IN('tempdb_snapshot','tempdb_file_snapshot','collection_run_v2');
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.schedule_revision,r.started_at,
   o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,o.inserted_item_count,o.source_row_count,
   o.output_item_count,o.loss_count_exact,o.truncated,o.loss_detected,encode(o.completion_digest,'hex') completion_digest,
   encode(r.request_digest,'hex') request_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='tempdb.health'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR (r.run_id=p_run AND r.target_revision=p_sample_revision))
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 10001
 ), summaries AS MATERIALIZED(
  SELECT s.* FROM telemetry.tempdb_snapshot s WHERE s.instance_id=p_target
   AND s.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
   AND s.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM cohort r)->>s.run_id::text)::bigint
 ), source AS MATERIALIZED(
  SELECT f.* FROM telemetry.tempdb_file_snapshot f WHERE f.instance_id=p_target
   AND f.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
   AND f.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM cohort r)->>f.run_id::text)::bigint
  LIMIT 250001
 ), stored AS MATERIALIZED(
  SELECT f.run_id,count(*) file_count,min(f.observed_at) first_file_observed,max(f.observed_at) last_file_observed,
   encode(sha256(convert_to(string_agg(encode(sha256(convert_to(to_jsonb(f)::text,'UTF8')),'hex'),'' ORDER BY f.file_id,f.observed_at),'UTF8')),'hex') file_digest
  FROM source f GROUP BY f.run_id
 ), summary_account AS MATERIALIZED(
  SELECT s.run_id,count(*) summary_count,min(s.observed_at) observed_at,min(s.state) source_state,bool_or(s.truncated) summary_truncated,
   encode(sha256(convert_to(string_agg(encode(sha256(convert_to(to_jsonb(s)::text,'UTF8')),'hex'),'' ORDER BY s.observed_at),'UTF8')),'hex') summary_digest
  FROM summaries s GROUP BY s.run_id
 ), accounted AS MATERIALIZED(
  SELECT r.*,coalesce(f.file_count,0) file_count,coalesce(s.summary_count,0) summary_count,s.observed_at,s.source_state,
   coalesce(s.summary_truncated,false) summary_truncated,s.summary_digest,f.file_digest,f.first_file_observed,f.last_file_observed,
   lag(r.completed_at) OVER(PARTITION BY r.target_revision ORDER BY r.completed_at,r.run_id) prior_completed
  FROM cohort r LEFT JOIN stored f ON f.run_id=r.run_id LEFT JOIN summary_account s ON s.run_id=r.run_id
 ), classified AS MATERIALIZED(
  SELECT a.*,CASE WHEN outcome NOT IN('succeeded','partial') THEN 'collection_failed'
   WHEN outcome='partial' OR loss_kind<>'none' OR truncated OR loss_detected OR summary_truncated THEN 'collection_loss'
   WHEN collector_version<>1 OR output_schema_version<>1 THEN 'source_schema_unsupported'
   WHEN summary_count=0 OR inserted_item_count>0 AND file_count=0 THEN 'payload_missing'
   WHEN summary_count<>1 OR inserted_item_count<>file_count OR (file_count>0 AND (first_file_observed<>observed_at OR last_file_observed<>observed_at)) THEN 'stored_count_mismatch'
   WHEN observed_at>p_cutoff OR abs(extract(epoch FROM observed_at-completed_at))>300 THEN 'source_clock_skew'
   WHEN source_state<>1 AND NOT(source_state=6 AND file_count=0) THEN 'source_partial'
   WHEN p_run IS NULL AND (coalesce(completed_at-prior_completed>v_gap,false)
    OR schedule_revision IS DISTINCT FROM (v_schedule->>'scheduleRevision')::bigint
    OR target_revision IS DISTINCT FROM (v_schedule->>'targetRevision')::bigint) THEN 'collection_gap'
   WHEN file_count=0 THEN 'observed_empty' ELSE 'observed_files' END sample_reason
  FROM accounted a
 ) SELECT count(*)::integer,coalesce(sum(file_count),0),
  count(*) FILTER(WHERE sample_reason IN('observed_empty','observed_files'))::integer,
  count(*) FILTER(WHERE outcome NOT IN('succeeded','partial'))::integer,
  count(*) FILTER(WHERE loss_kind<>'none')::integer,
  count(*) FILTER(WHERE inserted_item_count>file_count OR outcome IN('succeeded','partial') AND summary_count=0)::integer,
  count(*) FILTER(WHERE sample_reason='observed_empty')::integer,
  coalesce(count(DISTINCT target_revision)>1 OR bool_or(target_revision<>p_revision),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
   'outcome',outcome,'lossKind',loss_kind,'minimumLostItems',lost_row_count,'expectedFileCount',inserted_item_count,
   'storedFileCount',file_count,'summaryPresent',summary_count=1,'observedAtUtc',CASE WHEN summary_count=1 THEN observed_at END,
   'state',CASE WHEN sample_reason IN('observed_empty','observed_files') THEN 'available' WHEN summary_count=1 OR file_count>0 THEN 'partial' ELSE 'unavailable' END,
   'reason',sample_reason,'source',jsonb_build_object('startedAtUtc',started_at,'collectorVersion',collector_version,
    'schemaVersion',output_schema_version,'scheduleRevision',schedule_revision,'sourceRowCount',source_row_count,
    'outputItemCount',output_item_count,'lossCountExact',loss_count_exact,'truncated',truncated,'lossDetected',loss_detected,
    'completionDigest',completion_digest,'requestDigest',request_digest,'summaryDigest',summary_digest,'fileDigest',file_digest))
   ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_run_count,v_files,v_complete,v_failed,v_lost,v_missing,v_empty,v_boundary,v_runs FROM classified;
 IF v_run_count>10000 OR v_files>250000 OR EXISTS(SELECT 1 FROM jsonb_array_elements(v_runs) r
  WHERE (r->>'storedFileCount')::integer>10000 OR (r->>'expectedFileCount')::integer>10000) THEN
  RAISE EXCEPTION 'TempDB window exceeds bounded retained evidence; choose a narrower window' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','tempdb-window-v1','targetId',p_target,'targetRevision',p_revision,
  'fromUtc',p_from,'toUtc',p_to,'runId',p_run,'sampleTargetRevision',p_sample_revision,'runs',v_runs,'schedule',
  CASE WHEN p_run IS NULL THEN v_schedule END,'policies',v_policy)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,'coverage',jsonb_build_object(
  'runCount',v_run_count,'completeRunCount',v_complete,'gapRunCount',v_run_count-v_complete,'failedRunCount',v_failed,
  'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,'emptyRunCount',v_empty,'retainedFileCount',v_files,
  'hasVisibilityGap',v_complete<>v_run_count OR v_boundary));
END $manifest$;
CREATE FUNCTION reporting.read_tempdb_window_history(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_revision bigint;v_kind text;
 v_manifest jsonb;v_coverage jsonb;v_epoch text;v_receipt jsonb;v_items jsonb;v_next jsonb;v_last jsonb;v_more boolean;
 v_after_completed timestamptz;v_after_run uuid;v_after_file integer;v_state text;v_reason text;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'TempDB target scope required' USING ERRCODE='42501'; END IF;
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision IS NULL OR p_revision NOT BETWEEN 1 AND 9007199254740991
  OR p_from IS NULL OR p_to IS NULL OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '7 days'
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR ((p_run IS NULL)<>(p_sample_revision IS NULL))
  OR (p_run IS NOT NULL AND (p_run='00000000-0000-0000-0000-000000000000' OR p_sample_revision NOT BETWEEN 1 AND p_revision))
 THEN RAISE EXCEPTION 'TempDB window request invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'TempDB history requires repeatable read' USING ERRCODE='55000'; END IF;
 SELECT revision INTO v_revision FROM control.observation_target WHERE instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'TempDB target revision changed' USING ERRCODE='40001'; END IF;
 v_kind:=CASE WHEN p_run IS NULL THEN 'receipts' ELSE 'snapshot' END;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>14
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('kind','targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','runId','sampleTargetRevision','afterCompletedAtUtc','afterRunId','afterFileId'))
   OR p_cursor->>'kind' IS DISTINCT FROM v_kind OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target
   OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from
   OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit
   OR (p_cursor->>'runId')::uuid IS DISTINCT FROM p_run OR (p_cursor->>'sampleTargetRevision')::bigint IS DISTINCT FROM p_sample_revision
   OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'TempDB continuation binding invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now THEN
   RAISE EXCEPTION 'TempDB continuation clock invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'TempDB continuation expired' USING ERRCODE='40001'; END IF;
  v_after_completed:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;
  v_after_file:=(p_cursor->>'afterFileId')::integer;
  IF (v_kind='receipts' AND (v_after_completed IS NULL OR v_after_run IS NULL OR v_after_file IS NOT NULL))
   OR (v_kind='snapshot' AND (v_after_completed IS NOT NULL OR v_after_run IS NOT NULL OR v_after_file NOT BETWEEN 1 AND 65535 OR v_after_file IS NULL))
  THEN RAISE EXCEPTION 'TempDB continuation anchor invalid' USING ERRCODE='22023'; END IF;
 END IF;
 v_manifest:=reporting.tempdb_window_manifest(p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample_revision);
 v_epoch:=v_manifest->>'readEpoch';v_coverage:=v_manifest->'coverage';
 IF p_cursor IS NOT NULL AND v_epoch IS DISTINCT FROM p_cursor->>'readEpoch' THEN
  RAISE EXCEPTION 'TempDB retained manifest changed' USING ERRCODE='40001'; END IF;
 IF v_kind='receipts' THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r WHERE (r->>'completedAtUtc')::timestamptz=v_after_completed AND (r->>'runId')::uuid=v_after_run) THEN
   RAISE EXCEPTION 'TempDB receipt anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(r-'source' ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC),'[]'::jsonb) INTO v_items
  FROM(SELECT r FROM jsonb_array_elements(v_manifest->'runs') r WHERE v_after_completed IS NULL
   OR ((r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)<(v_after_completed,v_after_run)
   ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC LIMIT p_limit+1) page;
  v_state:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'unavailable' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
  v_reason:=CASE WHEN (v_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary' WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_window' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM telemetry.tempdb_file_snapshot s WHERE s.instance_id=p_target AND s.run_id=p_run AND s.target_revision=p_sample_revision AND s.file_id=v_after_file) THEN
   RAISE EXCEPTION 'TempDB snapshot anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(item ORDER BY file_id),'[]'::jsonb) INTO v_items FROM(
   SELECT s.file_id,jsonb_build_object('fileId',s.file_id,'sizeBytes',s.size_bytes::text,'usedBytes',s.used_bytes::text,
    'freeBytes',s.free_bytes::text,'sourceState',s.state,'observedAtUtc',s.observed_at) item
   FROM telemetry.tempdb_file_snapshot s WHERE s.instance_id=p_target AND s.run_id=p_run AND s.target_revision=p_sample_revision
    AND v_receipt IS NOT NULL AND (v_after_file IS NULL OR s.file_id>v_after_file)
   ORDER BY s.file_id LIMIT p_limit+1) page;
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','snapshot_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r
    JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
    JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
    WHERE r.instance_id=p_target AND r.collector_id='tempdb.health' AND r.run_id=p_run AND r.target_revision=p_sample_revision
     AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN
   v_reason:='snapshot_outside_window';
  END IF;
 END IF;
 v_more:=jsonb_array_length(v_items)>p_limit;
 IF v_more THEN v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('kind',v_kind,'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,
   'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,'runId',p_run,'sampleTargetRevision',p_sample_revision,
   'afterCompletedAtUtc',CASE WHEN v_kind='receipts' THEN v_last->>'completedAtUtc' END,'afterRunId',CASE WHEN v_kind='receipts' THEN v_last->>'runId' END,
   'afterFileId',CASE WHEN v_kind='snapshot' THEN (v_last->>'fileId')::integer END);
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_epoch,'repositoryTimeUtc',v_now,'state',v_state,'reason',v_reason,'items',v_items,'nextAnchor',v_next)
  || CASE WHEN v_kind='receipts' THEN jsonb_build_object('coverage',v_coverage)
    ELSE jsonb_build_object('runId',p_run,'sampleTargetRevision',p_sample_revision,'receipt',v_receipt,'summary',(SELECT jsonb_build_object('observedAtUtc',s.observed_at,'sourceState',s.state,'totalBytes',s.total_bytes::text,'usedBytes',s.used_bytes::text,'logTotalBytes',s.log_total_bytes::text,'logUsedBytes',s.log_used_bytes::text,'truncated',s.truncated) FROM telemetry.tempdb_snapshot s WHERE s.instance_id=p_target AND s.run_id=p_run AND s.target_revision=p_sample_revision AND v_receipt->>'summaryPresent'='true')) END;
END $read$;

REVOKE ALL ON FUNCTION reporting.tempdb_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.read_tempdb_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_tempdb_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;
