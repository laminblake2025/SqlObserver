-- Correct populated JSON bindings, failure branches, and deadlock conflict binding.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE OR REPLACE FUNCTION control.commit_m9_collection_run(
 p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_id text,
 p_collector_version integer,p_output_schema_version integer,p_schedule_revision bigint,
 p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
 p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,
 p_attempt_count integer,p_source_row_count integer,p_output_item_count integer,
 p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
 p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,
 p_next_consecutive_failures integer,p_payload jsonb,p_completion_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,telemetry,control AS $fn$
DECLARE run_row telemetry.collection_run%ROWTYPE; schedule_row control.collector_schedule%ROWTYPE; contract_row control.collector_contract%ROWTYPE; existing telemetry.m9_commit_replay%ROWTYPE; now_utc timestamptz:=clock_timestamp(); n integer:=0; inserted integer:=0; replica_inserted integer:=0; database_inserted integer:=0; event_inserted integer:=0; occurrence_inserted integer:=0; duplicate_count integer:=0; bytes integer:=0; is_failure boolean;
BEGIN
 is_failure := p_outcome IN ('timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open','degraded');
 IF p_collector_id NOT IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health') OR p_collector_version<>1 OR p_output_schema_version<>1 OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_schedule_revision<=0 OR p_scheduled_at IS NULL OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_completion_digest)<>32 OR (NOT is_failure AND (jsonb_typeof(p_payload)<>'object' OR COALESCE(p_payload->>'kind','')<>replace(p_collector_id,'.','_'))) OR (is_failure AND (p_payload IS NULL OR jsonb_typeof(p_payload)<>'object' OR COALESCE(p_payload->>'kind','')<>'m9_failure' OR COALESCE(p_payload->>'outcome','')<>p_outcome OR COALESCE(p_payload->>'reason','')<>p_reason_code OR (p_outcome='output_invalid' AND (COALESCE(p_payload->>'lossKind','')<>'output_validation_failure' OR COALESCE((p_payload->>'rejectedItems')::integer,-1) NOT BETWEEN 0 AND 4096 OR COALESCE((p_payload->>'rejectedBytes')::integer,-1) NOT BETWEEN 0 AND 2097152 OR COALESCE((p_payload->>'minimumLostItems')::integer,-1)<>p_minimum_lost_items OR COALESCE((p_payload->>'minimumLostBytes')::integer,-1)<>p_minimum_lost_bytes)))) OR octet_length(convert_to(COALESCE(p_payload,'{}'::jsonb)::text,'UTF8'))>(CASE WHEN p_collector_id='availability-groups.health' THEN 2097152 ELSE 1048576 END) OR p_source_row_count NOT BETWEEN 0 AND (CASE WHEN p_collector_id='sql-agent.failures' THEN 4096 WHEN p_collector_id='availability-groups.health' THEN 2049 WHEN p_collector_id='backups.status' THEN 1537 ELSE 129 END) OR p_output_item_count NOT BETWEEN 0 AND (CASE WHEN p_collector_id='availability-groups.health' THEN 2048 WHEN p_collector_id='backups.status' THEN 1537 WHEN p_collector_id='sql-agent.failures' THEN 512 ELSE 128 END) OR (is_failure AND p_outcome<>'output_invalid' AND (p_source_row_count<>0 OR p_output_item_count<>0 OR p_response_bytes<>0 OR p_output_bytes<>0 OR p_minimum_lost_items<>0 OR p_minimum_lost_bytes<>0 OR p_loss_kind<>'none')) OR (is_failure AND p_outcome='output_invalid' AND (p_output_item_count<>0 OR p_output_bytes<>0 OR p_loss_kind<>'output_validation_failure' OR p_minimum_lost_items NOT BETWEEN 0 AND 4096 OR p_minimum_lost_bytes NOT BETWEEN 0 AND 2097152)) OR (NOT is_failure AND p_output_item_count IS DISTINCT FROM (CASE WHEN p_collector_id IN ('backups.status','sql-agent.failures') THEN jsonb_array_length(COALESCE(p_payload->'items','[]'::jsonb)) WHEN p_collector_id='tempdb.health' THEN jsonb_array_length(COALESCE(p_payload->'files','[]'::jsonb)) ELSE jsonb_array_length(COALESCE(p_payload->'replicas','[]'::jsonb))+jsonb_array_length(COALESCE(p_payload->'databases','[]'::jsonb)) END)) OR p_response_bytes NOT BETWEEN 0 AND (CASE WHEN p_collector_id='availability-groups.health' THEN 2097152 ELSE 1048576 END) OR p_output_bytes NOT BETWEEN 0 AND (CASE WHEN p_collector_id='availability-groups.health' THEN 2097152 ELSE 1048576 END) OR p_next_circuit_state NOT IN ('closed','open','half_open') OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000 OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open','degraded') OR p_reason_code NOT IN ('completed','source_row_limit','response_byte_limit','deadline_exceeded','transient_target_failure','permanent_target_failure','required_permission_missing','target_unsupported','output_validation_failed','lease_ownership_lost','circuit_currently_open','capability_profile_missing','capability_profile_stale','capability_missing','target_version_unsupported','target_platform_unsupported','target_edition_unsupported','degraded','visibility_incomplete') OR p_attempt_count NOT BETWEEN 1 AND 2 OR p_duration_ms NOT BETWEEN 0 AND 3600000 OR p_minimum_lost_items NOT BETWEEN 0 AND 4096 OR p_minimum_lost_bytes NOT BETWEEN 0 AND 2097152 THEN RAISE EXCEPTION 'M9 commit bounds or contract rejected' USING ERRCODE='22023'; END IF;
 IF NOT is_failure AND COALESCE(p_payload->>'state','') !~ '^[1-6]$' THEN RAISE EXCEPTION 'M9 payload observation state is outside the closed enum' USING ERRCODE='22023'; END IF;
 IF NOT is_failure AND p_collector_id='availability-groups.health' AND COALESCE(p_payload->>'visibilityScope','') !~ '^[1-3]$' THEN RAISE EXCEPTION 'M9 availability visibility scope is outside the closed enum' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.collection_run WHERE run_id=p_run_id FOR UPDATE;
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version OR run_row.output_schema_version<>p_output_schema_version OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at THEN RAISE EXCEPTION 'M9 run identity, target revision, schedule, lease, or request digest mismatch' USING ERRCODE='55000'; END IF;
  SELECT * INTO contract_row FROM control.collector_contract WHERE collector_id=p_collector_id AND collector_version=1;
  IF NOT FOUND OR contract_row.output_schema_version<>1 THEN RAISE EXCEPTION 'M9 immutable collector contract missing' USING ERRCODE='55000'; END IF;
  PERFORM set_config('sqlobserver.target_scope',p_instance_id::text,true);
  -- Replay is an immutable, digest-validated lookup.  It deliberately occurs
  -- before the mutable schedule fence so an identical retry remains replayable
  -- after the first commit cleared active_run_id and advanced next_due_at.
  SELECT * INTO existing FROM telemetry.m9_commit_replay WHERE run_id=p_run_id FOR UPDATE;
  IF FOUND THEN
    IF existing.request_digest<>p_request_digest OR existing.payload_digest<>p_completion_digest THEN RAISE EXCEPTION 'M9 divergent replay digest' USING ERRCODE='40001'; END IF;
    RETURN QUERY SELECT 'replayed',0,0,0,0,existing.committed_at; RETURN;
  END IF;
  SELECT * INTO schedule_row FROM control.collector_schedule WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
  IF NOT FOUND OR schedule_row.active_run_id<>p_run_id OR schedule_row.schedule_revision<>p_schedule_revision OR schedule_row.target_revision<>p_target_revision OR schedule_row.next_due_at<>p_scheduled_at THEN RAISE EXCEPTION 'M9 schedule fence or next-due mismatch' USING ERRCODE='55000'; END IF;
 INSERT INTO telemetry.m9_commit_replay(run_id,instance_id,target_revision,collector_id,collector_version,output_schema_version,request_digest,payload_digest,committed_at,m9_observation_state,m9_visibility_scope) VALUES(p_run_id,p_instance_id,p_target_revision,p_collector_id,1,1,p_request_digest,p_completion_digest,now_utc,CASE WHEN NOT is_failure THEN (p_payload->>'state')::smallint ELSE NULL END,CASE WHEN p_collector_id='availability-groups.health' AND NOT is_failure THEN (p_payload->>'visibilityScope')::smallint ELSE NULL END);
 IF NOT is_failure AND p_collector_id='backups.status' THEN
   SELECT count(*) INTO n FROM jsonb_array_elements(COALESCE(p_payload->'items','[]'::jsonb)); IF n>1537 THEN RAISE EXCEPTION 'backup row bound rejected' USING ERRCODE='22023'; END IF;
   INSERT INTO telemetry.backup_status_snapshot(instance_id,target_revision,run_id,observed_at,database_fingerprint,backup_kind,backup_set_id,last_finish_utc,source_local_finish,source_time_unknown,size_bytes,copy_only,has_checksum,is_damaged,coverage)
   SELECT p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,decode(x."databaseFingerprint",'hex'),x.kind,x."backupSetId",x."lastFinishUtc",x."sourceLocalFinish",x."sourceTimeUnknown",x."sizeBytes",x."copyOnly",x."hasChecksum",x."isDamaged",x.coverage FROM jsonb_to_recordset(COALESCE(p_payload->'items','[]'::jsonb)) AS x("databaseFingerprint" text,kind smallint,"backupSetId" bigint,"lastFinishUtc" timestamptz,"sourceLocalFinish" timestamp,"sourceTimeUnknown" boolean,"sizeBytes" bigint,"copyOnly" boolean,"hasChecksum" boolean,"isDamaged" boolean,coverage smallint);
   GET DIAGNOSTICS inserted=ROW_COUNT; bytes:=inserted*160; duplicate_count:=greatest(p_output_item_count-inserted,0);
 ELSIF NOT is_failure AND p_collector_id='sql-agent.failures' THEN
   SELECT count(*) INTO n FROM jsonb_array_elements(COALESCE(p_payload->'items','[]'::jsonb)); IF n>512 THEN RAISE EXCEPTION 'agent row bound rejected' USING ERRCODE='22023'; END IF;
    INSERT INTO telemetry.sql_agent_failure(instance_id,target_revision,run_id,job_id,history_instance_id,step_id,run_status,failure_kind,message_id,severity,retry_attempt,duration_seconds,detected_at,first_observed_at_utc,failure_fingerprint)
 SELECT p_instance_id,p_target_revision,p_run_id,x."jobId",x."historyInstanceId",x."stepId",x."runStatus",x."failureKind",x."messageId",x.severity,x."retryAttempt",x."durationSeconds",x."firstObservedAtUtc",x."firstObservedAtUtc",decode(x."failureFingerprint",'hex') FROM jsonb_to_recordset(COALESCE(p_payload->'items','[]'::jsonb)) AS x("jobId" uuid,"historyInstanceId" bigint,"stepId" integer,"runStatus" integer,"failureKind" smallint,"messageId" integer,severity integer,"retryAttempt" integer,"durationSeconds" integer,"firstObservedAtUtc" timestamptz,"failureFingerprint" text) ON CONFLICT(instance_id,target_revision,failure_fingerprint) DO NOTHING;
   GET DIAGNOSTICS event_inserted=ROW_COUNT;
   INSERT INTO telemetry.sql_agent_failure_occurrence(instance_id,target_revision,run_id,collected_at,failure_fingerprint)
    SELECT p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,decode(x."failureFingerprint",'hex') FROM jsonb_to_recordset(COALESCE(p_payload->'items','[]'::jsonb)) AS x("failureFingerprint" text) ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS occurrence_inserted=ROW_COUNT;
   -- The immutable event is dedup metadata; only the per-run occurrence is a
   -- logical output item.  Event insertion must never inflate accounting.
   inserted:=occurrence_inserted; duplicate_count:=greatest(p_output_item_count-occurrence_inserted,0); bytes:=inserted*160;
   INSERT INTO telemetry.sql_agent_failure_scan_snapshot(instance_id,target_revision,run_id,observed_at,state,source_rows,truncated,coverage_from,coverage_to) VALUES(p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,(p_payload->>'state')::smallint,p_source_row_count,COALESCE((p_payload->>'truncated')::boolean,false),(p_payload->>'coverageFromUtc')::timestamptz,(p_payload->>'coverageToUtc')::timestamptz);
 ELSIF NOT is_failure AND p_collector_id='tempdb.health' THEN
   SELECT count(*) INTO n FROM jsonb_array_elements(COALESCE(p_payload->'files','[]'::jsonb)); IF n>128 THEN RAISE EXCEPTION 'tempdb file bound rejected' USING ERRCODE='22023'; END IF;
   INSERT INTO telemetry.tempdb_snapshot(instance_id,target_revision,run_id,observed_at,state,total_bytes,used_bytes,log_total_bytes,log_used_bytes,truncated) VALUES(p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,(p_payload->>'state')::smallint,(p_payload->>'totalBytes')::bigint,(p_payload->>'usedBytes')::bigint,(p_payload->>'logTotalBytes')::bigint,(p_payload->>'logUsedBytes')::bigint,COALESCE((p_payload->>'truncated')::boolean,false));
   INSERT INTO telemetry.tempdb_file_snapshot(instance_id,target_revision,run_id,observed_at,file_id,size_bytes,used_bytes,free_bytes,state) SELECT p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,x."fileId",x."sizeBytes",x."usedBytes",x."freeBytes",x.state FROM jsonb_to_recordset(COALESCE(p_payload->'files','[]'::jsonb)) AS x("fileId" integer,"sizeBytes" bigint,"usedBytes" bigint,"freeBytes" bigint,state smallint); GET DIAGNOSTICS inserted=ROW_COUNT; bytes:=inserted*64; duplicate_count:=greatest(p_output_item_count-inserted,0);
 ELSIF NOT is_failure AND p_collector_id='availability-groups.health' THEN
   SELECT (SELECT count(*) FROM jsonb_array_elements(COALESCE(p_payload->'replicas','[]'::jsonb))) + (SELECT count(*) FROM jsonb_array_elements(COALESCE(p_payload->'databases','[]'::jsonb))) INTO n; IF n>2048 THEN RAISE EXCEPTION 'availability group row bound rejected' USING ERRCODE='22023'; END IF;
   INSERT INTO telemetry.availability_group_replica_snapshot(instance_id,target_revision,run_id,observed_at,group_fingerprint,replica_fingerprint,role,operational_state,connected_state,visibility_scope,state_available) SELECT p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,decode(x."groupFingerprint",'hex'),decode(x."replicaFingerprint",'hex'),x.role,x."operationalState",x."connectedState",x."visibilityScope",x."stateAvailable" FROM jsonb_to_recordset(COALESCE(p_payload->'replicas','[]'::jsonb)) AS x("groupFingerprint" text,"replicaFingerprint" text,role text,"operationalState" text,"connectedState" text,"visibilityScope" smallint,"stateAvailable" boolean); GET DIAGNOSTICS replica_inserted=ROW_COUNT;
   INSERT INTO telemetry.availability_group_database_snapshot(instance_id,target_revision,run_id,observed_at,group_fingerprint,database_fingerprint,synchronization_state,database_state,visibility_scope,state_available) SELECT p_instance_id,p_target_revision,p_run_id,(p_payload->>'observedAtUtc')::timestamptz,decode(x."groupFingerprint",'hex'),decode(x."databaseFingerprint",'hex'),x."synchronizationState",x."databaseState",x."visibilityScope",x."stateAvailable" FROM jsonb_to_recordset(COALESCE(p_payload->'databases','[]'::jsonb)) AS x("groupFingerprint" text,"databaseFingerprint" text,"synchronizationState" text,"databaseState" text,"visibilityScope" smallint,"stateAvailable" boolean); GET DIAGNOSTICS database_inserted=ROW_COUNT;
   inserted:=replica_inserted+database_inserted; bytes:=inserted*160; duplicate_count:=greatest(p_output_item_count-inserted,0);
 END IF;
 INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at) VALUES(p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),p_duration_ms,p_source_row_count,p_output_item_count+CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,inserted,duplicate_count,CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,p_response_bytes,p_output_bytes,bytes,COALESCE((p_payload->>'truncated')::boolean,false),p_loss_kind<>'none',p_loss_kind,p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,p_completion_digest,now_utc);
 UPDATE control.collector_schedule SET active_run_id=NULL,next_due_at=CASE WHEN p_next_circuit_state='open' THEN now_utc+contract_row.circuit_open_interval ELSE now_utc+collection_interval END,circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,circuit_open_until=CASE WHEN p_next_circuit_state='open' THEN now_utc+contract_row.circuit_open_interval ELSE NULL END,last_completed_at=now_utc,last_succeeded_at=CASE WHEN p_outcome='succeeded' THEN now_utc ELSE last_succeeded_at END,last_outcome=p_outcome,updated_at=now_utc WHERE instance_id=p_instance_id AND collector_id=p_collector_id;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 -- The caller receives the same logical accounting persisted in the outcome:
 -- rejected rows are explicit output-invalid rejections, never an implicit
 -- event-dedup count.
 RETURN QUERY SELECT 'committed',inserted, greatest(p_output_item_count-inserted,0),CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,bytes,now_utc;
END; $fn$;

CREATE OR REPLACE FUNCTION control.commit_deadlock_collection_run
(
    p_run_id uuid, p_instance_id uuid, p_target_revision bigint, p_collector_id text,
    p_collector_version integer, p_output_schema_version integer, p_schedule_revision bigint,
    p_scheduled_at timestamptz, p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint,
    p_request_digest bytea, p_outcome text, p_reason_code text, p_duration_ms bigint,
    p_attempt_count integer, p_source_row_count integer, p_output_item_count integer,
    p_response_bytes bigint, p_output_bytes bigint, p_loss_kind text, p_minimum_lost_items integer,
    p_loss_count_is_exact boolean, p_minimum_lost_bytes integer, p_next_circuit_state text,
    p_next_consecutive_failures integer, p_occurred_ats timestamptz[], p_event_ids uuid[],
    p_fingerprints bytea[], p_participant_counts integer[], p_relation_counts integer[],
    p_parse_truncated boolean[], p_participant_json jsonb[], p_relation_json jsonb[], p_sizes integer[]
)
RETURNS TABLE(result_status text, inserted_count integer, duplicate_count integer, rejected_count integer, persisted_bytes integer, committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_contract control.collector_contract%ROWTYPE;
    selected_schedule control.collector_schedule%ROWTYPE;
    existing_outcome telemetry.collection_run_outcome%ROWTYPE;
    item_count integer := coalesce(cardinality(p_event_ids), -1);
    inserted_count integer := 0;
    duplicate_count integer := 0;
    rejected_count integer := 0;
    persisted_bytes bigint := 0;
    captured_repository_time timestamptz;
    completion_digest bytea;
    aggregate_json_bytes bigint;
    aggregate_size_bytes bigint;
    begin_status text;
    begin_started_at timestamptz;
    event_id uuid;
    event_occurred timestamptz;
    item_index integer;
    occurrence_month date;
    next_open_until timestamptz;
BEGIN
    IF p_collector_id <> 'deadlocks.system-health'
       OR item_count < 0 OR item_count > 256
       OR cardinality(p_occurred_ats) IS DISTINCT FROM item_count
       OR cardinality(p_fingerprints) IS DISTINCT FROM item_count
       OR cardinality(p_participant_counts) IS DISTINCT FROM item_count
       OR cardinality(p_relation_counts) IS DISTINCT FROM item_count
       OR cardinality(p_parse_truncated) IS DISTINCT FROM item_count
       OR cardinality(p_participant_json) IS DISTINCT FROM item_count
       OR cardinality(p_relation_json) IS DISTINCT FROM item_count
       OR cardinality(p_sizes) IS DISTINCT FROM item_count
       OR (p_outcome <> 'output_invalid' AND p_output_item_count <> item_count)
       OR (p_outcome = 'output_invalid' AND item_count <> 0)
       OR p_output_item_count NOT BETWEEN 0 AND 256
       OR (p_outcome NOT IN ('succeeded','partial','output_invalid') AND (item_count <> 0 OR p_output_item_count <> 0))
       OR p_source_row_count NOT BETWEEN 0 AND 100000
       OR p_response_bytes NOT BETWEEN 0 AND 33554432
       OR p_output_bytes NOT BETWEEN 0 AND 33554432
       OR p_response_bytes < p_output_bytes
       OR p_attempt_count NOT BETWEEN 0 AND 2
       OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit','output_validation_failure')
       OR p_minimum_lost_items < 0 OR p_minimum_lost_bytes < 0 THEN
        RAISE EXCEPTION 'deadlock collector payload failed bounded array preflight' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(p_fingerprints,p_participant_counts,p_relation_counts,p_sizes) AS x(fingerprint,participants,relations,size)
               WHERE octet_length(x.fingerprint) <> 32 OR x.participants NOT BETWEEN 0 AND 128 OR x.relations NOT BETWEEN 0 AND 256 OR x.size < 192 OR x.size > 1048576)
       OR EXISTS (SELECT 1 FROM unnest(p_occurred_ats) AS x(value) WHERE x.value IS NULL OR NOT isfinite(x.value)) THEN
        RAISE EXCEPTION 'deadlock collector payload contains an invalid bounded value' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(p_event_ids,p_fingerprints) AS x(event_id,fingerprint)
               WHERE x.event_id <> encode(substring(sha256(uuid_send(p_instance_id) || x.fingerprint) FROM 1 FOR 16),'hex')::uuid) THEN
        RAISE EXCEPTION 'deadlock event identity is not target scoped' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM generate_subscripts(p_event_ids,1) AS s(i)
               WHERE p_participant_json[s.i] IS NULL
                  OR p_relation_json[s.i] IS NULL
                  OR jsonb_typeof(p_participant_json[s.i]) <> 'array'
                  OR jsonb_typeof(p_relation_json[s.i]) <> 'array'
                  OR jsonb_array_length(p_participant_json[s.i]) <> p_participant_counts[s.i]
                  OR jsonb_array_length(p_relation_json[s.i]) <> p_relation_counts[s.i]
                  OR octet_length(p_participant_json[s.i]::text) > 262144
                  OR octet_length(p_relation_json[s.i]::text) > 524288
                  OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_participant_json[s.i]) AS item
                             WHERE jsonb_typeof(item) <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(item)) <> 2
                                OR NOT item ? 'sessionId' OR NOT item ? 'victim'
                                OR jsonb_typeof(item->'sessionId') <> 'number' OR jsonb_typeof(item->'victim') <> 'boolean'
                                OR CASE WHEN item->>'sessionId' ~ '^[0-9]+$' AND length(item->>'sessionId') <= 5 THEN (item->>'sessionId')::integer NOT BETWEEN 1 AND 32767 ELSE true END)
                  OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_relation_json[s.i]) AS item
                             WHERE jsonb_typeof(item) <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(item)) <> 4
                                OR NOT item ? 'blockerSessionId' OR NOT item ? 'waiterSessionId' OR NOT item ? 'resourceCategory' OR NOT item ? 'lockMode'
                                OR jsonb_typeof(item->'blockerSessionId') <> 'number' OR jsonb_typeof(item->'waiterSessionId') <> 'number'
                                OR jsonb_typeof(item->'resourceCategory') <> 'string' OR jsonb_typeof(item->'lockMode') <> 'string'
                                OR CASE WHEN item->>'blockerSessionId' ~ '^[0-9]+$' AND length(item->>'blockerSessionId') <= 5 THEN (item->>'blockerSessionId')::integer NOT BETWEEN 1 AND 32767 ELSE true END
                                OR CASE WHEN item->>'waiterSessionId' ~ '^[0-9]+$' AND length(item->>'waiterSessionId') <= 5 THEN (item->>'waiterSessionId')::integer NOT BETWEEN 1 AND 32767 ELSE true END
                                OR item->>'resourceCategory' NOT IN ('key','page','object_lock','metadata','exchange','other')
                                OR item->>'lockMode' NOT IN ('NL','S','U','X','IS','IU','IX','SIU','SIX','UIX','SCH_S','SCH_M','BU','RANGES_S','RANGES_U','RANGEI_N','RANGEI_S','RANGEX_X','OTHER'))) THEN
        RAISE EXCEPTION 'deadlock collector payload contains malformed bounded JSON evidence' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1
               FROM generate_subscripts(p_event_ids,1) AS s(i)
               CROSS JOIN LATERAL
               (
                   SELECT COALESCE('[' || (SELECT string_agg(
                       '{"sessionId":' || (item.value->>'sessionId') ||
                       ',"victim":' || lower(item.value->>'victim') || '}', ',' ORDER BY (item.value->>'sessionId')::integer)
                       FROM jsonb_array_elements(p_participant_json[s.i]) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS participant_json,
                          COALESCE('[' || (SELECT string_agg(
                       '{"blockerSessionId":' || (item.value->>'blockerSessionId') ||
                       ',"waiterSessionId":' || (item.value->>'waiterSessionId') ||
                       ',"resourceCategory":"' || (item.value->>'resourceCategory') ||
                       '","lockMode":"' || (item.value->>'lockMode') || '"}', ',' ORDER BY (item.value->>'blockerSessionId')::integer, (item.value->>'waiterSessionId')::integer, CASE item.value->>'resourceCategory' WHEN 'key' THEN 1 WHEN 'page' THEN 2 WHEN 'object_lock' THEN 3 WHEN 'metadata' THEN 4 WHEN 'exchange' THEN 5 ELSE 6 END, convert_to(item.value->>'lockMode','UTF8'))
                       FROM jsonb_array_elements(p_relation_json[s.i]) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS relation_json
               ) AS canonical
               WHERE p_sizes[s.i] <> 192
                   + octet_length(convert_to(canonical.participant_json, 'UTF8'))
                   + octet_length(convert_to(canonical.relation_json, 'UTF8'))) THEN
        RAISE EXCEPTION 'deadlock collector payload contains forged per-item byte accounting' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM generate_subscripts(p_event_ids,1) AS s(i)
               WHERE EXISTS (SELECT 1 FROM (SELECT item->>'sessionId' AS session_id, count(*) AS c FROM jsonb_array_elements(p_participant_json[s.i]) AS item GROUP BY item->>'sessionId' HAVING count(*) > 1) AS duplicate_participants)
                  OR EXISTS (SELECT 1 FROM (SELECT item->>'blockerSessionId' AS blocker_id,item->>'waiterSessionId' AS waiter_id,item->>'resourceCategory' AS category,item->>'lockMode' AS mode,count(*) AS c FROM jsonb_array_elements(p_relation_json[s.i]) AS item GROUP BY item->>'blockerSessionId',item->>'waiterSessionId',item->>'resourceCategory',item->>'lockMode' HAVING count(*) > 1) AS duplicate_relations)) THEN
        RAISE EXCEPTION 'deadlock collector payload contains duplicate normalized evidence' USING ERRCODE = '22023';
    END IF;
    SELECT coalesce(sum(octet_length(convert_to(canonical.participant_json, 'UTF8')) + octet_length(convert_to(canonical.relation_json, 'UTF8'))), 0),
           coalesce(sum(size), 0)
    INTO aggregate_json_bytes, aggregate_size_bytes
    FROM unnest(p_participant_json, p_relation_json, p_sizes) AS bounded(participant_json, relation_json, size)
    CROSS JOIN LATERAL
    (
        SELECT COALESCE('[' || (SELECT string_agg(
                   '{"sessionId":' || (item.value->>'sessionId') ||
                   ',"victim":' || lower(item.value->>'victim') || '}', ',' ORDER BY (item.value->>'sessionId')::integer)
                   FROM jsonb_array_elements(bounded.participant_json) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS participant_json,
               COALESCE('[' || (SELECT string_agg(
                   '{"blockerSessionId":' || (item.value->>'blockerSessionId') ||
                   ',"waiterSessionId":' || (item.value->>'waiterSessionId') ||
                   ',"resourceCategory":"' || (item.value->>'resourceCategory') ||
                   '","lockMode":"' || (item.value->>'lockMode') || '"}', ',' ORDER BY (item.value->>'blockerSessionId')::integer, (item.value->>'waiterSessionId')::integer, CASE item.value->>'resourceCategory' WHEN 'key' THEN 1 WHEN 'page' THEN 2 WHEN 'object_lock' THEN 3 WHEN 'metadata' THEN 4 WHEN 'exchange' THEN 5 ELSE 6 END, convert_to(item.value->>'lockMode','UTF8'))
                   FROM jsonb_array_elements(bounded.relation_json) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS relation_json
    ) AS canonical;
    IF aggregate_json_bytes > 1048576
       OR (p_outcome <> 'output_invalid' AND aggregate_size_bytes <> p_output_bytes)
       OR (p_outcome = 'output_invalid' AND (aggregate_size_bytes <> 0 OR aggregate_json_bytes <> 0))
       OR aggregate_size_bytes > p_response_bytes
       OR aggregate_json_bytes > p_output_bytes THEN
        RAISE EXCEPTION 'deadlock collector payload exceeds its bounded aggregate byte accounting' USING ERRCODE = '22023';
    END IF;
    SELECT contract.* INTO selected_contract FROM control.collector_contract AS contract
    WHERE contract.collector_id = p_collector_id AND contract.collector_version = p_collector_version;
    IF NOT FOUND OR selected_contract.output_schema_version <> p_output_schema_version
       OR p_source_row_count > selected_contract.maximum_rows OR p_response_bytes > selected_contract.maximum_response_bytes
       OR p_output_bytes > selected_contract.maximum_response_bytes OR p_attempt_count > selected_contract.maximum_attempts THEN
        RAISE EXCEPTION 'deadlock collector payload does not match immutable contract' USING ERRCODE = '22023';
    END IF;
    IF p_outcome = 'succeeded' AND (p_loss_kind <> 'none' OR p_reason_code <> 'completed') THEN
        RAISE EXCEPTION 'deadlock success must be complete and loss-free' USING ERRCODE = '22023';
    END IF;
    IF p_outcome = 'partial' AND p_loss_kind = 'none' THEN
        RAISE EXCEPTION 'deadlock partial outcome requires explicit loss' USING ERRCODE = '22023';
    END IF;
    completion_digest := sha256(convert_to(jsonb_build_object(
        'contract','sqlobserver.deadlock-completion.v1','run',p_run_id,'target',p_instance_id,
        'revision',p_target_revision,'collector',p_collector_id,'collectorVersion',p_collector_version,
        'outputVersion',p_output_schema_version,'scheduleRevision',p_schedule_revision,
        'scheduledAt',p_scheduled_at,'requestDigest',encode(p_request_digest,'hex'),
        'outcome',p_outcome,'reason',p_reason_code,'sourceRows',p_source_row_count,
        'outputItems',p_output_item_count,'responseBytes',p_response_bytes,'outputBytes',p_output_bytes,
        'durationMs',p_duration_ms,'attempts',p_attempt_count,'lossExact',p_loss_count_is_exact,
        'nextCircuit',p_next_circuit_state,'nextFailures',p_next_consecutive_failures,
        'lossKind',p_loss_kind,'lostItems',p_minimum_lost_items,'lostBytes',p_minimum_lost_bytes,
        'fingerprints',p_fingerprints,'occurredAt',p_occurred_ats,'eventIds',p_event_ids,
        'participants',p_participant_counts,'relations',p_relation_counts,'parseTruncated',p_parse_truncated,
        'participantJson',p_participant_json,'relationJson',p_relation_json,'sizes',p_sizes)::text,'UTF8'));
    -- Serialize target reads with the atomic commit so a collected_at watermark
    -- cannot precede visibility of an in-flight transaction.
    PERFORM pg_advisory_xact_lock(hashtextextended(p_instance_id::text, 0));
    SELECT result.result_status,result.started_at,result.repository_time INTO begin_status,begin_started_at,captured_repository_time
    FROM control.begin_collection_run(p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,p_output_schema_version,p_schedule_revision,p_scheduled_at,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest) AS result;
    IF begin_status = 'replayed' THEN
        SELECT outcome.* INTO existing_outcome FROM telemetry.collection_run_outcome AS outcome WHERE outcome.run_id = p_run_id;
        IF existing_outcome.completion_digest <> completion_digest THEN RAISE EXCEPTION 'deadlock collector replay differs from committed payload' USING ERRCODE = '22023'; END IF;
        RETURN QUERY SELECT 'replayed',existing_outcome.inserted_item_count,existing_outcome.duplicate_item_count,existing_outcome.rejected_item_count,existing_outcome.persisted_bytes::integer,existing_outcome.completed_at; RETURN;
    ELSIF begin_status NOT IN ('started','running_replay') THEN
        RETURN QUERY SELECT begin_status,0,0,0,0,NULL::timestamptz; RETURN;
    END IF;
    captured_repository_time := clock_timestamp();
    IF EXISTS (SELECT 1 FROM unnest(p_occurred_ats) AS x(value) WHERE x.value < captured_repository_time - interval '33 days' OR x.value > captured_repository_time + interval '1 day') THEN
        RAISE EXCEPTION 'deadlock occurrence is outside the bounded repository window' USING ERRCODE = '22023';
    END IF;
    FOR occurrence_month IN SELECT DISTINCT date_trunc('month', value)::date FROM unnest(p_occurred_ats) AS x(value) LOOP
        PERFORM control.ensure_monthly_event_partition(occurrence_month);
    END LOOP;
    FOR event_occurred,event_id IN SELECT value.event_occurred,value.event_id FROM unnest(p_occurred_ats,p_event_ids) AS value(event_occurred,event_id) LOOP
        item_index := array_position(p_event_ids,event_id);
        IF NOT EXISTS (SELECT 1 FROM events.deadlock_summary AS prior WHERE prior.instance_id=p_instance_id AND prior.fingerprint=p_fingerprints[item_index]) THEN
            INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at)
            VALUES(event_occurred,event_id,p_instance_id,'deadlock.captured',0,jsonb_build_object('participantCount',p_participant_counts[item_index],'relationCount',p_relation_counts[item_index],'parseTruncated',p_parse_truncated[item_index]),captured_repository_time)
            ON CONFLICT ON CONSTRAINT pk_diagnostic_event DO NOTHING;
            INSERT INTO events.deadlock_summary(occurred_at,event_id,collection_run_id,instance_id,fingerprint,participant_count,relation_count,parse_truncated,collected_at)
            VALUES(event_occurred,event_id,p_run_id,p_instance_id,p_fingerprints[item_index],p_participant_counts[item_index],p_relation_counts[item_index],p_parse_truncated[item_index],captured_repository_time);
            inserted_count := inserted_count + 1;
            persisted_bytes := persisted_bytes + p_sizes[item_index];
            INSERT INTO events.deadlock_participant(occurred_at,event_id,session_id,is_victim)
            SELECT event_occurred,event_id,(x->>'sessionId')::integer,(x->>'victim')::boolean FROM jsonb_array_elements(p_participant_json[item_index]) AS x;
            INSERT INTO events.deadlock_relation(occurred_at,event_id,blocker_session_id,waiter_session_id,resource_category,lock_mode)
            SELECT event_occurred,event_id,(x->>'blockerSessionId')::integer,(x->>'waiterSessionId')::integer,x->>'resourceCategory',x->>'lockMode' FROM jsonb_array_elements(p_relation_json[item_index]) AS x;
        ELSE duplicate_count := duplicate_count + 1; END IF;
    END LOOP;
    rejected_count := greatest(p_output_item_count - inserted_count - duplicate_count,0);
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    SELECT schedule.* INTO selected_schedule FROM control.collector_schedule AS schedule WHERE schedule.instance_id=p_instance_id AND schedule.collector_id=p_collector_id FOR UPDATE;
    IF selected_schedule.active_run_id <> p_run_id OR selected_schedule.schedule_revision <> p_schedule_revision THEN RAISE EXCEPTION 'deadlock collector schedule changed before commit' USING ERRCODE = '55000'; END IF;
    IF
    (
        p_outcome IN ('succeeded', 'partial')
        AND (p_next_circuit_state <> 'closed' OR p_next_consecutive_failures <> 0)
    )
    OR
    (
        p_outcome IN ('transient_failure', 'timed_out')
        AND
        (
            p_next_consecutive_failures <> selected_schedule.consecutive_failure_count + 1
            OR p_next_circuit_state <> CASE
                WHEN selected_schedule.circuit_state = 'half_open'
                     OR selected_schedule.consecutive_failure_count + 1 >= selected_contract.circuit_failure_threshold
                    THEN 'open'
                ELSE 'closed'
            END
        )
    )
    OR
    (
        p_outcome NOT IN ('succeeded', 'partial', 'transient_failure', 'timed_out')
        AND
        (
            p_next_circuit_state <> 'closed'
            OR p_next_consecutive_failures <> 0
        )
    ) THEN
        RAISE EXCEPTION 'deadlock collector circuit transition differs from the authoritative schedule state' USING ERRCODE = '22023';
    END IF;
    next_open_until := CASE
        WHEN p_next_circuit_state = 'open'
            THEN captured_repository_time + selected_contract.circuit_open_interval
        ELSE NULL
    END;
    INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
    VALUES(p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),p_duration_ms,p_source_row_count,p_output_item_count,inserted_count,duplicate_count,rejected_count,p_response_bytes,p_output_bytes,persisted_bytes,p_loss_kind IN ('source_row_limit','response_byte_limit'),p_loss_kind <> 'none',p_loss_kind,p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,completion_digest,captured_repository_time);
    IF p_outcome <> 'succeeded' OR p_loss_kind <> 'none' THEN
        INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at)
        VALUES(gen_random_uuid(),p_run_id,p_instance_id,p_collector_id,p_reason_code,p_scheduled_at,captured_repository_time,greatest(p_minimum_lost_items,1),p_minimum_lost_bytes,CASE WHEN p_loss_kind <> 'none' THEN p_loss_count_is_exact ELSE false END,captured_repository_time);
    END IF;
    UPDATE control.collector_schedule SET active_run_id=NULL,next_due_at=CASE WHEN p_next_circuit_state='open' THEN next_open_until ELSE captured_repository_time+collection_interval END,circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,circuit_open_until=next_open_until,last_completed_at=captured_repository_time,last_succeeded_at=CASE WHEN p_outcome IN ('succeeded','partial') THEN captured_repository_time ELSE last_succeeded_at END,last_outcome=p_outcome,updated_at=captured_repository_time WHERE instance_id=p_instance_id AND collector_id=p_collector_id;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    RETURN QUERY SELECT 'committed',inserted_count,duplicate_count,rejected_count,persisted_bytes::integer,captured_repository_time;
END
$sqlobserver$;
