-- Read existing immutable legacy source; no collector, permission or v2 activation changes.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.backup_availability_window_manifest(p_kind text,p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,p_run uuid DEFAULT NULL,p_sample bigint DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER STABLE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $manifest$
DECLARE v_runs jsonb;v_run_count integer;v_rows bigint;v_complete integer;v_failed integer;v_lost integer;v_missing integer;v_empty integer;
 v_boundary boolean;v_cadence_gap boolean;v_schedule jsonb;v_policy jsonb;v_gap interval;v_epoch text;v_run_ids uuid[];v_source_count bigint;
BEGIN
 IF p_kind NOT IN('backup','availability') OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'Legacy history scope required' USING ERRCODE='42501'; END IF;
 -- Refuse overflow before expensive per-row hashes. One cohort membership scan avoids
 -- nested target-wide scans on fresh, unanalyzed source bursts.
 SELECT array_agg(run_id) INTO v_run_ids FROM(
  SELECT r.run_id FROM telemetry.collection_run_v2 r
  JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id=CASE p_kind WHEN 'backup' THEN 'backups.status' ELSE 'availability-groups.health' END
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR r.run_id=p_run AND r.target_revision=p_sample)
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 20481) bounded;
 IF coalesce(cardinality(v_run_ids),0)>20480 THEN RAISE EXCEPTION 'Legacy history run bound exceeded' USING ERRCODE='54000'; END IF;
 SELECT count(*) INTO v_source_count FROM(
  SELECT 1 FROM telemetry.backup_status_snapshot b WHERE p_kind='backup' AND b.instance_id=p_target AND b.run_id=ANY(v_run_ids)
  UNION ALL SELECT 1 FROM telemetry.availability_group_replica_snapshot a WHERE p_kind='availability' AND a.instance_id=p_target AND a.run_id=ANY(v_run_ids)
  UNION ALL SELECT 1 FROM telemetry.availability_group_database_snapshot a WHERE p_kind='availability' AND a.instance_id=p_target AND a.run_id=ANY(v_run_ids)
  LIMIT 250001) bounded;
 IF v_source_count>250000 THEN RAISE EXCEPTION 'Legacy history row bound exceeded' USING ERRCODE='54000'; END IF;
 SELECT jsonb_build_object('targetRevision',s.target_revision,'version',s.collector_version,'enabled',s.enabled,'interval',s.collection_interval,'timeout',c.execution_timeout)
  ,greatest(interval '5 minutes',s.collection_interval*2+c.execution_timeout) INTO v_schedule,v_gap
 FROM control.collector_schedule s JOIN control.collector_contract c USING(collector_id,collector_version)
 WHERE s.instance_id=p_target AND s.collector_id=CASE p_kind WHEN 'backup' THEN 'backups.status' ELSE 'availability-groups.health' END;
 v_gap:=coalesce(v_gap,interval '5 minutes');
 SELECT jsonb_agg(to_jsonb(p) ORDER BY p.data_class) INTO v_policy FROM system.retention_policy p
 WHERE p.parent_table IN('backup_status_snapshot','availability_group_replica_snapshot','availability_group_database_snapshot','collection_run_v2');
 WITH bounded_runs AS MATERIALIZED (
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,o.completed_at,o.outcome,o.loss_kind,o.lost_row_count,o.inserted_item_count,o.completion_digest,
   q.m9_observation_state,q.m9_visibility_scope,q.payload_digest,q.committed_at,
   q.run_id IS NOT NULL AND q.instance_id=r.instance_id AND q.target_revision=r.target_revision AND q.collector_id=r.collector_id AND q.collector_version=r.collector_version AND q.output_schema_version=r.output_schema_version AND q.payload_digest=o.completion_digest AS header_valid
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id LEFT JOIN telemetry.m9_commit_replay q ON q.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.run_id=ANY(v_run_ids) AND r.collector_id=CASE p_kind WHEN 'backup' THEN 'backups.status' ELSE 'availability-groups.health' END
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR r.run_id=p_run AND r.target_revision=p_sample)
  ORDER BY o.completed_at DESC,r.run_id DESC LIMIT 20481
 ), retained_source AS MATERIALIZED (
  SELECT b.run_id,b.target_revision,b.observed_at,0 AS stream,encode(sha256(convert_to(to_jsonb(b)::text,'UTF8')),'hex') AS row_digest,true AS state_available
  FROM telemetry.backup_status_snapshot b WHERE p_kind='backup' AND b.instance_id=p_target AND b.run_id=ANY(ARRAY(SELECT run_id FROM bounded_runs))
  UNION ALL SELECT a.run_id,a.target_revision,a.observed_at,1,encode(sha256(convert_to(to_jsonb(a)::text,'UTF8')),'hex'),a.state_available
  FROM telemetry.availability_group_replica_snapshot a WHERE p_kind='availability' AND a.instance_id=p_target AND a.run_id=ANY(ARRAY(SELECT run_id FROM bounded_runs))
  UNION ALL SELECT a.run_id,a.target_revision,a.observed_at,2,encode(sha256(convert_to(to_jsonb(a)::text,'UTF8')),'hex'),a.state_available
  FROM telemetry.availability_group_database_snapshot a WHERE p_kind='availability' AND a.instance_id=p_target AND a.run_id=ANY(ARRAY(SELECT run_id FROM bounded_runs))
  LIMIT 250001
 ), grouped_source AS (
  SELECT run_id,count(*)::integer AS row_count,count(*) FILTER(WHERE stream=1)::integer AS replica_count,count(*) FILTER(WHERE stream=2)::integer AS database_count,
   min(observed_at) AS observed_at,count(DISTINCT observed_at)=1 AS shared_clock,bool_and(state_available) AS state_available,
   min(target_revision)=max(target_revision) AS shared_revision,min(target_revision) AS source_revision,
   encode(sha256(convert_to(string_agg(row_digest,'' ORDER BY row_digest),'UTF8')),'hex') AS rows_digest
  FROM retained_source GROUP BY run_id
 ), source_receipts AS (
  SELECT r.*,coalesce(s.row_count,0) AS row_count,coalesce(s.replica_count,0) AS replica_count,coalesce(s.database_count,0) AS database_count,
   CASE WHEN s.shared_clock THEN s.observed_at END AS observed_at,s.rows_digest,
   CASE WHEN r.outcome NOT IN('succeeded','partial') THEN 'collection_failed'
    WHEN r.collector_version<>1 OR r.output_schema_version<>1 THEN 'source_schema_unsupported'
    WHEN NOT r.header_valid OR r.m9_observation_state IS NULL OR p_kind='availability' AND r.m9_visibility_scope IS NULL THEN 'source_header_missing'
    WHEN r.loss_kind IS DISTINCT FROM 'none' OR r.lost_row_count IS DISTINCT FROM 0 OR r.outcome='partial' THEN 'collection_loss'
    WHEN r.inserted_item_count>coalesce(s.row_count,0) THEN 'payload_missing'
    WHEN r.inserted_item_count<>coalesce(s.row_count,0) OR coalesce(s.row_count,0)>CASE p_kind WHEN 'backup' THEN 1537 ELSE 2048 END OR s.shared_revision=false OR s.source_revision IS DISTINCT FROM r.target_revision AND s.row_count>0 THEN 'stored_count_mismatch'
    WHEN s.shared_clock=false OR s.observed_at>p_cutoff OR abs(extract(epoch FROM s.observed_at-r.completed_at))>300 THEN 'source_clock_skew'
    WHEN r.m9_observation_state NOT IN(1,6) OR r.m9_observation_state=6 AND coalesce(s.row_count,0)>0 OR s.state_available=false THEN 'source_partial'
    WHEN coalesce(s.row_count,0)=0 THEN 'observed_empty' ELSE 'observed_rows' END AS receipt_reason,
   lag(r.completed_at) OVER(ORDER BY r.completed_at,r.run_id) AS previous_completion
  FROM bounded_runs r LEFT JOIN grouped_source s USING(run_id)
 ), classified AS (
  SELECT s.*,CASE WHEN receipt_reason IN('observed_rows','observed_empty') THEN 'available'
    WHEN receipt_reason IN('collection_failed','source_schema_unsupported') THEN 'unavailable' ELSE 'partial' END AS receipt_state
  FROM source_receipts s
 )
 SELECT count(*)::integer,coalesce(sum(row_count),0),count(*) FILTER(WHERE receipt_state='available')::integer,
  count(*) FILTER(WHERE outcome NOT IN('succeeded','partial'))::integer,count(*) FILTER(WHERE loss_kind IS NOT NULL AND loss_kind<>'none')::integer,
  count(*) FILTER(WHERE collector_version=1 AND output_schema_version=1 AND (inserted_item_count>row_count OR receipt_reason='source_header_missing'))::integer,count(*) FILTER(WHERE receipt_reason='observed_empty')::integer,
  coalesce(bool_or(target_revision<>p_revision),false) OR count(DISTINCT target_revision)>1,
  p_run IS NULL AND coalesce(bool_or(previous_completion IS NOT NULL AND completed_at-previous_completion>v_gap),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'collectorVersion',collector_version,'outputSchemaVersion',output_schema_version,
   'completedAtUtc',completed_at,'monitorObservedAtUtc',observed_at,'outcome',outcome,'lossKind',loss_kind,'minimumLostItems',lost_row_count,
   'sourceState',CASE WHEN header_valid THEN m9_observation_state END,'visibilityScope',CASE WHEN p_kind='availability' AND header_valid THEN m9_visibility_scope END,
   'expectedRowCount',inserted_item_count,'storedRowCount',row_count,'storedReplicaCount',replica_count,'storedDatabaseCount',database_count,
   'state',receipt_state,'reason',receipt_reason,'source',jsonb_build_object('rowsDigest',rows_digest,'completionDigest',encode(completion_digest,'hex'),'payloadDigest',encode(payload_digest,'hex'),'committedAt',committed_at,'headerValid',header_valid))
   ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_run_count,v_rows,v_complete,v_failed,v_lost,v_missing,v_empty,v_boundary,v_cadence_gap,v_runs FROM classified;
 IF v_run_count>20480 OR v_rows>250000 OR EXISTS(SELECT 1 FROM jsonb_array_elements(v_runs) r WHERE (r->>'storedRowCount')::integer>CASE p_kind WHEN 'backup' THEN 1537 ELSE 2048 END) THEN
  RAISE EXCEPTION 'Legacy history bounded cohort overflow' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','backup-availability-window-v1','kind',p_kind,'targetId',p_target,'revision',p_revision,'from',p_from,'to',p_to,'cutoff',CASE WHEN p_run IS NULL THEN p_cutoff END,'selectedRun',p_run,'sampleRevision',p_sample,'runs',v_runs,'schedule',v_schedule,'policy',v_policy)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,'coverage',jsonb_build_object('runCount',v_run_count,'completeRunCount',v_complete,'gapRunCount',v_run_count-v_complete,
  'failedRunCount',v_failed,'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,'emptyRunCount',v_empty,'retainedRowCount',v_rows,'hasVisibilityGap',v_complete<>v_run_count OR v_boundary OR v_cadence_gap));
END $manifest$;

CREATE FUNCTION reporting.backup_availability_window_rows(p_stream text,p_target uuid,p_run uuid,p_sample bigint)
RETURNS TABLE(first_key text,second_key text,item jsonb) LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $rows$
 SELECT encode(b.database_fingerprint,'hex'),b.backup_kind::text,jsonb_build_object('databaseFingerprint',encode(b.database_fingerprint,'hex'),'kind',b.backup_kind,
  'backupSetId',b.backup_set_id::text,'lastFinishUtc',b.last_finish_utc,'sourceLocalFinish',CASE WHEN b.source_local_finish IS NOT NULL THEN to_char(b.source_local_finish,'YYYY-MM-DD"T"HH24:MI:SS.US')||'0' END,
  'sourceTimeUnknown',b.source_time_unknown,'sizeBytes',b.size_bytes::text,'copyOnly',b.copy_only,'hasChecksum',b.has_checksum,'isDamaged',b.is_damaged,'coverage',b.coverage,'monitorObservedAtUtc',b.observed_at)
 FROM telemetry.backup_status_snapshot b WHERE p_stream='backup' AND b.instance_id=p_target AND b.run_id=p_run AND b.target_revision=p_sample
 UNION ALL SELECT encode(a.group_fingerprint,'hex'),encode(a.replica_fingerprint,'hex'),jsonb_build_object('groupFingerprint',encode(a.group_fingerprint,'hex'),'replicaFingerprint',encode(a.replica_fingerprint,'hex'),
  'role',a.role,'operationalState',a.operational_state,'connectedState',a.connected_state,'visibilityScope',a.visibility_scope,'stateAvailable',a.state_available,'monitorObservedAtUtc',a.observed_at)
 FROM telemetry.availability_group_replica_snapshot a WHERE p_stream='replicas' AND a.instance_id=p_target AND a.run_id=p_run AND a.target_revision=p_sample
 UNION ALL SELECT encode(a.group_fingerprint,'hex'),encode(a.database_fingerprint,'hex'),jsonb_build_object('groupFingerprint',encode(a.group_fingerprint,'hex'),'databaseFingerprint',encode(a.database_fingerprint,'hex'),
  'synchronizationState',a.synchronization_state,'databaseState',a.database_state,'visibilityScope',a.visibility_scope,'stateAvailable',a.state_available,'monitorObservedAtUtc',a.observed_at)
 FROM telemetry.availability_group_database_snapshot a WHERE p_stream='databases' AND a.instance_id=p_target AND a.run_id=p_run AND a.target_revision=p_sample;
$rows$;

CREATE FUNCTION reporting.read_backup_availability_window_history(p_kind text,p_stream text,p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,p_run uuid DEFAULT NULL,p_sample bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_revision bigint;v_manifest jsonb;v_coverage jsonb;v_epoch text;v_receipt jsonb;v_items jsonb;v_next jsonb;v_last jsonb;v_more boolean;
 v_after_completed timestamptz;v_after_run uuid;v_first text;v_second text;v_state text;v_reason text;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN RAISE EXCEPTION 'Target scope required' USING ERRCODE='42501'; END IF;
 IF p_kind IS NULL OR p_kind NOT IN('backup','availability') OR p_stream IS NULL OR p_stream NOT IN('receipts','backup','replicas','databases')
  OR p_stream='backup' AND p_kind<>'backup' OR p_stream IN('replicas','databases') AND p_kind<>'availability'
  OR p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision IS NULL OR p_revision NOT BETWEEN 1 AND 9007199254740991
  OR p_from IS NULL OR p_to IS NULL OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '7 days'
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR p_stream='receipts' AND (p_run IS NOT NULL OR p_sample IS NOT NULL)
  OR p_stream<>'receipts' AND (p_run IS NULL OR p_run='00000000-0000-0000-0000-000000000000' OR p_sample IS NULL OR p_sample NOT BETWEEN 1 AND p_revision)
 THEN RAISE EXCEPTION 'Legacy history request invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN RAISE EXCEPTION 'Repeatable read required' USING ERRCODE='55000'; END IF;
 SELECT revision INTO v_revision FROM control.observation_target WHERE instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'Current target revision changed' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>16
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('kind','stream','targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','runId','sampleTargetRevision','afterCompletedAtUtc','afterRunId','afterFirstKey','afterSecondKey'))
   OR p_cursor->>'kind' IS DISTINCT FROM p_kind OR p_cursor->>'stream' IS DISTINCT FROM p_stream OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target
   OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to
   OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit OR (p_cursor->>'runId')::uuid IS DISTINCT FROM p_run OR (p_cursor->>'sampleTargetRevision')::bigint IS DISTINCT FROM p_sample OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'Continuation binding invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now THEN RAISE EXCEPTION 'Continuation clock invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'Continuation expired' USING ERRCODE='40001'; END IF;
  v_after_completed:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;v_first:=p_cursor->>'afterFirstKey';v_second:=p_cursor->>'afterSecondKey';
  IF p_stream='receipts' AND (v_after_completed IS NULL OR v_after_run IS NULL OR v_after_run='00000000-0000-0000-0000-000000000000' OR NOT isfinite(v_after_completed) OR v_after_completed<p_from OR v_after_completed>=p_to OR v_after_completed>v_cutoff OR v_first IS NOT NULL OR v_second IS NOT NULL)
   OR p_stream<>'receipts' AND (v_after_completed IS NOT NULL OR v_after_run IS NOT NULL OR coalesce(v_first,'') !~ '^[0-9a-f]{64}$' OR (p_stream='backup' AND coalesce(v_second,'') NOT IN('1','2','3')) OR (p_stream<>'backup' AND coalesce(v_second,'') !~ '^[0-9a-f]{64}$'))
  THEN RAISE EXCEPTION 'Continuation anchor invalid' USING ERRCODE='22023'; END IF;
 END IF;
 v_manifest:=reporting.backup_availability_window_manifest(p_kind,p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample);v_epoch:=v_manifest->>'readEpoch';v_coverage:=v_manifest->'coverage';
 IF p_cursor IS NOT NULL AND p_cursor->>'readEpoch' IS DISTINCT FROM v_epoch THEN RAISE EXCEPTION 'Retained manifest changed' USING ERRCODE='40001'; END IF;
 IF p_stream='receipts' THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r WHERE (r->>'completedAtUtc')::timestamptz=v_after_completed AND (r->>'runId')::uuid=v_after_run) THEN RAISE EXCEPTION 'Receipt anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(r-'source' ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC),'[]'::jsonb) INTO v_items FROM(
   SELECT r FROM jsonb_array_elements(v_manifest->'runs') r WHERE v_after_completed IS NULL OR ((r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)<(v_after_completed,v_after_run)
   ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC LIMIT p_limit+1) page;
  v_state:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'unavailable' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
  v_reason:=CASE WHEN (v_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary' WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_window' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM reporting.backup_availability_window_rows(p_stream,p_target,p_run,p_sample) r WHERE r.first_key=v_first AND r.second_key=v_second) THEN RAISE EXCEPTION 'Snapshot anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(item ORDER BY first_key,second_key),'[]'::jsonb) INTO v_items FROM(
   SELECT first_key,second_key,item FROM reporting.backup_availability_window_rows(p_stream,p_target,p_run,p_sample) r WHERE v_receipt IS NOT NULL AND (v_first IS NULL OR (r.first_key,r.second_key)>(v_first,v_second))
   ORDER BY r.first_key,r.second_key LIMIT p_limit+1) page;
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','snapshot_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
   WHERE r.instance_id=p_target AND r.run_id=p_run AND r.target_revision=p_sample AND r.collector_id=CASE p_kind WHEN 'backup' THEN 'backups.status' ELSE 'availability-groups.health' END AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN v_reason:='snapshot_outside_window'; END IF;
 END IF;
 v_more:=jsonb_array_length(v_items)>p_limit;
 IF v_more THEN
  v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('kind',p_kind,'stream',p_stream,'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,'runId',p_run,'sampleTargetRevision',p_sample,
   'afterCompletedAtUtc',CASE WHEN p_stream='receipts' THEN v_last->>'completedAtUtc' END,'afterRunId',CASE WHEN p_stream='receipts' THEN v_last->>'runId' END,
   'afterFirstKey',CASE p_stream WHEN 'backup' THEN v_last->>'databaseFingerprint' WHEN 'replicas' THEN v_last->>'groupFingerprint' WHEN 'databases' THEN v_last->>'groupFingerprint' END,
   'afterSecondKey',CASE p_stream WHEN 'backup' THEN v_last->>'kind' WHEN 'replicas' THEN v_last->>'replicaFingerprint' WHEN 'databases' THEN v_last->>'databaseFingerprint' END);
 END IF;
 RETURN jsonb_build_object('kind',p_kind,'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'readEpoch',v_epoch,'repositoryTimeUtc',v_now,'sourceQualification','legacy_v1','state',v_state,'reason',v_reason,'items',v_items,'nextAnchor',v_next)
  || CASE WHEN p_stream='receipts' THEN jsonb_build_object('coverage',v_coverage) ELSE jsonb_build_object('stream',p_stream,'runId',p_run,'sampleTargetRevision',p_sample,'receipt',v_receipt) END;
END $read$;

REVOKE ALL ON FUNCTION reporting.backup_availability_window_manifest(text,uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.backup_availability_window_rows(text,uuid,uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.read_backup_availability_window_history(text,text,uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_backup_availability_window_history(text,text,uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;
