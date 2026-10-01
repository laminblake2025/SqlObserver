-- Staged v3 atomic content lifecycle. No runtime grant, catalog activation or schedule change.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.commit_query_performance_v3(
 p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_schedule_revision bigint,
 p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
 p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,p_attempt_count integer,
 p_source_row_count integer,p_output_item_count integer,p_response_bytes bigint,p_output_bytes bigint,
 p_loss_kind text,p_minimum_lost_items integer,p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,
 p_next_circuit_state text,p_next_consecutive_failures integer,p_completion_digest bytea,
 p_window_start timestamptz,p_window_end timestamptz,p_source events.query_performance_source,
 p_source_state text,p_coverage text,p_freshness boolean,p_truncated boolean,
 p_query_payload jsonb,p_cache_payload jsonb,p_content_payload jsonb,p_text_links jsonb,p_plan_links jsonb,p_wait_snapshots jsonb)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,
 rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET row_security=on
AS $commit$
DECLARE
 v_run telemetry.collection_run%ROWTYPE; v_prior telemetry.collection_run_outcome%ROWTYPE;
 v_schedule control.collector_schedule%ROWTYPE; v_contract control.collector_contract%ROWTYPE;
 v_now timestamptz:=clock_timestamp(); v_failure boolean; v_digest bytea;
 v_query_items integer; v_cache_items integer:=0; v_cache_inserted integer:=0; v_content_items integer:=0; v_content_inserted integer:=0; v_content_bytes bigint:=0; v_query_inserted integer:=0;
 v_inserted integer:=0; v_bytes integer:=0; v_expected_bytes bigint; v_source_minimum bigint;
 v_write_v3 boolean; v_mirror integer; v_deleted integer;
 v_context text:=current_setting('sqlobserver.retention_context',true);
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN
  RAISE EXCEPTION 'Query performance v3 requires exact target scope' USING ERRCODE='42501'; END IF;
 IF p_run_id IS NULL OR p_run_id='00000000-0000-0000-0000-000000000000'::uuid
    OR p_instance_id IS NULL OR p_instance_id='00000000-0000-0000-0000-000000000000'::uuid
    OR p_target_revision IS NULL OR p_target_revision<=0 OR p_schedule_revision IS NULL OR p_schedule_revision<=0
    OR p_scheduled_at IS NULL OR NOT isfinite(p_scheduled_at) OR p_owner_execution_id IS NULL
    OR p_fencing_token IS NULL OR p_fencing_token<=0
    OR p_work_key IS DISTINCT FROM 'collector/run/queries.performance/'||replace(p_instance_id::text,'-','')
    OR octet_length(p_request_digest) IS DISTINCT FROM 32 OR octet_length(p_completion_digest) IS DISTINCT FROM 32
    OR p_outcome IS NULL OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure',
       'permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
    OR p_reason_code IS NULL OR octet_length(p_reason_code) NOT BETWEEN 1 AND 128
    OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
    OR p_attempt_count IS NULL OR (p_outcome='circuit_open' AND p_attempt_count<>0)
    OR (p_outcome<>'circuit_open' AND p_attempt_count NOT BETWEEN 1 AND 2)
    OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 514777
    OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 22008
    OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 8388608
    OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 8388608
    OR p_loss_kind IS NULL OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit',
       'output_validation_failure','ingestion_rejection','duplicate_overlap','visibility_incomplete')
    OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 514777
    OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 8388608
    OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL
    OR p_next_circuit_state NOT IN ('closed','open','half_open')
    OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
    OR (p_loss_kind='none' AND (p_minimum_lost_items<>0 OR p_minimum_lost_bytes<>0 OR NOT p_loss_count_is_exact))
    OR (p_loss_kind<>'none' AND p_minimum_lost_items=0 AND p_minimum_lost_bytes=0)
    OR (p_outcome='succeeded' AND (p_loss_kind<>'none' OR p_reason_code<>'completed'))
    OR (p_outcome='partial' AND (p_loss_kind='none' OR p_reason_code NOT IN
       ('source_row_limit','response_byte_limit','overlap_deduplicated','degraded','visibility_incomplete')))
    OR (p_outcome='circuit_open' AND (p_loss_kind<>'none' OR p_reason_code<>'circuit_currently_open'))
    OR (p_outcome='lease_lost' AND (p_loss_kind<>'none' OR p_reason_code<>'lease_ownership_lost'))
    OR (p_outcome='output_invalid' AND (p_loss_kind<>'output_validation_failure' OR p_reason_code<>'output_validation_failed'
       OR p_minimum_lost_items>22008))
    OR (p_outcome='timed_out' AND (p_loss_kind<>'none' OR p_reason_code<>'deadline_exceeded'))
    OR (p_outcome='transient_failure' AND (p_loss_kind<>'none' OR p_reason_code<>'transient_target_failure'))
    OR (p_outcome='permanent_failure' AND (p_loss_kind<>'none' OR p_reason_code<>'permanent_target_failure'))
    OR (p_outcome='permission_denied' AND (p_loss_kind<>'none' OR p_reason_code<>'required_permission_missing'))
    OR (p_outcome='unsupported' AND (p_loss_kind<>'none' OR p_reason_code NOT IN
       ('target_unsupported','target_version_unsupported','target_platform_unsupported','target_edition_unsupported',
        'capability_missing','capability_profile_missing','capability_profile_stale')))
    OR p_window_start IS NULL OR p_window_end IS NULL OR NOT isfinite(p_window_start) OR NOT isfinite(p_window_end)
    OR p_window_end<=p_window_start OR p_window_end-p_window_start>interval '7 days'
    OR p_source IS NULL OR p_source_state IS NULL OR p_coverage IS NULL OR p_freshness IS NULL OR p_truncated IS NULL
    OR jsonb_typeof(p_query_payload) IS DISTINCT FROM 'object'
    OR jsonb_typeof(p_query_payload->'observations') IS DISTINCT FROM 'array'
    OR jsonb_typeof(p_query_payload->'databaseStatuses') IS DISTINCT FROM 'array'
    OR jsonb_typeof(p_text_links) IS DISTINCT FROM 'array' OR jsonb_typeof(p_plan_links) IS DISTINCT FROM 'array'
    OR jsonb_typeof(p_wait_snapshots) IS DISTINCT FROM 'array' THEN
  RAISE EXCEPTION 'Query performance v3 lifecycle bounds rejected' USING ERRCODE='22023'; END IF;
 IF EXISTS(SELECT 1 FROM jsonb_object_keys(p_query_payload) f(name)
     WHERE f.name NOT IN ('observations','databaseStatuses','targetStatus'))
    OR jsonb_array_length(p_query_payload->'observations')>20000
    OR jsonb_array_length(p_query_payload->'databaseStatuses')>256
    OR jsonb_array_length(p_text_links)>64 OR jsonb_array_length(p_plan_links)>64
    OR jsonb_array_length(p_wait_snapshots)>8
    OR octet_length(p_query_payload::text)+coalesce(octet_length(p_cache_payload::text),0)
       +coalesce(octet_length(p_content_payload::text),0)
       +octet_length(p_text_links::text)+octet_length(p_plan_links::text)+octet_length(p_wait_snapshots::text)>8388608
    OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_query_payload->'observations') item(value)
       WHERE item.value->>'source' IS DISTINCT FROM 'query_store'
          OR item.value->>'semantics' IS DISTINCT FROM 'query_store_interval') THEN
  RAISE EXCEPTION 'Query performance v3 source envelope rejected' USING ERRCODE='22023'; END IF;
 v_failure:=p_outcome NOT IN ('succeeded','partial');
 v_query_items:=jsonb_array_length(p_query_payload->'observations');
 IF v_failure AND (p_source_state<>'unavailable' OR p_coverage<>'unavailable' OR p_freshness
     OR jsonb_typeof(p_query_payload->'targetStatus') IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_query_payload->'targetStatus'->'status') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_query_payload->'targetStatus'->'reason') IS DISTINCT FROM 'string'
     OR (SELECT count(*) FROM jsonb_object_keys(p_query_payload->'targetStatus'))<>2
     OR octet_length(p_query_payload->'targetStatus'->>'status') NOT BETWEEN 1 AND 64
     OR octet_length(p_query_payload->'targetStatus'->>'reason') NOT BETWEEN 1 AND 128) THEN
  RAISE EXCEPTION 'Query performance v3 failure visibility is missing' USING ERRCODE='22023'; END IF;
 IF p_cache_payload IS NOT NULL THEN
  IF jsonb_typeof(p_cache_payload) IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_cache_payload->'items') IS DISTINCT FROM 'array' THEN
   RAISE EXCEPTION 'Query performance v3 cache frame rejected' USING ERRCODE='22023'; END IF;
  v_cache_items:=jsonb_array_length(p_cache_payload->'items');
  IF v_cache_items>2000 OR v_failure THEN
   RAISE EXCEPTION 'Query performance v3 cache output differs from outcome' USING ERRCODE='22023'; END IF;
  IF (p_outcome='succeeded' AND (p_cache_payload->'sourceExhaustive' IS DISTINCT FROM 'true'::jsonb
          OR p_cache_payload->'unqualifiedRows' IS DISTINCT FROM '0'::jsonb))
     OR (p_cache_payload->>'candidateRows')::integer>(p_cache_payload->>'candidateLimit')::integer AND p_loss_count_is_exact
     OR p_minimum_lost_items<greatest(
        greatest((p_cache_payload->>'candidateRows')::integer-(p_cache_payload->>'candidateLimit')::integer,0),
        (p_cache_payload->>'unresolvedRows')::integer+(p_cache_payload->>'invisibleDatabaseRows')::integer
          +(p_cache_payload->>'unqualifiedRows')::integer) THEN
   RAISE EXCEPTION 'Query performance v3 cache coverage is not accounted' USING ERRCODE='22023'; END IF;
 END IF;
 -- Every counter frame carries an explicit content capture receipt, including
 -- disabled/no-key frames. Query Store-only and failed runs carry neither.
 IF (p_content_payload IS NULL)<>(p_cache_payload IS NULL) THEN
  RAISE EXCEPTION 'Query performance v3 content capture receipt is missing or unbound' USING ERRCODE='22023'; END IF;
 IF p_content_payload IS NOT NULL THEN
  IF v_failure OR jsonb_typeof(p_content_payload) IS DISTINCT FROM 'object'
    OR jsonb_typeof(p_content_payload->'items') IS DISTINCT FROM 'array'
    OR octet_length(p_content_payload::text)>32768 THEN
   RAISE EXCEPTION 'Query performance v3 content envelope rejected' USING ERRCODE='22023'; END IF;
  v_content_items:=jsonb_array_length(p_content_payload->'items');
  IF v_content_items>8 THEN RAISE EXCEPTION 'Query performance v3 content cardinality rejected' USING ERRCODE='22023'; END IF;
  SELECT coalesce(sum(CASE WHEN item.value->'text'->>'state'='available' THEN (item.value->'text'->>'ciphertextBytes')::bigint ELSE 0 END
    +CASE WHEN item.value->'plan'->>'state'='available' THEN (item.value->'plan'->>'ciphertextBytes')::bigint ELSE 0 END),0)
   INTO v_content_bytes FROM jsonb_array_elements(p_content_payload->'items') item(value);
  IF v_content_bytes NOT BETWEEN 0 AND 2097152 THEN
   RAISE EXCEPTION 'Query performance v3 protected byte budget rejected' USING ERRCODE='22023'; END IF;
 END IF;
 v_source_minimum:=coalesce((SELECT sum((item.value->>'sourceRowsRead')::bigint)
     FROM jsonb_array_elements(p_query_payload->'databaseStatuses') item(value)),0)
     +coalesce((p_cache_payload->>'candidateRows')::bigint,0)+coalesce((p_content_payload->>'attemptedEntries')::bigint,0);
 v_expected_bytes:=256::bigint*v_query_items+coalesce((SELECT sum(jsonb_array_length(item.value->'categories'))*24
     FROM jsonb_array_elements(p_wait_snapshots) item(value)),0)
     +CASE WHEN p_cache_payload IS NULL THEN 0 ELSE 1024+2048::bigint*v_cache_items END
     +CASE WHEN p_content_payload IS NULL THEN 0 ELSE 1024+2176::bigint*v_content_items+v_content_bytes END;
 IF p_source_row_count<v_source_minimum OR p_output_item_count<>v_query_items+v_cache_items+v_content_items
    OR p_output_bytes<>v_expected_bytes
    OR (v_failure AND (v_query_items<>0 OR p_output_item_count<>0 OR p_output_bytes<>0
        OR p_text_links<>'[]'::jsonb OR p_plan_links<>'[]'::jsonb OR p_wait_snapshots<>'[]'::jsonb)) THEN
  RAISE EXCEPTION 'Query performance v3 output or source accounting differs' USING ERRCODE='22023'; END IF;
 -- Include every mutable summary, circuit, source and sidecar field. A caller-supplied
 -- digest alone must never authorize a different replay or changed next circuit.
 v_digest:=sha256(p_completion_digest||sha256(convert_to(jsonb_build_array(
     p_outcome,p_reason_code,p_duration_ms,p_attempt_count,p_source_row_count,p_output_item_count,
     p_response_bytes,p_output_bytes,p_loss_kind,p_minimum_lost_items,p_loss_count_is_exact,p_minimum_lost_bytes,
     p_next_circuit_state,p_next_consecutive_failures,p_window_start,p_window_end,p_source,p_source_state,
     p_coverage,p_freshness,p_truncated,p_query_payload,p_cache_payload,p_content_payload,p_text_links,p_plan_links,p_wait_snapshots)::text,'UTF8')));
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO v_run FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR v_run.instance_id<>p_instance_id OR v_run.target_revision<>p_target_revision
    OR v_run.collector_id<>'queries.performance' OR v_run.collector_version<>3 OR v_run.output_schema_version<>3
    OR v_run.schedule_revision<>p_schedule_revision OR v_run.scheduled_for<>p_scheduled_at
    OR v_run.work_key<>p_work_key OR v_run.owner_execution_id<>p_owner_execution_id
    OR v_run.fencing_token<>p_fencing_token OR v_run.request_digest<>p_request_digest THEN
  RAISE EXCEPTION 'Query performance v3 run identity differs' USING ERRCODE='55000'; END IF;
 SELECT * INTO v_contract FROM control.collector_contract WHERE collector_id='queries.performance' AND collector_version=3;
 IF NOT FOUND OR v_contract.execution_order<>9 OR v_contract.manifest_schema_version<>9 OR v_contract.output_schema_version<>3
    OR v_contract.manifest_sha256<>decode('071ba07098afdf427846cd828439bcd823aa4863330829429f77801549a5ee7c','hex')
    OR v_contract.asset_bundle_sha256<>decode('8a707984375b0d29ca144ded142b836c0ece121d419508601e64179169a631e8','hex')
    OR v_contract.default_interval<>interval '5 minutes' OR v_contract.minimum_interval<>interval '1 minute'
    OR v_contract.execution_timeout<>interval '2 minutes' OR v_contract.maximum_rows<>22008
    OR v_contract.maximum_response_bytes<>8388608 OR v_contract.estimated_cost<>'moderate'
    OR v_contract.maximum_attempts<>2 OR v_contract.circuit_failure_threshold<>3 OR v_contract.circuit_open_interval<>interval '5 minutes' THEN
  RAISE EXCEPTION 'Query performance v3 immutable contract differs' USING ERRCODE='55000'; END IF;
 SELECT * INTO v_prior FROM telemetry.collection_run_outcome WHERE run_id=p_run_id FOR UPDATE;
 IF FOUND THEN
  IF v_prior.completion_digest<>v_digest THEN
   RAISE EXCEPTION 'Query performance v3 completion replay differs' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,v_prior.completed_at; RETURN;
 END IF;
 PERFORM 1 FROM control.observation_target WHERE instance_id=p_instance_id AND revision=p_target_revision
     AND lifecycle_state='active' AND host_name IS NOT NULL FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Query performance v3 target is not active at the expected revision' USING ERRCODE='55000'; END IF;
 SELECT * INTO v_schedule FROM control.collector_schedule WHERE instance_id=p_instance_id AND collector_id='queries.performance' FOR UPDATE;
 IF NOT FOUND OR NOT v_schedule.enabled OR v_schedule.collector_version<>3 OR v_schedule.active_run_id IS DISTINCT FROM p_run_id
    OR v_schedule.target_revision<>p_target_revision OR v_schedule.schedule_revision<>p_schedule_revision
    OR v_schedule.next_due_at<>p_scheduled_at THEN
  RAISE EXCEPTION 'Query performance v3 schedule differs' USING ERRCODE='55000'; END IF;
 IF (p_outcome IN ('succeeded','partial') AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
    OR (p_outcome IN ('timed_out','transient_failure') AND (p_next_consecutive_failures<>v_schedule.consecutive_failure_count+1
       OR p_next_circuit_state<>CASE WHEN v_schedule.circuit_state='half_open'
            OR v_schedule.consecutive_failure_count+1>=v_contract.circuit_failure_threshold THEN 'open' ELSE 'closed' END))
    OR (p_outcome='circuit_open' AND (p_next_circuit_state<>'open' OR p_next_consecutive_failures<>v_schedule.consecutive_failure_count))
    OR (p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','circuit_open')
       AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0)) THEN
  RAISE EXCEPTION 'Query performance v3 circuit transition differs' USING ERRCODE='22023'; END IF;
 -- Preserve the existing Query Store writer, protected links, wait sidecars and
 -- v3 mirror routing. Cache tuples have separate additive history and accounting;
 -- they are not passed to the legacy cumulative-cache writer.
 IF EXISTS(SELECT 1 FROM events.query_performance_run WHERE collection_run_id=p_run_id)
    OR EXISTS(SELECT 1 FROM events.plan_cache_runtime_receipt WHERE instance_id=p_instance_id AND run_id=p_run_id)
    OR EXISTS(SELECT 1 FROM events.plan_cache_content_receipt WHERE instance_id=p_instance_id AND run_id=p_run_id)
    OR EXISTS(SELECT 1 FROM events.plan_cache_content_link WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN
  RAISE EXCEPTION 'Query performance v3 found evidence without a canonical completion' USING ERRCODE='55000'; END IF;
 LOCK TABLE events.query_performance_observation IN ROW EXCLUSIVE MODE;
 SELECT route.write_v3 INTO STRICT v_write_v3 FROM system.query_observation_v3_read_route route WHERE route.singleton;
 PERFORM control.commit_query_performance_collection_run(p_run_id,p_instance_id,p_target_revision,v_digest,
     p_window_start,p_window_end,p_source,p_source_state,p_coverage,p_freshness,p_truncated,p_fencing_token,p_query_payload);
 SELECT count(*)::integer INTO v_query_inserted FROM events.query_performance_observation WHERE collection_run_id=p_run_id;
 IF p_cache_payload IS NOT NULL THEN
  v_cache_inserted:=events.apply_plan_cache_runtime_batch(p_run_id,p_instance_id,p_target_revision,p_cache_payload);
  IF v_cache_inserted<>v_cache_items THEN
   RAISE EXCEPTION 'Query performance v3 found a cache receipt without a canonical completion' USING ERRCODE='55000'; END IF;
 END IF;
 IF p_content_payload IS NOT NULL THEN
  v_content_inserted:=events.apply_plan_cache_content_batch(p_run_id,p_instance_id,p_target_revision,p_content_payload);
  IF v_content_inserted<>v_content_items THEN
   RAISE EXCEPTION 'Query performance v3 content receipt lacks canonical completion' USING ERRCODE='55000'; END IF;
 END IF;
 PERFORM control.commit_query_text_links(p_run_id,p_instance_id,p_fencing_token,p_text_links);
 PERFORM control.commit_query_plan_links(p_run_id,p_instance_id,p_fencing_token,p_plan_links);
 PERFORM control.commit_query_plan_wait_snapshots(p_run_id,p_instance_id,p_fencing_token,p_wait_snapshots);
 IF v_write_v3 THEN
  SELECT count(*)::integer INTO v_mirror FROM events.query_performance_observation_v3
      WHERE instance_id=p_instance_id AND collection_run_id=p_run_id;
  IF v_mirror<>v_query_inserted THEN RAISE EXCEPTION 'Query performance v3 mirror differs' USING ERRCODE='55000'; END IF;
  PERFORM set_config('sqlobserver.retention_context','envelope_cascade',true);
  DELETE FROM events.query_performance_observation WHERE instance_id=p_instance_id AND collection_run_id=p_run_id;
  GET DIAGNOSTICS v_deleted=ROW_COUNT;
  PERFORM set_config('sqlobserver.retention_context',coalesce(v_context,''),true);
  IF v_deleted<>v_query_inserted THEN RAISE EXCEPTION 'Query performance v3 original retirement differs' USING ERRCODE='55000'; END IF;
 END IF;
 v_inserted:=v_query_inserted+v_cache_inserted+v_content_inserted;
 IF v_inserted>p_output_item_count THEN RAISE EXCEPTION 'Query performance v3 inserted accounting differs' USING ERRCODE='55000'; END IF;
 v_bytes:=CASE WHEN v_inserted>0 OR p_cache_payload IS NOT NULL THEN p_output_bytes ELSE 0 END;
 IF p_outcome<>'succeeded' OR p_loss_kind<>'none' THEN
  INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,
     lost_row_count,lost_byte_count,count_is_exact,recorded_at)
  VALUES(gen_random_uuid(),p_run_id,p_instance_id,'queries.performance',p_reason_code,p_scheduled_at,v_now,
     greatest(p_minimum_lost_items,1),p_minimum_lost_bytes,
     CASE WHEN p_minimum_lost_items=0 THEN false ELSE p_loss_count_is_exact END,v_now);
 END IF;
 INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,
     output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,
     truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
 VALUES(p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),p_duration_ms,p_source_row_count,
     p_output_item_count+CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,v_inserted,
     p_output_item_count-v_inserted,CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
     p_response_bytes,p_output_bytes,v_bytes,p_truncated OR p_outcome='partial',p_loss_kind<>'none',p_loss_kind,
     p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,v_digest,v_now);
 UPDATE control.collector_schedule SET active_run_id=NULL,
     next_due_at=v_now+CASE WHEN p_next_circuit_state='open' THEN v_contract.circuit_open_interval ELSE collection_interval END,
     circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,
     circuit_open_until=CASE WHEN p_next_circuit_state='open' THEN v_now+v_contract.circuit_open_interval ELSE NULL END,
     last_completed_at=v_now,last_succeeded_at=CASE WHEN p_outcome='succeeded' THEN v_now ELSE last_succeeded_at END,
     last_outcome=p_outcome,updated_at=v_now WHERE instance_id=p_instance_id AND collector_id='queries.performance';
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 'committed',v_inserted,p_output_item_count-v_inserted,
     CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,v_bytes,v_now;
END $commit$;
REVOKE ALL ON FUNCTION control.commit_query_performance_v3(
 uuid,uuid,bigint,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,
 text,integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,events.query_performance_source,
 text,text,boolean,boolean,jsonb,jsonb,jsonb,jsonb,jsonb,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
