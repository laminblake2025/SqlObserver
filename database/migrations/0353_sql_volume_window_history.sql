-- Bounded completion-time history from canonical, immutable SQL-volume evidence.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.sql_volume_window_manifest(
 p_instance_id uuid,p_target_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $manifest$
DECLARE v_runs jsonb;v_catalog jsonb;v_run_count integer;v_observations bigint;v_complete integer;v_policy jsonb;
 v_gap_interval interval;v_epoch text;v_identity_count integer;v_revision_boundary boolean;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN
  RAISE EXCEPTION 'SQL volume target scope required' USING ERRCODE='42501';
 END IF;
 SELECT greatest(interval '5 minutes',coalesce(s.collection_interval,interval '5 minutes')*2) INTO v_gap_interval
 FROM control.collector_schedule s WHERE s.instance_id=p_instance_id AND s.collector_id='storage.volume';
 v_gap_interval:=coalesce(v_gap_interval,interval '10 minutes');
 SELECT jsonb_build_object('days',CASE WHEN p.enabled AND p.retain_for IS NOT NULL THEN greatest(1,floor(extract(epoch FROM p.retain_for)/86400)::integer) END,
   'enabled',p.enabled,'revision',p.policy_revision,'retainFor',p.retain_for) INTO v_policy
 FROM system.retention_policy p WHERE p.data_class='sql_volume_capacity';
 WITH bounded_runs AS MATERIALIZED(
  SELECT r.run_id,r.target_revision,r.collector_version,r.output_schema_version,r.started_at,r.schedule_revision,
   o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.lost_row_count,o.inserted_item_count,o.output_item_count,
   o.loss_count_exact,o.truncated,o.loss_detected,encode(o.completion_digest,'hex') completion_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_instance_id AND r.collector_id='storage.volume'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
  ORDER BY o.completed_at,r.run_id LIMIT 10001
 ), retained_source AS MATERIALIZED(
  -- The look-ahead observation proves overflow without scanning an unbounded
  -- retained payload. At or below the cap, this includes the complete cohort.
  SELECT s.run_id,s.target_revision,s.total_bytes,s.available_bytes,s.observed_at,s.collected_at
  -- Independent cohort membership avoids a nested target/revision bitmap scan
  -- per run when fresh partitions have not yet acquired planner statistics.
  -- The bounded revision map checks the exact run/revision pair before limiting.
  FROM telemetry.sql_volume_snapshot s WHERE s.instance_id=p_instance_id AND s.collected_at<=p_cutoff
   AND s.run_id=ANY(ARRAY(SELECT r.run_id FROM bounded_runs r))
   AND s.target_revision=((SELECT jsonb_object_agg(r.run_id::text,r.target_revision) FROM bounded_runs r)->>s.run_id::text)::bigint
  LIMIT 250001
 ), retained AS MATERIALIZED(
  -- Account the bounded run cohort in one grouped scan. A correlated aggregate
  -- per run repeatedly visits every retained partition before the overflow gate.
  SELECT s.run_id,s.target_revision,count(*) retained_count,
   count(*) FILTER(WHERE s.total_bytes IS NULL OR s.available_bytes IS NULL) unknown_count,
   count(*) FILTER(WHERE s.observed_at>p_cutoff OR abs(extract(epoch FROM s.observed_at-s.collected_at))>300) skew_count
  FROM retained_source s
  GROUP BY s.run_id,s.target_revision
 ), accounted AS MATERIALIZED(
  SELECT r.*,coalesce(a.retained_count,0) retained_count,coalesce(a.unknown_count,0) unknown_count,
   coalesce(a.skew_count,0) skew_count,
   lag(r.completed_at) OVER(PARTITION BY r.target_revision ORDER BY r.completed_at,r.run_id) prior_completed
  FROM bounded_runs r LEFT JOIN retained a ON a.run_id=r.run_id AND a.target_revision=r.target_revision
 ) SELECT count(*)::integer,coalesce(sum(retained_count),0),
  coalesce(jsonb_agg(jsonb_build_object('runId',run_id,'sampleTargetRevision',target_revision,'completedAtUtc',completed_at,
    'startedAtUtc',started_at,'collectorVersion',collector_version,'outputSchemaVersion',output_schema_version,'scheduleRevision',schedule_revision,
    'outcome',outcome,'reasonCode',reason_code,'lossKind',loss_kind,'minimumLostItems',lost_row_count,
    'insertedItemCount',inserted_item_count,'outputItemCount',output_item_count,'lossCountExact',loss_count_exact,
    'truncated',truncated,'lossDetected',loss_detected,'completionDigest',completion_digest,'retainedCount',retained_count,
    'unknownCount',unknown_count,'skewCount',skew_count,'elapsedGap',coalesce(completed_at-prior_completed>v_gap_interval,false),
    'isGap',outcome<>'succeeded' OR loss_kind<>'none' OR retained_count<>inserted_item_count OR retained_count=0
       OR retained_count>1000 OR unknown_count>0 OR skew_count>0 OR output_schema_version<>1
       OR coalesce(completed_at-prior_completed>v_gap_interval,false)) ORDER BY completed_at,run_id),'[]'::jsonb)
  INTO v_run_count,v_observations,v_runs FROM accounted;
 IF v_run_count>10000 OR v_observations>250000 THEN
  RAISE EXCEPTION 'volume history exceeds bounded whole window; choose a narrower window' USING ERRCODE='54000';
 END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(v_runs) r WHERE (r->>'retainedCount')::integer>1000) THEN
  RAISE EXCEPTION 'volume run exceeds immutable source bound' USING ERRCODE='54000';
 END IF;
 SELECT count(*)::integer INTO v_complete
 FROM jsonb_array_elements(v_runs) r WHERE NOT(r->>'isGap')::boolean;
 -- Boundary detection also includes revisions whose runs were wholly gaps.
 SELECT count(DISTINCT r->>'sampleTargetRevision')>1 INTO v_revision_boundary FROM jsonb_array_elements(v_runs) r;
 WITH runs AS MATERIALIZED(
  SELECT * FROM jsonb_to_recordset(v_runs) AS r("runId" uuid,"sampleTargetRevision" bigint,"completedAtUtc" timestamptz,"isGap" boolean)
 ), revisions AS MATERIALIZED(
  SELECT "sampleTargetRevision" sample_revision,count(*) run_count,bool_or("isGap") has_gap FROM runs GROUP BY "sampleTargetRevision"
 ), identities AS MATERIALIZED(
  SELECT s.target_revision,s.volume_key,min(s.identity_kind) identity_kind,count(DISTINCT s.identity_kind) identity_kinds,
   count(*) observation_count,min(r."completedAtUtc") first_completed,max(r."completedAtUtc") last_completed,
   (array_agg(s.total_bytes ORDER BY r."completedAtUtc",r."runId",s.observed_at))[1] first_total,
   (array_agg(s.available_bytes ORDER BY r."completedAtUtc",r."runId",s.observed_at))[1] first_available,
   (array_agg(s.total_bytes ORDER BY r."completedAtUtc" DESC,r."runId" DESC,s.observed_at DESC))[1] last_total,
   (array_agg(s.available_bytes ORDER BY r."completedAtUtc" DESC,r."runId" DESC,s.observed_at DESC))[1] last_available,
   count(*) FILTER(WHERE s.total_bytes IS NULL OR s.available_bytes IS NULL) unknown_count,
   min(s.total_bytes) minimum_total,max(s.total_bytes) maximum_total,
   bool_or(r."isGap") run_gap,v.run_count revision_runs,v.has_gap revision_gap
  FROM runs r JOIN telemetry.sql_volume_snapshot s ON s.run_id=r."runId" AND s.target_revision=r."sampleTargetRevision"
   AND s.instance_id=p_instance_id AND s.collected_at<=p_cutoff
  JOIN revisions v ON v.sample_revision=s.target_revision
  GROUP BY s.target_revision,s.volume_key,v.run_count,v.has_gap
  ORDER BY s.target_revision,s.volume_key LIMIT 1001
 ), classified AS(
  SELECT *,CASE WHEN identity_kind<>'volume_id' OR identity_kinds<>1 THEN 'identity_not_comparable'
   WHEN observation_count<2 THEN 'insufficient_observations' WHEN unknown_count>0 THEN 'capacity_unknown'
   WHEN minimum_total<>maximum_total THEN 'capacity_changed'
   WHEN run_gap OR revision_gap OR observation_count<>revision_runs OR v_complete<>v_run_count OR v_revision_boundary THEN 'visibility_gaps' ELSE 'eligible' END comparison
  FROM identities
 ) SELECT count(*)::integer,coalesce(jsonb_agg(jsonb_build_object('sampleTargetRevision',target_revision,'volumeKey',encode(volume_key,'hex'),
   'identityKind',identity_kind,'observationCount',observation_count,'firstCompletedAtUtc',first_completed,'lastCompletedAtUtc',last_completed,
   'firstTotalBytes',first_total::text,'firstAvailableBytes',first_available::text,'lastTotalBytes',last_total::text,'lastAvailableBytes',last_available::text,
   'comparisonState',comparison,'availableBytesDelta',CASE WHEN comparison='eligible' THEN (last_available-first_available)::text END,
   'totalBytesDelta',CASE WHEN comparison='eligible' THEN (last_total-first_total)::text END) ORDER BY target_revision,volume_key),'[]'::jsonb)
 INTO v_identity_count,v_catalog FROM classified;
 IF v_identity_count>1000 THEN RAISE EXCEPTION 'volume identities exceed bounded whole window; choose a narrower window' USING ERRCODE='54000'; END IF;
 -- Completion digests bind immutable source payloads. Complete retained counts
 -- and the full identity inventory bind retention/membership, including pages
 -- not yet read, without building multi-megabyte copies of every observation.
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','sql-volume-window-v1','instanceId',p_instance_id,
  'targetRevision',p_target_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',p_cutoff,'runs',v_runs,
  'catalog',v_catalog,'policy',v_policy,'elapsedGapInterval',v_gap_interval)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('runs',v_runs,'catalog',v_catalog,'readEpoch',v_epoch,'revisionBoundary',v_revision_boundary,'coverage',jsonb_build_object(
  'runCount',v_run_count,'completeRunCount',v_complete,'gapRunCount',v_run_count-v_complete,
  'retainedObservationCount',v_observations,'retentionDays',v_policy->'days','hasVisibilityGap',v_complete<>v_run_count OR v_revision_boundary));
END $manifest$;

CREATE FUNCTION reporting.read_sql_volume_window_history(
 p_instance_id uuid,p_target_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,
 p_sample_revision bigint,p_volume_key bytea,p_cursor jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $read$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_kind text;
 v_manifest jsonb;v_epoch text;v_coverage jsonb;v_items jsonb;v_next jsonb;v_page jsonb;v_run_count integer;v_complete integer;
 v_observations integer;v_identity_kind text;v_after_revision bigint;v_after_key text;v_after_completed timestamptz;v_after_run uuid;
 v_has_more boolean;v_last jsonb;v_current_revision bigint;v_revision_boundary boolean;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN
  RAISE EXCEPTION 'SQL volume target scope required' USING ERRCODE='42501';
 END IF;
 IF p_instance_id IS NULL OR p_instance_id='00000000-0000-0000-0000-000000000000' OR p_target_revision IS NULL
  OR p_target_revision NOT BETWEEN 1 AND 9007199254740991 OR p_from IS NULL OR p_to IS NULL
  OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '31 days'
  OR ((p_sample_revision IS NULL)<>(p_volume_key IS NULL)) OR p_limit IS NULL
  OR (p_sample_revision IS NOT NULL AND (p_sample_revision NOT BETWEEN 1 AND p_target_revision OR octet_length(p_volume_key)<>32))
  OR p_limit NOT BETWEEN 1 AND (CASE WHEN p_sample_revision IS NULL THEN 100 ELSE 250 END)
 THEN RAISE EXCEPTION 'volume history request bounds invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN
  RAISE EXCEPTION 'volume history requires coherent repeatable read' USING ERRCODE='55000';
 END IF;
 SELECT revision INTO v_current_revision FROM control.observation_target WHERE instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_current_revision<>p_target_revision THEN RAISE EXCEPTION 'volume target revision changed' USING ERRCODE='40001'; END IF;
 v_kind:=CASE WHEN p_sample_revision IS NULL THEN 'catalog' ELSE 'history' END;
 IF p_cursor IS NULL THEN v_cutoff:=v_now;v_issued:=v_now;
 ELSE
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>15
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('kind','instanceId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','sampleTargetRevision','volumeKey','afterSampleTargetRevision','afterVolumeKey','afterCompletedAtUtc','afterRunId'))
   OR p_cursor->>'kind' IS DISTINCT FROM v_kind OR (p_cursor->>'instanceId')::uuid IS DISTINCT FROM p_instance_id
   OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
   OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to
   OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit
   OR (p_cursor->>'sampleTargetRevision')::bigint IS DISTINCT FROM p_sample_revision
   OR p_cursor->>'volumeKey' IS DISTINCT FROM encode(p_volume_key,'hex')
   OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'volume cursor context invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued)
   OR v_cutoff>v_issued OR v_issued>v_now THEN RAISE EXCEPTION 'volume cursor timing invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'volume cursor expired' USING ERRCODE='40001'; END IF;
  v_after_revision:=(p_cursor->>'afterSampleTargetRevision')::bigint;v_after_key:=p_cursor->>'afterVolumeKey';
  v_after_completed:=(p_cursor->>'afterCompletedAtUtc')::timestamptz;v_after_run:=(p_cursor->>'afterRunId')::uuid;
  IF (v_kind='catalog' AND (v_after_revision IS NULL OR coalesce(v_after_key,'') !~ '^[0-9a-f]{64}$' OR v_after_completed IS NOT NULL OR v_after_run IS NOT NULL))
   OR (v_kind='history' AND (v_after_completed IS NULL OR v_after_run IS NULL OR v_after_revision IS NOT NULL OR v_after_key IS NOT NULL))
  THEN RAISE EXCEPTION 'volume cursor anchor invalid' USING ERRCODE='22023'; END IF;
 END IF;
 v_manifest:=reporting.sql_volume_window_manifest(p_instance_id,p_target_revision,p_from,p_to,v_cutoff);v_epoch:=v_manifest->>'readEpoch';
 IF p_cursor IS NOT NULL AND v_epoch IS DISTINCT FROM p_cursor->>'readEpoch' THEN
  RAISE EXCEPTION 'volume history manifest changed' USING ERRCODE='40001';
 END IF;
 v_coverage:=v_manifest->'coverage';
 v_revision_boundary:=(v_manifest->>'revisionBoundary')::boolean;
 IF v_kind='catalog' THEN
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'catalog') c
    WHERE (c->>'sampleTargetRevision')::bigint=v_after_revision AND c->>'volumeKey'=v_after_key) THEN
   RAISE EXCEPTION 'volume catalog anchor unavailable' USING ERRCODE='22023';
  END IF;
  SELECT coalesce(jsonb_agg(c ORDER BY (c->>'sampleTargetRevision')::bigint,c->>'volumeKey'),'[]'::jsonb) INTO v_items
  FROM(SELECT c FROM jsonb_array_elements(v_manifest->'catalog') c
   WHERE v_after_revision IS NULL OR ((c->>'sampleTargetRevision')::bigint,c->>'volumeKey')>(v_after_revision,v_after_key)
   ORDER BY (c->>'sampleTargetRevision')::bigint,c->>'volumeKey' LIMIT p_limit+1) q;
 ELSE
  SELECT c->>'identityKind' INTO v_identity_kind FROM jsonb_array_elements(v_manifest->'catalog') c
   WHERE (c->>'sampleTargetRevision')::bigint=p_sample_revision AND c->>'volumeKey'=encode(p_volume_key,'hex');
  IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_manifest->'runs') r
    WHERE (r->>'sampleTargetRevision')::bigint=p_sample_revision AND (r->>'completedAtUtc')::timestamptz=v_after_completed AND (r->>'runId')::uuid=v_after_run) THEN
   RAISE EXCEPTION 'volume history anchor unavailable' USING ERRCODE='22023';
  END IF;
  WITH source AS MATERIALIZED(
   SELECT r,s.observed_at,s.collected_at,s.mapped_file_count,s.total_bytes,s.available_bytes,
    CASE WHEN r->>'outcome' NOT IN('succeeded','partial') THEN 'collection_failed'
     WHEN r->>'lossKind'<>'none' OR r->>'outcome'='partial' THEN 'collection_loss'
     WHEN (r->>'retainedCount')::integer<>(r->>'insertedItemCount')::integer OR (r->>'outputSchemaVersion')::integer<>1 OR (r->>'retainedCount')::integer=0 THEN 'stored_count_mismatch'
     WHEN s.run_id IS NULL THEN 'volume_absent' WHEN s.total_bytes IS NULL OR s.available_bytes IS NULL THEN 'capacity_unknown'
     WHEN s.observed_at>v_cutoff OR abs(extract(epoch FROM s.observed_at-s.collected_at))>300 THEN 'source_clock_skew'
     WHEN (r->>'elapsedGap')::boolean THEN 'collection_gap' ELSE 'observed_capacity' END sample_reason
   FROM jsonb_array_elements(v_manifest->'runs') r LEFT JOIN LATERAL(
    SELECT s.* FROM telemetry.sql_volume_snapshot s WHERE s.run_id=(r->>'runId')::uuid AND s.instance_id=p_instance_id
     AND s.target_revision=p_sample_revision AND s.volume_key=p_volume_key AND s.collected_at<=v_cutoff
    ORDER BY s.observed_at DESC LIMIT 1
   ) s ON true WHERE (r->>'sampleTargetRevision')::bigint=p_sample_revision
  ), samples AS MATERIALIZED(
   SELECT r,jsonb_build_object('runId',r->'runId','completedAtUtc',r->'completedAtUtc','observedAtUtc',observed_at,'collectedAtUtc',collected_at,
    'outcome',r->'outcome','lossKind',r->'lossKind','minimumLostItems',r->'minimumLostItems',
    'state',CASE WHEN sample_reason='observed_capacity' THEN 'available' WHEN observed_at IS NOT NULL THEN 'partial' ELSE 'unavailable' END,
    'reason',sample_reason,'mappedFileCount',mapped_file_count,'totalBytes',total_bytes::text,'availableBytes',available_bytes::text) item,
    sample_reason FROM source
  ) SELECT count(*)::integer,count(*) FILTER(WHERE sample_reason='observed_capacity')::integer,
   count(*) FILTER(WHERE item->>'observedAtUtc' IS NOT NULL)::integer,
   coalesce((SELECT jsonb_agg(item ORDER BY (r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)
    FROM(SELECT item,r FROM samples WHERE v_after_completed IS NULL OR ((r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid)>(v_after_completed,v_after_run)
     ORDER BY (r->>'completedAtUtc')::timestamptz,(r->>'runId')::uuid LIMIT p_limit+1) page),'[]'::jsonb)
   INTO v_run_count,v_complete,v_observations,v_items FROM samples;
  v_revision_boundary:=v_revision_boundary OR (v_run_count=0 AND (v_coverage->>'runCount')::integer>0);
  v_coverage:=jsonb_build_object('runCount',v_run_count,'completeRunCount',v_complete,'gapRunCount',v_run_count-v_complete,
   'retainedObservationCount',v_observations,'retentionDays',v_coverage->'retentionDays','hasVisibilityGap',v_complete<>v_run_count OR v_revision_boundary);
 END IF;
 v_has_more:=jsonb_array_length(v_items)>p_limit;
 IF v_has_more THEN v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('kind',v_kind,'instanceId',p_instance_id,'targetRevision',p_target_revision,'fromUtc',p_from,'toUtc',p_to,
   'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,'sampleTargetRevision',p_sample_revision,'volumeKey',encode(p_volume_key,'hex'),
   'afterSampleTargetRevision',CASE WHEN v_kind='catalog' THEN (v_last->>'sampleTargetRevision')::bigint END,
   'afterVolumeKey',CASE WHEN v_kind='catalog' THEN v_last->>'volumeKey' END,'afterCompletedAtUtc',CASE WHEN v_kind='history' THEN v_last->>'completedAtUtc' END,
   'afterRunId',CASE WHEN v_kind='history' THEN v_last->>'runId' END);
 END IF;
 v_page:=jsonb_build_object('instanceId',p_instance_id,'targetRevision',p_target_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,
  'readEpoch',v_epoch,'repositoryTimeUtc',v_now,'coverage',v_coverage,'items',v_items,'nextAnchor',v_next,
  'state',CASE WHEN (v_coverage->>'runCount')::integer=0 OR (v_coverage->>'retainedObservationCount')::integer=0 THEN 'unavailable'
    WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'partial' ELSE 'available' END,
  'reason',CASE WHEN v_revision_boundary THEN 'revision_boundary' WHEN (v_coverage->>'runCount')::integer=0 THEN 'no_completed_runs'
    WHEN (v_coverage->>'retainedObservationCount')::integer=0 THEN 'retained_evidence_unavailable'
    WHEN (v_coverage->>'hasVisibilityGap')::boolean THEN 'visibility_gaps' ELSE 'retained_window' END);
 IF v_kind='history' THEN v_page:=v_page||jsonb_build_object('sampleTargetRevision',p_sample_revision,'volumeKey',encode(p_volume_key,'hex'),'identityKind',v_identity_kind); END IF;
 RETURN v_page;
END $read$;

REVOKE ALL ON FUNCTION reporting.sql_volume_window_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.read_sql_volume_window_history(uuid,bigint,timestamptz,timestamptz,integer,bigint,bytea,jsonb)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_volume_window_history(uuid,bigint,timestamptz,timestamptz,integer,bigint,bytea,jsonb) TO sqlobserver_server;
