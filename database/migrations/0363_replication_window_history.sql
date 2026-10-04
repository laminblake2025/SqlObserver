-- Retained replication evidence, with explicit legacy status/monitor-clock semantics.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
CREATE INDEX ix_replication_canonical_run ON telemetry.replication_snapshot_v2(run_id,instance_id,target_revision);
ALTER TABLE telemetry.replication_snapshot_v2 ADD CONSTRAINT fk_replication_canonical_run
 FOREIGN KEY(run_id) REFERENCES telemetry.collection_run_identity(run_id) NOT VALID;
-- Avoid unbounded validation/backfill on upgraded fleets. Unvalidated catalogs
-- retain the existing conservative canonical-retirement hold until reviewed.
DO $validate$
BEGIN
 IF (SELECT count(*) FROM (SELECT 1 FROM telemetry.replication_snapshot_v2 LIMIT 250001) bounded)<=250000
 AND NOT EXISTS(SELECT 1 FROM telemetry.replication_snapshot_v2 s LEFT JOIN telemetry.collection_run_identity i ON i.run_id=s.run_id WHERE i.run_id IS NULL) THEN
  ALTER TABLE telemetry.replication_snapshot_v2 VALIDATE CONSTRAINT fk_replication_canonical_run;
 END IF;
END $validate$;
CREATE FUNCTION reporting.replication_window_manifest(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $manifest$
DECLARE v_runs jsonb;v_count integer;v_rows bigint;v_complete integer;v_failed integer;v_lost integer;
 v_missing integer;v_empty integer;v_unbound integer:=0;v_unbound_digest text;v_schedule jsonb;v_policy jsonb;v_gap interval;v_boundary boolean;v_continuity boolean;v_epoch text;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'Replication target scope required' USING ERRCODE='42501'; END IF;
 SELECT jsonb_build_object('targetRevision',s.target_revision,'scheduleRevision',s.schedule_revision,
  'enabled',s.enabled,'collectorVersion',s.collector_version,'interval',s.collection_interval,'timeout',c.execution_timeout),
  greatest(interval '5 minutes',s.collection_interval*2+c.execution_timeout)
 INTO v_schedule,v_gap FROM control.collector_schedule s JOIN control.collector_contract c
 ON c.collector_id=s.collector_id AND c.collector_version=s.collector_version
 WHERE s.instance_id=p_target AND s.collector_id='replication.health';
 v_gap:=coalesce(v_gap,interval '10 minutes');
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.data_class),'[]'::jsonb) INTO v_policy
 FROM system.retention_policy p WHERE p.data_class IN('m10_replication','collection_run_canonical');
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id WHERE r.instance_id=p_target AND r.collector_id='replication.health'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND (p_run IS NULL OR(r.run_id=p_run AND r.target_revision=p_sample_revision)) LIMIT 20481
 ), bounded_source AS MATERIALIZED(
  SELECT 1 FROM telemetry.replication_snapshot_v2 s WHERE s.instance_id=p_target AND s.collected_at<=p_cutoff
   AND s.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
   AND s.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM cohort r)->>s.run_id::text)::bigint LIMIT 250001
 ) SELECT (SELECT count(*) FROM cohort),(SELECT count(*) FROM bounded_source) INTO v_count,v_rows;
 IF v_count>20480 OR v_rows>250000 THEN RAISE EXCEPTION 'Replication retained evidence bound exceeded' USING ERRCODE='54000'; END IF;
 IF p_run IS NULL THEN
  -- Legacy unbound rows cannot be assigned a completion time. Count their
  -- actual repository-ingest time bucket separately, never as completed runs.
  WITH unbound AS MATERIALIZED(
   SELECT s.* FROM telemetry.replication_snapshot_v2 s WHERE s.instance_id=p_target AND s.collected_at>=p_from AND s.collected_at<p_to AND s.collected_at<=p_cutoff
    AND NOT EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
     JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id WHERE r.run_id=s.run_id AND r.instance_id=s.instance_id AND r.target_revision=s.target_revision AND r.collector_id='replication.health') LIMIT 250001
  ) SELECT count(*)::integer,encode(sha256(convert_to(coalesce(string_agg(encode(sha256(convert_to(to_jsonb(s)::text,'UTF8')),'hex'),'' ORDER BY s.collected_at,s.run_id,s.topology_fingerprint),''),'UTF8')),'hex')
  INTO v_unbound,v_unbound_digest FROM unbound s;
  IF v_unbound>250000 OR v_rows+v_unbound>250000 THEN RAISE EXCEPTION 'Replication unbound source limit exceeded' USING ERRCODE='54000'; END IF;
 END IF;
 WITH cohort AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.schedule_revision,r.started_at,
   o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,o.inserted_item_count,o.source_row_count,o.output_item_count,
   o.loss_count_exact,o.truncated,o.loss_detected,encode(o.completion_digest,'hex') completion_digest,encode(r.request_digest,'hex') request_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id WHERE r.instance_id=p_target AND r.collector_id='replication.health'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
   AND(p_run IS NULL OR(r.run_id=p_run AND r.target_revision=p_sample_revision))
 ), source AS MATERIALIZED(
  SELECT s.* FROM telemetry.replication_snapshot_v2 s WHERE s.instance_id=p_target AND s.collected_at<=p_cutoff
   AND s.run_id=ANY(ARRAY(SELECT r.run_id FROM cohort r))
   AND s.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM cohort r)->>s.run_id::text)::bigint
 ), stored AS MATERIALIZED(
  SELECT s.run_id,count(*) row_count,min(s.observed_at) first_observed,max(s.observed_at) last_observed,
   count(*) FILTER(WHERE s.visibility_scope<>1) partial_count,
   count(*) FILTER(WHERE s.observed_at>p_cutoff OR abs(extract(epoch FROM s.observed_at-s.collected_at))>300) skew_count,
   count(DISTINCT s.topology_fingerprint) unique_count,
   encode(sha256(convert_to(string_agg(encode(sha256(convert_to(to_jsonb(s)::text,'UTF8')),'hex'),'' ORDER BY s.topology_fingerprint,s.observed_at),'UTF8')),'hex') source_digest
  FROM source s GROUP BY s.run_id
 ), accounted AS MATERIALIZED(
  SELECT r.*,coalesce(s.row_count,0) row_count,s.first_observed,s.last_observed,coalesce(s.partial_count,0) partial_count,
   coalesce(s.skew_count,0) skew_count,coalesce(s.unique_count,0) unique_count,s.source_digest,
   lag(r.completed_at) OVER(PARTITION BY r.target_revision ORDER BY r.completed_at,r.run_id) prior_completed
  FROM cohort r LEFT JOIN stored s ON s.run_id=r.run_id
 ), classified AS MATERIALIZED(
  SELECT a.*,CASE WHEN outcome NOT IN('succeeded','partial') THEN 'collection_failed'
   WHEN outcome='partial' OR loss_kind IS DISTINCT FROM 'none' OR truncated OR loss_detected THEN 'collection_loss'
   WHEN collector_version<>1 OR output_schema_version<>1 THEN 'source_schema_unsupported'
   WHEN inserted_item_count>0 AND row_count=0 THEN 'payload_missing'
   WHEN inserted_item_count<>row_count OR unique_count<>row_count OR first_observed IS DISTINCT FROM last_observed THEN 'stored_count_mismatch'
   WHEN skew_count>0 OR abs(extract(epoch FROM first_observed-completed_at))>300 THEN 'source_clock_skew' WHEN partial_count>0 THEN 'source_partial'
   WHEN row_count=0 THEN 'observed_empty' ELSE 'observed_rows' END sample_reason,
   p_run IS NULL AND(coalesce(completed_at-prior_completed>v_gap,false) OR schedule_revision IS DISTINCT FROM(v_schedule->>'scheduleRevision')::bigint OR target_revision IS DISTINCT FROM(v_schedule->>'targetRevision')::bigint) continuity_gap FROM accounted a
 ) SELECT count(*)::integer,coalesce(sum(row_count),0),count(*) FILTER(WHERE sample_reason IN('observed_rows','observed_empty'))::integer,
  count(*) FILTER(WHERE outcome NOT IN('succeeded','partial'))::integer,count(*) FILTER(WHERE loss_kind<>'none')::integer,
  count(*) FILTER(WHERE inserted_item_count>row_count)::integer,count(*) FILTER(WHERE sample_reason='observed_empty')::integer,
  coalesce(count(DISTINCT target_revision)>1 OR bool_or(target_revision<>p_revision),false),coalesce(bool_or(continuity_gap),false),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
   'outcome',outcome,'lossKind',loss_kind,'minimumLostItems',lost_row_count,'expectedRowCount',inserted_item_count,'storedRowCount',row_count,
   'monitorObservedAtUtc',CASE WHEN row_count>0 AND first_observed=last_observed THEN first_observed END,
   'state',CASE WHEN sample_reason IN('observed_rows','observed_empty') THEN 'available' WHEN row_count>0 THEN 'partial' ELSE 'unavailable' END,'reason',sample_reason,
   'source',jsonb_build_object('startedAtUtc',started_at,'collectorVersion',collector_version,'schemaVersion',output_schema_version,'scheduleRevision',schedule_revision,
    'sourceRowCount',source_row_count,'outputItemCount',output_item_count,'lossCountExact',loss_count_exact,'truncated',truncated,'lossDetected',loss_detected,
    'completionDigest',completion_digest,'requestDigest',request_digest,'sourceDigest',source_digest)) ORDER BY completed_at DESC,run_id DESC),'[]'::jsonb)
 INTO v_count,v_rows,v_complete,v_failed,v_lost,v_missing,v_empty,v_boundary,v_continuity,v_runs FROM classified;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(v_runs) r WHERE(r->>'storedRowCount')::integer>2048 OR(r->>'expectedRowCount')::integer>2048) THEN
  RAISE EXCEPTION 'Replication snapshot row bound exceeded' USING ERRCODE='54000'; END IF;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','replication-window-v1','target',p_target,'revision',p_revision,'from',p_from,'to',p_to,'run',p_run,'sampleRevision',p_sample_revision,
  'runs',v_runs,'schedule',CASE WHEN p_run IS NULL THEN v_schedule END,'policies',v_policy,'unboundCount',v_unbound,'unboundDigest',v_unbound_digest)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'readEpoch',v_epoch,'revisionBoundary',v_boundary,'coverage',jsonb_build_object('runCount',v_count,'completeRunCount',v_complete,
  'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,'lostRunCount',v_lost,'missingPayloadRunCount',v_missing,'emptyRunCount',v_empty,'retainedRowCount',v_rows,
  'unboundSourceRowCount',v_unbound,'hasVisibilityGap',v_complete<>v_count OR v_boundary OR v_continuity OR v_unbound>0));
END $manifest$;
CREATE FUNCTION reporting.read_replication_window_history(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_run uuid DEFAULT NULL,p_sample_revision bigint DEFAULT NULL,p_cursor jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_kind text;v_revision bigint;
 v_manifest jsonb;v_epoch text;v_items jsonb;v_receipt jsonb;v_coverage jsonb;v_state text;v_reason text;
 v_before timestamptz;v_after_run uuid;v_after_identity text;v_more boolean;v_last jsonb;v_next jsonb;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN
  RAISE EXCEPTION 'Replication history scope required' USING ERRCODE='42501'; END IF;
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision IS NULL
  OR p_revision NOT BETWEEN 1 AND 9007199254740991 OR p_from IS NULL OR p_to IS NULL
  OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '7 days'
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR ((p_run IS NULL)<>(p_sample_revision IS NULL))
  OR p_run='00000000-0000-0000-0000-000000000000' OR p_sample_revision IS NOT NULL AND p_sample_revision NOT BETWEEN 1 AND p_revision
 THEN RAISE EXCEPTION 'Replication history request invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'Replication history requires repeatable read' USING ERRCODE='55000'; END IF;
 SELECT revision INTO v_revision FROM control.observation_target WHERE instance_id=p_target FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_revision<>p_revision THEN RAISE EXCEPTION 'Replication history target changed' USING ERRCODE='40001'; END IF;
 v_kind:=CASE WHEN p_run IS NULL THEN 'receipts' ELSE 'snapshot' END;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>14
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('kind','targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','runId','sampleTargetRevision','afterCompletedAtUtc','afterRunId','afterRowIdentity'))
   OR p_cursor->>'kind' IS DISTINCT FROM v_kind OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target
   OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from
   OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit
   OR (p_cursor->>'runId')::uuid IS DISTINCT FROM p_run OR (p_cursor->>'sampleTargetRevision')::bigint IS DISTINCT FROM p_sample_revision
   OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'Replication history cursor invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now THEN
   RAISE EXCEPTION 'Replication history cursor timing invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'Replication history cursor expired' USING ERRCODE='40001'; END IF;
  v_before:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;v_after_identity:=p_cursor->>'afterRowIdentity';
  IF v_kind='receipts' AND (v_before IS NULL OR v_after_run IS NULL OR v_after_identity IS NOT NULL)
   OR v_kind='snapshot' AND (v_before IS NOT NULL OR v_after_run IS NOT NULL OR coalesce(v_after_identity,'') !~ '^[0-9a-f]{64}$') THEN
   RAISE EXCEPTION 'Replication history anchor invalid' USING ERRCODE='22023'; END IF;
 END IF;
 v_manifest:=reporting.replication_window_manifest(p_target,p_revision,p_from,p_to,v_cutoff,p_run,p_sample_revision);
 v_coverage:=v_manifest->'coverage';v_epoch:=v_manifest->>'readEpoch';
 IF v_kind='receipts' THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r
   WHERE (r->>'completedAtUtc')::timestamptz=v_before AND (r->>'runId')::uuid=v_after_run) THEN
   RAISE EXCEPTION 'Replication receipt anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(r-'source' ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC),'[]'::jsonb) INTO v_items
  FROM(SELECT r FROM jsonb_array_elements(v_manifest->'runs') r
   WHERE v_before IS NULL OR ((r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)<(v_before,v_after_run)
   ORDER BY (r->>'completedAtUtc')::timestamptz DESC,(r->>'runId')::uuid DESC LIMIT p_limit+1) q;
  v_state:=CASE WHEN (v_coverage->>'runCount')::integer=0 THEN 'unavailable' WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END;
  v_reason:=CASE WHEN (v_coverage->>'unboundSourceRowCount')::integer>0 THEN 'source_unbound' WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs' WHEN (v_manifest->>'revisionBoundary')::boolean THEN 'revision_boundary'
   WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_window' END;
 ELSE
  v_receipt:=(v_manifest->'runs'->0)-'source';
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM telemetry.replication_snapshot_v2 s WHERE s.instance_id=p_target
   AND s.run_id=p_run AND s.target_revision=p_sample_revision AND encode(s.topology_fingerprint,'hex')=v_after_identity AND s.collected_at<=v_cutoff) THEN
   RAISE EXCEPTION 'Replication snapshot anchor unavailable' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(jsonb_build_object('rowIdentity',encode(s.topology_fingerprint,'hex'),'subscriptionFingerprint',encode(s.database_fingerprint,'hex'),
   'monitorObservedAtUtc',s.observed_at,'collectedAtUtc',s.collected_at,'role',s.role,'reportedStatus',s.synchronization_state,
   'pendingCommands',s.pending_commands::text,'latencySeconds',s.latency_seconds,'sourceCoverage',CASE s.visibility_scope WHEN 1 THEN 'complete' WHEN 2 THEN 'local_summary' ELSE 'visibility_gap' END,
   'stateAvailable',s.state_available,'topology',NULL,'ratePerSecond',NULL,'lastSuccessAgeSeconds',NULL) ORDER BY s.topology_fingerprint),'[]'::jsonb) INTO v_items
  FROM(SELECT s.* FROM telemetry.replication_snapshot_v2 s WHERE s.instance_id=p_target AND s.run_id=p_run AND s.target_revision=p_sample_revision
   AND s.collected_at<=v_cutoff AND v_receipt IS NOT NULL AND(v_after_identity IS NULL OR encode(s.topology_fingerprint,'hex')>v_after_identity)
   ORDER BY s.topology_fingerprint LIMIT p_limit+1) s;
  v_state:=coalesce(v_receipt->>'state','unavailable');v_reason:=coalesce(v_receipt->>'reason','snapshot_not_retained');
  IF v_receipt IS NULL AND EXISTS(SELECT 1 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i
   ON i.run_id=r.run_id AND i.started_at=r.started_at JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
   WHERE r.instance_id=p_target AND r.run_id=p_run AND r.target_revision=p_sample_revision AND r.collector_id='replication.health'
    AND o.completed_at<=v_cutoff AND (o.completed_at<p_from OR o.completed_at>=p_to)) THEN v_reason:='snapshot_outside_window'; END IF;
 END IF;
 IF p_cursor IS NOT NULL AND v_epoch IS DISTINCT FROM p_cursor->>'readEpoch' THEN
  RAISE EXCEPTION 'Replication history source changed' USING ERRCODE='40001'; END IF;
 v_more:=jsonb_array_length(v_items)>p_limit;
 IF v_more THEN v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('kind',v_kind,'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,
   'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,'runId',p_run,'sampleTargetRevision',p_sample_revision,
   'afterCompletedAtUtc',CASE WHEN v_kind='receipts' THEN v_last->>'completedAtUtc' END,
   'afterRunId',CASE WHEN v_kind='receipts' THEN v_last->>'runId' END,'afterRowIdentity',CASE WHEN v_kind='snapshot' THEN v_last->>'rowIdentity' END);
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_epoch,'repositoryTimeUtc',v_now,'state',v_state,'reason',v_reason,'items',v_items,'nextAnchor',v_next,'statusQualification','legacy_unqualified','observationTimeBasis','monitor_capture')
  || CASE WHEN v_kind='receipts' THEN jsonb_build_object('coverage',v_coverage)
   ELSE jsonb_build_object('runId',p_run,'sampleTargetRevision',p_sample_revision,'receipt',v_receipt) END;
END $read$;
CREATE OR REPLACE FUNCTION system.collection_run_reviewed_references()
RETURNS TABLE(child_relation regclass,column_name name,parent_relation regclass,delete_action "char")
LANGUAGE sql STABLE PARALLEL SAFE SET search_path=pg_catalog
AS $inventory$
 VALUES
 ('control.collector_schedule'::regclass,'active_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.blocking_edge'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.deadlock_summary'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.query_performance_run'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.activity_request_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.activity_session_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_group_database_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_group_replica_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.backup_status_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.collection_run_outcome'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.database_file_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.database_inventory_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.raw_metric_sample'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.server_wait_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure_occurrence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure_scan_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_volume_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.tempdb_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.visibility_gap'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.server_configuration_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.backup_v2_scan'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_replica_evidence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_database_replica_evidence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('alerting.deadlock_source_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'c'::"char"),
 ('control.deadlock_read_checkpoint'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'c'::"char"),
 ('events.plan_cache_runtime_watermark'::regclass,'last_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_runtime_delta'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_runtime_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_content_link'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_content_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.deadlock_read_receipt'::regclass,'run_id'::name,'telemetry.collection_run_outcome'::regclass,'a'::"char"),
 ('telemetry.server_lifecycle_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.server_lifecycle_event'::regclass,'after_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('control.server_lifecycle_checkpoint'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.cpu_paired_capture_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.replication_snapshot_v2'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char");
$inventory$;
REVOKE ALL ON FUNCTION system.collection_run_reviewed_references() FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.replication_snapshot_v2 TO sqlobserver_run_dependency_reader;
REVOKE ALL ON FUNCTION reporting.replication_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.read_replication_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_replication_window_history(uuid,bigint,timestamptz,timestamptz,integer,uuid,bigint,jsonb) TO sqlobserver_server;
