-- Commit complete exact-value scans under the canonical run and lease fence.
-- Partial or failed scans record a gap and never publish drift.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.commit_server_configuration_collection_run(
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
 previous_run_id uuid;
 previous_outcome text;
 event_count integer := 0;
BEGIN
 is_failure := p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'engine.configurations'
    OR p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1
    OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision <= 0
    OR p_schedule_revision IS NULL OR p_schedule_revision <= 0 OR p_scheduled_at IS NULL
    OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token <= 0
    OR octet_length(p_request_digest) IS DISTINCT FROM 32
    OR octet_length(p_completion_digest) IS DISTINCT FROM 32
    OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object'
    OR jsonb_typeof(p_payload->'items') IS DISTINCT FROM 'array'
    OR octet_length(p_payload::text) > 131072
    OR jsonb_array_length(p_payload->'items') > 512
    OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1
    OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
    OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
    OR p_outcome IS NULL OR p_outcome NOT IN
       ('succeeded','partial','timed_out','transient_failure','permanent_failure',
        'permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
    OR p_reason_code IS NULL OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 1 AND 2
    OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
    OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 513
    OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 512
    OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 65536
    OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 65536
    OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 513
    OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 65536
    OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL
    OR p_next_circuit_state NOT IN ('closed','open','half_open')
    OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
    OR p_loss_kind IS NULL OR p_loss_kind NOT IN
       ('none','source_row_limit','response_byte_limit','output_validation_failure',
        'ingestion_rejection','blocking_graph_limit','duplicate_overlap','visibility_incomplete')
    OR (p_outcome='succeeded' AND (p_loss_kind<>'none' OR p_minimum_lost_items<>0
        OR p_minimum_lost_bytes<>0 OR p_output_item_count<1
        OR p_source_row_count<>p_output_item_count))
    OR (p_outcome='partial' AND (p_loss_kind NOT IN ('source_row_limit','response_byte_limit')
        OR p_minimum_lost_items<1 OR p_output_item_count<>0 OR p_output_bytes<>0))
    OR (NOT is_failure AND p_source_row_count<p_output_item_count)
    OR (NOT is_failure AND jsonb_array_length(p_payload->'items') <> p_output_item_count)
    OR (p_outcome='succeeded' AND
        (jsonb_typeof(p_payload->'serverMajorVersion') IS DISTINCT FROM 'number'
         OR (p_payload->>'serverMajorVersion')::integer NOT BETWEEN 15 AND 17))
    OR (is_failure AND (p_payload->>'outcome' IS DISTINCT FROM p_outcome
      OR p_payload->>'reason' IS DISTINCT FROM p_reason_code
      OR jsonb_array_length(p_payload->'items') <> 0 OR p_output_item_count <> 0 OR p_output_bytes <> 0))
 THEN RAISE EXCEPTION 'Server configuration lifecycle bounds rejected' USING ERRCODE='22023'; END IF;

 IF NOT is_failure THEN
  item_count := jsonb_array_length(p_payload->'items');
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_payload->'items') AS item(value)
    WHERE jsonb_typeof(item.value) IS DISTINCT FROM 'object'
       OR jsonb_typeof(item.value->'configurationId') IS DISTINCT FROM 'number'
       OR (item.value->>'configurationId')::numeric NOT BETWEEN 1 AND 1000000
       OR (item.value->>'configurationId')::numeric <>
          trunc((item.value->>'configurationId')::numeric)
       OR jsonb_typeof(item.value->'name') IS DISTINCT FROM 'string'
       OR octet_length(item.value->>'name') NOT BETWEEN 1 AND 256
       OR btrim(item.value->>'name') = ''
       OR item.value->>'name' ~ '[[:cntrl:]]'
       OR jsonb_typeof(item.value->'sourceType') IS DISTINCT FROM 'string'
       OR item.value->>'sourceType' NOT IN ('bit','tinyint','smallint','int','bigint')
       OR jsonb_typeof(item.value->'configuredValue') IS DISTINCT FROM 'string'
       OR jsonb_typeof(item.value->'inUseValue') IS DISTINCT FROM 'string'
       OR octet_length(item.value->>'configuredValue') NOT BETWEEN 1 AND 128
       OR octet_length(item.value->>'inUseValue') NOT BETWEEN 1 AND 128
       OR item.value->>'configuredValue' !~ '^(0|-[1-9][0-9]{0,18}|[1-9][0-9]{0,18})$'
       OR item.value->>'inUseValue' !~ '^(0|-[1-9][0-9]{0,18}|[1-9][0-9]{0,18})$'
       OR CASE item.value->>'sourceType'
            WHEN 'bit' THEN (item.value->>'configuredValue')::numeric NOT BETWEEN 0 AND 1
               OR (item.value->>'inUseValue')::numeric NOT BETWEEN 0 AND 1
            WHEN 'tinyint' THEN (item.value->>'configuredValue')::numeric NOT BETWEEN 0 AND 255
               OR (item.value->>'inUseValue')::numeric NOT BETWEEN 0 AND 255
            WHEN 'smallint' THEN (item.value->>'configuredValue')::numeric NOT BETWEEN -32768 AND 32767
               OR (item.value->>'inUseValue')::numeric NOT BETWEEN -32768 AND 32767
            WHEN 'int' THEN (item.value->>'configuredValue')::numeric NOT BETWEEN -2147483648 AND 2147483647
               OR (item.value->>'inUseValue')::numeric NOT BETWEEN -2147483648 AND 2147483647
            WHEN 'bigint' THEN (item.value->>'configuredValue')::numeric NOT BETWEEN -9223372036854775808 AND 9223372036854775807
               OR (item.value->>'inUseValue')::numeric NOT BETWEEN -9223372036854775808 AND 9223372036854775807
            ELSE true END
       OR jsonb_typeof(item.value->'isDynamic') IS DISTINCT FROM 'boolean'
       OR jsonb_typeof(item.value->'isAdvanced') IS DISTINCT FROM 'boolean'
       OR jsonb_typeof(item.value->'observedAtUtc') IS DISTINCT FROM 'string'
       OR item.value->>'observedAtUtc' IS NULL
       OR item.value->>'observedAtUtc' !~ '(Z|\+00:00)$'
       OR NOT isfinite((item.value->>'observedAtUtc')::timestamptz)
       OR (item.value->>'observedAtUtc')::timestamptz > now_utc + interval '5 minutes'
       OR EXISTS (SELECT 1 FROM jsonb_object_keys(item.value) AS field(name)
                  WHERE field.name NOT IN ('configurationId','name','sourceType',
                     'configuredValue','inUseValue','isDynamic','isAdvanced',
                     'observedAtUtc'))
  ) OR EXISTS (SELECT 1 FROM jsonb_object_keys(p_payload) AS field(name)
               WHERE field.name NOT IN ('schemaVersion','targetId','targetRevision',
                                        'serverMajorVersion','items'))
    OR (SELECT count(DISTINCT (item.value->>'configurationId')::integer)
        FROM jsonb_array_elements(p_payload->'items') AS item(value)) <> item_count
    OR (SELECT count(DISTINCT item.value->>'observedAtUtc')
        FROM jsonb_array_elements(p_payload->'items') AS item(value)) > 1
  THEN RAISE EXCEPTION 'Server configuration identity or value rejected' USING ERRCODE='22023'; END IF;
  IF p_outcome='succeeded' AND item_count=0
  THEN RAISE EXCEPTION 'Server configuration scan is empty' USING ERRCODE='22023'; END IF;
 END IF;

 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision
    OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version
    OR run_row.output_schema_version<>p_output_schema_version
    OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key
    OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token
    OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at
 THEN RAISE EXCEPTION 'Server configuration run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='engine.configurations' AND collector_version=1;
 IF NOT FOUND OR contract_row.output_schema_version<>1
 THEN RAISE EXCEPTION 'Server configuration immutable collector contract missing' USING ERRCODE='55000'; END IF;
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
  THEN RAISE EXCEPTION 'Server configuration divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
    OR schedule_row.schedule_revision<>p_schedule_revision
    OR schedule_row.target_revision<>p_target_revision
    OR schedule_row.next_due_at<>p_scheduled_at
 THEN RAISE EXCEPTION 'Server configuration schedule fence mismatch' USING ERRCODE='55000'; END IF;
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
 THEN RAISE EXCEPTION 'Server configuration circuit transition mismatch' USING ERRCODE='22023'; END IF;

 IF p_outcome='succeeded' THEN
  SELECT older.run_id,outcome.outcome INTO previous_run_id,previous_outcome
  FROM telemetry.collection_run_v2 AS older
  JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=older.run_id
  WHERE older.instance_id=p_instance_id
    AND older.collector_id='engine.configurations'
    AND older.started_at<run_row.started_at
  ORDER BY older.started_at DESC,older.run_id DESC LIMIT 1;

  INSERT INTO telemetry.server_configuration_snapshot
   (instance_id,run_id,configuration_id,target_revision,server_major_version,
    configuration_name,source_type,configured_value,in_use_value,
    is_dynamic,is_advanced,observed_at,collected_at)
  SELECT p_instance_id,p_run_id,(item.value->>'configurationId')::integer,
         p_target_revision,(p_payload->>'serverMajorVersion')::smallint,
         item.value->>'name',item.value->>'sourceType',
         item.value->>'configuredValue',item.value->>'inUseValue',
         (item.value->>'isDynamic')::boolean,
         (item.value->>'isAdvanced')::boolean,
         (item.value->>'observedAtUtc')::timestamptz,now_utc
  FROM jsonb_array_elements(p_payload->'items') AS item(value);
  GET DIAGNOSTICS inserted = ROW_COUNT;
  IF inserted<>p_output_item_count
  THEN RAISE EXCEPTION 'Server configuration output accounting mismatch'
       USING ERRCODE='22023'; END IF;
  written_bytes := p_output_bytes;

  -- The immediately preceding terminal scan alone may establish a comparison.
  -- A failure, partial scan, target revision, version or renamed ID starts a
  -- new baseline instead of attributing a change across the gap.
  IF previous_run_id IS NOT NULL AND previous_outcome='succeeded' AND EXISTS (
      SELECT 1 FROM telemetry.server_configuration_snapshot AS old
      WHERE old.run_id=previous_run_id AND old.instance_id=p_instance_id
        AND old.target_revision=p_target_revision
        AND old.server_major_version=(p_payload->>'serverMajorVersion')::smallint
  ) THEN
   INSERT INTO events.server_configuration_change
    (instance_id,run_id,configuration_id,target_revision,server_major_version,
     configuration_name,source_type,previous_configured_value,
     configured_value,previous_in_use_value,in_use_value,
     configured_changed,in_use_changed,previous_observed_at,
     observed_at,detected_at)
   SELECT current.instance_id,current.run_id,current.configuration_id,
          current.target_revision,current.server_major_version,
          current.configuration_name,current.source_type,
          old.configured_value,current.configured_value,
          old.in_use_value,current.in_use_value,
          old.configured_value IS DISTINCT FROM current.configured_value,
          old.in_use_value IS DISTINCT FROM current.in_use_value,
          old.observed_at,current.observed_at,now_utc
   FROM telemetry.server_configuration_snapshot AS current
   JOIN telemetry.server_configuration_snapshot AS old
     ON old.run_id=previous_run_id
    AND old.instance_id=current.instance_id
    AND old.configuration_id=current.configuration_id
    AND old.target_revision=current.target_revision
    AND old.server_major_version=current.server_major_version
    AND old.configuration_name=current.configuration_name
    AND old.source_type=current.source_type
   WHERE current.run_id=p_run_id AND current.instance_id=p_instance_id
     AND (old.configured_value IS DISTINCT FROM current.configured_value
       OR old.in_use_value IS DISTINCT FROM current.in_use_value);
   GET DIAGNOSTICS event_count = ROW_COUNT;
   IF event_count>p_output_item_count
   THEN RAISE EXCEPTION 'Server configuration drift exceeded scan count'
        USING ERRCODE='22023'; END IF;
  END IF;
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
REVOKE ALL ON FUNCTION control.commit_server_configuration_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_server_configuration_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) TO sqlobserver_collector;
