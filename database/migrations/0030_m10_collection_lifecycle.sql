-- Complete M10 success and failure runs atomically with their schedule.
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION control.bind_replication_run_gap() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,telemetry AS $$
DECLARE revision bigint;
BEGIN
 IF NEW.collector_id='replication.health' AND NEW.topology_fingerprint IS NULL AND NEW.gap_evidence IS NULL THEN
   SELECT r.target_revision INTO STRICT revision FROM telemetry.collection_run r WHERE r.run_id=NEW.run_id AND r.instance_id=NEW.instance_id AND r.collector_id=NEW.collector_id;
   NEW.topology_fingerprint:=sha256(convert_to('m10-replication-gap|'||NEW.instance_id::text||'|'||revision::text||'|'||NEW.run_id::text,'UTF8'));
   NEW.gap_evidence:=jsonb_build_object('kind','collection_gap','reason',NEW.reason_code,'targetRevision',revision,'topologyAvailable',false);
 END IF;
 RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION control.bind_replication_run_gap() FROM PUBLIC;
CREATE TRIGGER bind_replication_run_gap BEFORE INSERT ON telemetry.visibility_gap FOR EACH ROW EXECUTE FUNCTION control.bind_replication_run_gap();
CREATE OR REPLACE FUNCTION control.commit_m10_collection_run(
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
DECLARE run_row telemetry.collection_run%ROWTYPE; schedule_row control.collector_schedule%ROWTYPE;
 contract_row control.collector_contract%ROWTYPE; prior telemetry.collection_run_outcome%ROWTYPE;
 now_utc timestamptz:=clock_timestamp(); inserted integer:=0; duplicate_count integer:=0; bytes integer:=0;
 is_failure boolean; payload_result record;
BEGIN
 is_failure := p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS NULL OR p_collector_id NOT IN ('host.metrics','replication.health') OR p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1
 OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<=0 OR p_schedule_revision IS NULL OR p_schedule_revision<=0 OR p_scheduled_at IS NULL OR p_fencing_token IS NULL OR p_fencing_token<=0
 OR octet_length(p_request_digest) IS DISTINCT FROM 32 OR octet_length(p_completion_digest) IS DISTINCT FROM 32
 OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR jsonb_typeof(p_payload->'items') IS DISTINCT FROM 'array'
 OR octet_length(p_payload::text)>1048576 OR jsonb_array_length(p_payload->'items')>2048
 OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1 OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
 OR p_outcome IS NULL OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
 OR p_reason_code IS NULL OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 1 AND 2 OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
 OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 4096 OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 2048
 OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 2097152 OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 2097152
 OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 4096 OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 2097152
 OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL OR p_next_circuit_state NOT IN ('closed','open','half_open') OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
 OR (NOT is_failure AND jsonb_array_length(p_payload->'items')<>p_output_item_count)
 OR (is_failure AND (p_payload->>'outcome' IS DISTINCT FROM p_outcome OR p_payload->>'reason' IS DISTINCT FROM p_reason_code OR jsonb_array_length(p_payload->'items')<>0 OR p_output_item_count<>0 OR p_output_bytes<>0))
 THEN RAISE EXCEPTION 'M10 lifecycle bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.collection_run WHERE run_id=p_run_id FOR UPDATE;
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version OR run_row.output_schema_version<>p_output_schema_version OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at THEN RAISE EXCEPTION 'M10 run identity, target revision, schedule, lease, or request digest mismatch' USING ERRCODE='55000'; END IF;
  SELECT * INTO contract_row FROM control.collector_contract WHERE collector_id=p_collector_id AND collector_version=1;
  IF NOT FOUND OR contract_row.output_schema_version<>1 THEN RAISE EXCEPTION 'M10 immutable collector contract missing' USING ERRCODE='55000'; END IF;
  PERFORM set_config('sqlobserver.target_scope',p_instance_id::text,true);
  -- Replay is an immutable, digest-validated lookup.  It deliberately occurs
  -- before the mutable schedule fence so an identical retry remains replayable
  -- after the first commit cleared active_run_id and advanced next_due_at.
 SELECT * INTO prior FROM telemetry.collection_run_outcome o WHERE o.run_id=p_run_id;
 IF FOUND THEN
   IF prior.completion_digest<>p_completion_digest OR prior.outcome<>p_outcome OR prior.reason_code<>p_reason_code OR prior.source_row_count<>p_source_row_count OR prior.response_bytes<>p_response_bytes OR prior.output_bytes<>p_output_bytes OR prior.loss_kind<>p_loss_kind OR prior.lost_row_count<>p_minimum_lost_items OR prior.lost_byte_count<>p_minimum_lost_bytes OR prior.loss_count_exact<>p_loss_count_is_exact
   THEN RAISE EXCEPTION 'M10 divergent completion replay' USING ERRCODE='40001'; END IF;
   RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at; RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id OR schedule_row.schedule_revision<>p_schedule_revision OR schedule_row.target_revision<>p_target_revision OR schedule_row.next_due_at<>p_scheduled_at THEN RAISE EXCEPTION 'M10 schedule fence mismatch' USING ERRCODE='55000'; END IF;
 IF (p_outcome IN ('succeeded','partial') AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
 OR (p_outcome IN ('timed_out','transient_failure') AND (p_next_consecutive_failures<>schedule_row.consecutive_failure_count+1 OR p_next_circuit_state<>CASE WHEN schedule_row.circuit_state='half_open' OR schedule_row.consecutive_failure_count+1>=contract_row.circuit_failure_threshold THEN 'open' ELSE 'closed' END))
 OR (p_outcome='circuit_open' AND (p_next_circuit_state<>'open' OR p_next_consecutive_failures<>schedule_row.consecutive_failure_count))
 OR (p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','circuit_open') AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
 THEN RAISE EXCEPTION 'M10 circuit transition mismatch' USING ERRCODE='22023'; END IF;
 IF NOT is_failure THEN
   IF p_collector_id='host.metrics' THEN
     SELECT * INTO payload_result FROM telemetry.commit_m10_host_metrics(p_run_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_payload,p_completion_digest);
   ELSE
     SELECT * INTO payload_result FROM telemetry.commit_m10_replication(p_run_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_payload,p_completion_digest);
   END IF;
   inserted:=payload_result.inserted_count; duplicate_count:=payload_result.duplicate_count;
   IF inserted+duplicate_count<>p_output_item_count THEN RAISE EXCEPTION 'M10 output accounting mismatch' USING ERRCODE='22023'; END IF;
   bytes:=p_output_bytes;
 END IF;
 IF p_outcome<>'succeeded' OR p_loss_kind<>'none' THEN
   INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at)
   VALUES(gen_random_uuid(),p_run_id,p_instance_id,p_collector_id,p_reason_code,p_scheduled_at,now_utc,greatest(p_minimum_lost_items,1),p_minimum_lost_bytes,p_loss_count_is_exact,now_utc) ON CONFLICT(run_id) DO NOTHING;
 END IF;
 INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at) VALUES(p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),p_duration_ms,p_source_row_count,p_output_item_count+CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,inserted,duplicate_count,CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,p_response_bytes,p_output_bytes,bytes,COALESCE((p_payload->>'truncated')::boolean,false),p_loss_kind<>'none',p_loss_kind,p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,p_completion_digest,now_utc);
 UPDATE control.collector_schedule SET active_run_id=NULL,next_due_at=CASE WHEN p_next_circuit_state='open' THEN now_utc+contract_row.circuit_open_interval ELSE now_utc+collection_interval END,circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,circuit_open_until=CASE WHEN p_next_circuit_state='open' THEN now_utc+contract_row.circuit_open_interval ELSE NULL END,last_completed_at=now_utc,last_succeeded_at=CASE WHEN p_outcome='succeeded' THEN now_utc ELSE last_succeeded_at END,last_outcome=p_outcome,updated_at=now_utc WHERE instance_id=p_instance_id AND collector_id=p_collector_id;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 -- The caller receives the same logical accounting persisted in the outcome:
 -- rejected rows are explicit output-invalid rejections, never an implicit
 -- event-dedup count.
 RETURN QUERY SELECT 'committed',inserted, greatest(p_output_item_count-inserted,0),CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,bytes,now_utc;
END; $fn$;
REVOKE ALL ON FUNCTION control.commit_m10_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_m10_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea) TO sqlobserver_collector;
CREATE OR REPLACE FUNCTION telemetry.commit_m10_replication
 (p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_payload jsonb,p_completion_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,telemetry,control SET TimeZone='UTC'
AS $m10_replication_commit$
DECLARE existing telemetry.m10_commit_replay%ROWTYPE; computed_payload_digest bytea; n integer; now_utc timestamptz:=clock_timestamp();
BEGIN
 IF p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_completion_digest)<>32 OR jsonb_typeof(p_payload)<>'object' OR jsonb_typeof(p_payload->'items')<>'array' OR jsonb_array_length(p_payload->'items')>2048 OR octet_length(p_payload::text)>2097152 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1 OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 replication commit bounds or target scope rejected' USING ERRCODE='22023'; END IF;
 IF EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'items') x WHERE
     (x->>'pendingCommands') IS NOT NULL AND ((x->>'pendingCommands')::numeric < 0 OR (x->>'pendingCommands')::numeric > 2000000000)
     OR (x->>'latencySeconds') IS NOT NULL AND ((x->>'latencySeconds')::double precision < 0 OR (x->>'latencySeconds')::double precision > 86400)
     OR (x->>'latencyMillis') IS NOT NULL AND ((x->>'latencyMillis')::double precision < 0 OR (x->>'latencyMillis')::double precision > 86400000)
   ) THEN RAISE EXCEPTION 'M10 replication metric mapping bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 computed_payload_digest:=sha256(convert_to(p_payload::text,'UTF8'));
 INSERT INTO telemetry.m10_commit_replay(run_id,instance_id,target_revision,collector_id,collector_version,request_digest,payload_digest) VALUES(p_run_id,p_instance_id,p_target_revision,'replication.health',1,p_request_digest,computed_payload_digest) ON CONFLICT(run_id,instance_id,target_revision) DO NOTHING;
 IF NOT FOUND THEN SELECT * INTO existing FROM telemetry.m10_commit_replay WHERE run_id=p_run_id AND instance_id=p_instance_id AND target_revision=p_target_revision; IF existing.request_digest<>p_request_digest OR existing.payload_digest<>computed_payload_digest THEN RAISE EXCEPTION 'M10 divergent replay digest' USING ERRCODE='40001'; END IF; RETURN QUERY SELECT 'replayed',0,0,existing.committed_at; RETURN; END IF;
 INSERT INTO telemetry.replication_snapshot_v2(observed_at,run_id,instance_id,target_revision,topology_fingerprint,database_fingerprint,role,synchronization_state,send_queue_bytes,redo_queue_bytes,pending_commands,latency_seconds,visibility_scope,state_available,collected_at)
 SELECT coalesce((x->>'observedAtUtc')::timestamptz,now_utc),p_run_id,p_instance_id,p_target_revision,decode(x->>'topologyFingerprint','hex'),decode(nullif(x->>'databaseFingerprint',''),'hex'),x->>'role',x->>'synchronizationState',(x->>'sendQueueBytes')::bigint,(x->>'redoQueueBytes')::bigint,
        coalesce((x->>'pendingCommands')::bigint,(x->>'sendQueueBytes')::bigint),
        coalesce((x->>'latencySeconds')::double precision,(x->>'latencyMillis')::double precision/1000.0),
        coalesce((x->>'visibilityScope')::smallint,3),coalesce((x->>'stateAvailable')::boolean,false),now_utc FROM jsonb_array_elements(p_payload->'items') x
 WHERE x->>'topologyFingerprint' ~ '^[0-9a-fA-F]{64}$';
 GET DIAGNOSTICS n=ROW_COUNT; RETURN QUERY SELECT 'committed',n,0,now_utc;
 IF EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'items') x WHERE coalesce((x->>'visibilityScope')::smallint,3)=3 OR (x->'visibilityGap' IS NOT NULL AND x->'visibilityGap'<>'null'::jsonb)) THEN
   INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at,topology_fingerprint,gap_evidence)
   VALUES (left(encode(sha256(convert_to('m10-replication-gap|'||p_run_id::text,'UTF8')),'hex'),32)::uuid,p_run_id,p_instance_id,'replication.health','capability_missing',now_utc,now_utc,greatest(n,1),0,false,now_utc,
     coalesce((SELECT decode(x->>'topologyFingerprint','hex') FROM jsonb_array_elements(p_payload->'items') x WHERE x->>'topologyFingerprint' ~ '^[0-9a-fA-F]{64}' LIMIT 1),sha256(convert_to('m10-replication-gap|'||p_instance_id::text||'|'||p_target_revision::text||'|'||p_run_id::text,'UTF8'))),
     jsonb_build_object('kind','visibility_gap','reason','distribution_database_unbound','targetRevision',p_target_revision))
   ON CONFLICT(run_id) DO NOTHING;
 END IF;
END $m10_replication_commit$;
