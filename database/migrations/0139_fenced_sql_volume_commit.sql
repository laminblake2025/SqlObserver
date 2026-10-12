-- Commit SQL-reported volume capacity in the same transaction as its run
-- outcome and schedule advancement. No collector is scheduled by this file.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.commit_sql_volume_collection_run(
 p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_id text,
 p_collector_version integer,p_output_schema_version integer,p_schedule_revision bigint,
 p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
 p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,
 p_attempt_count integer,p_source_row_count integer,p_output_item_count integer,
 p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
 p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,
 p_next_consecutive_failures integer,p_payload jsonb,p_completion_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,
 rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
DECLARE
 run_row telemetry.collection_run%ROWTYPE;
 schedule_row control.collector_schedule%ROWTYPE;
 contract_row control.collector_contract%ROWTYPE;
 prior telemetry.collection_run_outcome%ROWTYPE;
 now_utc timestamptz := clock_timestamp();
 is_failure boolean;
 inserted integer := 0;
 written_bytes integer := 0;
 item_count integer;
BEGIN
 is_failure := p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'storage.volume'
    OR p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1
    OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision <= 0
    OR p_schedule_revision IS NULL OR p_schedule_revision <= 0 OR p_scheduled_at IS NULL
    OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token <= 0
    OR octet_length(p_request_digest) IS DISTINCT FROM 32
    OR octet_length(p_completion_digest) IS DISTINCT FROM 32
    OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object'
    OR jsonb_typeof(p_payload->'items') IS DISTINCT FROM 'array'
    OR octet_length(p_payload::text) > 1048576
    OR jsonb_array_length(p_payload->'items') > 1000
    OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1
    OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
    OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
    OR p_outcome IS NULL OR p_outcome NOT IN
       ('succeeded','partial','timed_out','transient_failure','permanent_failure',
        'permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
    OR p_reason_code IS NULL OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 1 AND 2
    OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
    OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 1001
    OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 1000
    OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 4194304
    OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 4194304
    OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 1001
    OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 4194304
    OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL
    OR p_next_circuit_state NOT IN ('closed','open','half_open')
    OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
    OR (NOT is_failure AND jsonb_array_length(p_payload->'items') <> p_output_item_count)
    OR (is_failure AND (p_payload->>'outcome' IS DISTINCT FROM p_outcome
      OR p_payload->>'reason' IS DISTINCT FROM p_reason_code
      OR jsonb_array_length(p_payload->'items') <> 0 OR p_output_item_count <> 0 OR p_output_bytes <> 0))
 THEN RAISE EXCEPTION 'SQL volume lifecycle bounds rejected' USING ERRCODE='22023'; END IF;

 IF NOT is_failure THEN
  item_count := jsonb_array_length(p_payload->'items');
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_payload->'items') AS item(value)
    WHERE jsonb_typeof(item.value) IS DISTINCT FROM 'object'
       OR item.value->>'volumeKey' IS NULL
       OR item.value->>'volumeKey' !~ '^[0-9a-f]{64}$'
       OR item.value->>'identityKind' IS NULL
       OR item.value->>'identityKind' NOT IN ('volume_id','mount_point','file_scoped_unknown')
       OR item.value->>'mappedFileCount' IS NULL
       OR (item.value->>'mappedFileCount')::integer NOT BETWEEN 1 AND 1000
       OR item.value->>'observedAtUtc' IS NULL
       OR NOT isfinite((item.value->>'observedAtUtc')::timestamptz)
       OR (item.value->>'observedAtUtc')::timestamptz > now_utc + interval '5 minutes'
       OR ((item.value->>'totalBytes') IS NULL) <> ((item.value->>'availableBytes') IS NULL)
       OR (item.value->>'totalBytes')::bigint <= 0
       OR (item.value->>'availableBytes')::bigint < 0
       OR (item.value->>'availableBytes')::bigint > (item.value->>'totalBytes')::bigint
       OR EXISTS (SELECT 1 FROM jsonb_object_keys(item.value) AS field(name)
                  WHERE field.name NOT IN ('volumeKey','identityKind','mappedFileCount',
                       'totalBytes','availableBytes','observedAtUtc'))
  ) OR EXISTS (SELECT 1 FROM jsonb_object_keys(p_payload) AS field(name)
               WHERE field.name NOT IN ('schemaVersion','targetId','targetRevision','items'))
    OR (SELECT count(DISTINCT item.value->>'volumeKey')
        FROM jsonb_array_elements(p_payload->'items') AS item(value)) <> item_count
  THEN RAISE EXCEPTION 'SQL volume payload identity or capacity rejected' USING ERRCODE='22023'; END IF;
 END IF;

 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.collection_run WHERE run_id=p_run_id FOR UPDATE;
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision
    OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version
    OR run_row.output_schema_version<>p_output_schema_version
    OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key
    OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token
    OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at
 THEN RAISE EXCEPTION 'SQL volume run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='storage.volume' AND collector_version=1;
 IF NOT FOUND OR contract_row.output_schema_version<>1
 THEN RAISE EXCEPTION 'SQL volume immutable collector contract missing' USING ERRCODE='55000'; END IF;
 PERFORM set_config('sqlobserver.target_scope',p_instance_id::text,true);

 -- A matching retry is valid after the schedule has advanced. A changed
 -- completion digest or accounting is an attempt to rewrite an immutable run.
 SELECT * INTO prior FROM telemetry.collection_run_outcome WHERE run_id=p_run_id;
 IF FOUND THEN
  IF prior.completion_digest<>p_completion_digest OR prior.outcome<>p_outcome
     OR prior.reason_code<>p_reason_code OR prior.attempt_count<>p_attempt_count
     OR prior.duration_ms<>p_duration_ms
     OR prior.source_row_count<>p_source_row_count
     OR prior.output_item_count <>
        (p_output_item_count + CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END)
     OR prior.response_bytes<>p_response_bytes OR prior.output_bytes<>p_output_bytes
     OR prior.loss_kind<>p_loss_kind OR prior.lost_row_count<>p_minimum_lost_items
     OR prior.lost_byte_count<>p_minimum_lost_bytes
     OR prior.loss_count_exact<>p_loss_count_is_exact
  THEN RAISE EXCEPTION 'SQL volume divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
    OR schedule_row.schedule_revision<>p_schedule_revision
    OR schedule_row.target_revision<>p_target_revision
    OR schedule_row.next_due_at<>p_scheduled_at
 THEN RAISE EXCEPTION 'SQL volume schedule fence mismatch' USING ERRCODE='55000'; END IF;
 IF (p_outcome IN ('succeeded','partial')
       AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
    OR (p_outcome IN ('timed_out','transient_failure')
       AND (p_next_consecutive_failures<>schedule_row.consecutive_failure_count+1
         OR p_next_circuit_state<>CASE WHEN schedule_row.circuit_state='half_open'
                OR schedule_row.consecutive_failure_count+1>=contract_row.circuit_failure_threshold
              THEN 'open' ELSE 'closed' END))
    OR (p_outcome='circuit_open' AND (p_next_circuit_state<>'open'
       OR p_next_consecutive_failures<>schedule_row.consecutive_failure_count))
    OR (p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','circuit_open')
       AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
 THEN RAISE EXCEPTION 'SQL volume circuit transition mismatch' USING ERRCODE='22023'; END IF;

 IF NOT is_failure THEN
  INSERT INTO telemetry.sql_volume_snapshot
   (observed_at,run_id,instance_id,target_revision,volume_key,identity_kind,
    mapped_file_count,total_bytes,available_bytes,collected_at)
  SELECT (item.value->>'observedAtUtc')::timestamptz,p_run_id,p_instance_id,p_target_revision,
         decode(item.value->>'volumeKey','hex'),item.value->>'identityKind',
         (item.value->>'mappedFileCount')::integer,
         (item.value->>'totalBytes')::bigint,(item.value->>'availableBytes')::bigint,now_utc
  FROM jsonb_array_elements(p_payload->'items') AS item(value);
  GET DIAGNOSTICS inserted = ROW_COUNT;
  IF inserted<>p_output_item_count
  THEN RAISE EXCEPTION 'SQL volume output accounting mismatch' USING ERRCODE='22023'; END IF;
  written_bytes := p_output_bytes;
 END IF;
 IF p_outcome<>'succeeded' OR p_loss_kind<>'none' THEN
  INSERT INTO telemetry.visibility_gap
   (gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,
    lost_row_count,lost_byte_count,count_is_exact,recorded_at)
  VALUES (gen_random_uuid(),p_run_id,p_instance_id,p_collector_id,p_reason_code,
          p_scheduled_at,now_utc,greatest(p_minimum_lost_items,1),
          p_minimum_lost_bytes,p_loss_count_is_exact,now_utc)
  ON CONFLICT(run_id) DO NOTHING;
 END IF;
 INSERT INTO telemetry.collection_run_outcome
  (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,
   output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,
   response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
   loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
 VALUES (p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),
         p_duration_ms,p_source_row_count,
         p_output_item_count+CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
         inserted,0,CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
         p_response_bytes,p_output_bytes,written_bytes,
         coalesce((p_payload->>'truncated')::boolean,false),p_loss_kind<>'none',p_loss_kind,
         p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,p_completion_digest,now_utc);
 UPDATE control.collector_schedule
 SET active_run_id=NULL,
     next_due_at=CASE WHEN p_next_circuit_state='open'
       THEN now_utc+contract_row.circuit_open_interval ELSE now_utc+collection_interval END,
     circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,
     circuit_open_until=CASE WHEN p_next_circuit_state='open'
       THEN now_utc+contract_row.circuit_open_interval ELSE NULL END,
     last_completed_at=now_utc,
     last_succeeded_at=CASE WHEN p_outcome='succeeded' THEN now_utc ELSE last_succeeded_at END,
     last_outcome=p_outcome,updated_at=now_utc
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 'committed',inserted,0,
   CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
   written_bytes,now_utc;
END;
$fn$;
REVOKE ALL ON FUNCTION control.commit_sql_volume_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_sql_volume_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) TO sqlobserver_collector;
