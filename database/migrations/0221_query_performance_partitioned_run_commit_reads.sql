-- Lock the selected run source throughout query evidence and protected links.
-- The existing atomic payload, replay and lease contracts remain unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.commit_query_performance_collection_run_canonical(
 p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_version integer,p_output_schema_version integer,
 p_schedule_revision bigint,p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
 p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,p_attempt_count integer,p_source_row_count integer,
 p_output_item_count integer,p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
 p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,p_next_consecutive_failures integer,
 p_completion_digest bytea,p_window_start timestamptz,p_window_end timestamptz,p_source events.query_performance_source,p_source_state text,p_coverage text,p_freshness boolean,p_truncated boolean,p_payload jsonb)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path = pg_catalog,control,events,telemetry AS $fn$
DECLARE run_row telemetry.collection_run%ROWTYPE; outcome_row telemetry.collection_run_outcome%ROWTYPE; schedule_row control.collector_schedule%ROWTYPE; contract_row control.collector_contract%ROWTYPE; now_utc timestamptz := clock_timestamp(); item_count integer := 0; bytes_count integer := 0; completion bytea;
BEGIN
 IF p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_collector_version<>1 OR p_output_schema_version<>1 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_completion_digest)<>32 OR jsonb_typeof(p_payload)<>'object' OR jsonb_typeof(p_payload->'observations')<>'array' OR jsonb_typeof(p_payload->'databaseStatuses')<>'array' OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open') OR p_reason_code IS NULL OR ((p_attempt_count=0 AND p_outcome<>'circuit_open') OR (p_outcome='circuit_open' AND p_attempt_count<>0) OR (p_outcome NOT IN ('circuit_open') AND p_attempt_count NOT BETWEEN 1 AND 2)) OR p_source_row_count NOT BETWEEN 0 AND 512256 OR p_output_item_count NOT BETWEEN 0 AND 20000 OR p_response_bytes NOT BETWEEN 0 AND 8388608 OR p_output_bytes NOT BETWEEN 0 AND 8388608 OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit','output_validation_failure','ingestion_rejection','duplicate_overlap') OR p_minimum_lost_items NOT BETWEEN 0 AND 20000 OR p_minimum_lost_bytes NOT BETWEEN 0 AND 8388608 OR p_next_circuit_state NOT IN ('closed','open','half_open') OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000 OR (p_outcome='succeeded' AND (p_loss_kind<>'none' OR p_reason_code<>'completed')) OR (p_outcome='partial' AND p_loss_kind='none') OR (p_outcome='circuit_open' AND (p_loss_kind<>'none' OR p_reason_code<>'circuit_currently_open')) OR (p_outcome='lease_lost' AND (p_loss_kind<>'none' OR p_reason_code<>'lease_ownership_lost')) OR (p_outcome='output_invalid' AND (p_loss_kind<>'output_validation_failure' OR p_reason_code<>'output_validation_failed')) OR (p_outcome='timed_out' AND (p_loss_kind<>'none' OR p_reason_code<>'deadline_exceeded')) OR (p_outcome='transient_failure' AND (p_loss_kind<>'none' OR p_reason_code<>'transient_target_failure')) OR (p_outcome='permanent_failure' AND (p_loss_kind<>'none' OR p_reason_code<>'permanent_target_failure')) OR (p_outcome='permission_denied' AND (p_loss_kind<>'none' OR p_reason_code<>'required_permission_missing')) OR (p_outcome='unsupported' AND (p_loss_kind<>'none' OR p_reason_code NOT IN ('target_unsupported','target_version_unsupported','target_platform_unsupported','target_edition_unsupported','capability_missing','capability_profile_missing','capability_profile_stale'))) OR (p_outcome NOT IN ('succeeded','partial') AND jsonb_array_length(p_payload->'observations')<>0) OR (p_outcome NOT IN ('succeeded','partial') AND (jsonb_typeof(p_payload->'targetStatus')<>'object' OR p_payload->'targetStatus'->>'status' IS NULL OR p_payload->'targetStatus'->>'reason' IS NULL)) THEN RAISE EXCEPTION 'query performance canonical preflight failed' USING ERRCODE='22023'; END IF;
 SELECT * INTO run_row FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision OR run_row.collector_id<>'queries.performance' OR run_row.collector_version<>p_collector_version OR run_row.output_schema_version<>p_output_schema_version OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token OR run_row.request_digest<>p_request_digest THEN RAISE EXCEPTION 'query performance canonical run identity or request digest mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO outcome_row FROM telemetry.collection_run_outcome WHERE run_id=p_run_id FOR UPDATE;
 IF FOUND THEN
   IF coalesce((SELECT completion_digest FROM events.query_performance_run WHERE collection_run_id=p_run_id),(SELECT completion_digest FROM telemetry.collection_run_outcome WHERE run_id=p_run_id)) IS DISTINCT FROM p_completion_digest THEN RAISE EXCEPTION 'query performance replay digest mismatch' USING ERRCODE='40001'; END IF;
   RETURN QUERY SELECT 'replayed',outcome_row.inserted_item_count,outcome_row.duplicate_item_count,outcome_row.rejected_item_count,outcome_row.persisted_bytes::integer,outcome_row.completed_at; RETURN;
 END IF;
 IF octet_length(p_completion_digest) <> 32 OR (p_outcome NOT IN ('succeeded','partial') AND coalesce(jsonb_array_length(p_payload->'observations'),0) <> 0) THEN RAISE EXCEPTION 'query performance completion evidence is invalid for outcome' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 IF p_outcome IN ('succeeded','partial') OR jsonb_array_length(p_payload->'databaseStatuses') > 0 THEN PERFORM control.commit_query_performance_collection_run(p_run_id,p_instance_id,p_target_revision,p_completion_digest,p_window_start,p_window_end,p_source,p_source_state,p_coverage,p_freshness,p_truncated,p_fencing_token,p_payload); ELSE INSERT INTO events.query_performance_run(collection_run_id,instance_id,target_revision,window_start,window_end,source,source_state,coverage,freshness,truncated,target_status,target_reason,completion_digest) VALUES(p_run_id,p_instance_id,p_target_revision,p_window_start,p_window_end,p_source,p_source_state,p_coverage,p_freshness,p_truncated,p_payload->'targetStatus'->>'status',p_payload->'targetStatus'->>'reason',p_completion_digest); END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule WHERE instance_id=p_instance_id AND collector_id='queries.performance' FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id<>p_run_id OR schedule_row.schedule_revision<>p_schedule_revision OR schedule_row.target_revision<>p_target_revision THEN RAISE EXCEPTION 'query performance schedule fence missing' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract WHERE collector_id='queries.performance' AND collector_version=1;
 IF NOT FOUND THEN RAISE EXCEPTION 'query performance contract missing' USING ERRCODE='55000'; END IF;
 SELECT count(*)::integer,coalesce(sum(256),0)::integer INTO item_count,bytes_count FROM events.query_performance_observation WHERE collection_run_id=p_run_id;
 IF item_count>p_output_item_count OR p_output_item_count-item_count>20000 THEN RAISE EXCEPTION 'query performance output accounting mismatch' USING ERRCODE='22023'; END IF;
 completion := p_completion_digest;
 INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at) VALUES(p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),p_duration_ms,p_source_row_count,p_output_item_count,item_count,p_output_item_count-item_count,0,p_response_bytes,p_output_bytes,bytes_count,p_loss_kind IN ('source_row_limit','response_byte_limit','duplicate_overlap'),p_loss_kind<>'none',p_loss_kind,p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,completion,now_utc);
 IF p_outcome<>'succeeded' OR p_loss_kind<>'none' THEN INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at) VALUES(gen_random_uuid(),p_run_id,p_instance_id,'queries.performance',p_reason_code,p_scheduled_at,now_utc,greatest(p_minimum_lost_items,CASE WHEN p_outcome='succeeded' THEN 0 ELSE 1 END),p_minimum_lost_bytes,p_loss_count_is_exact,now_utc); END IF;
 IF (p_outcome IN ('succeeded','partial') AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0)) OR (p_outcome IN ('timed_out','transient_failure') AND (p_next_consecutive_failures<>schedule_row.consecutive_failure_count+1 OR p_next_circuit_state<>CASE WHEN schedule_row.circuit_state='half_open' OR schedule_row.consecutive_failure_count+1>=contract_row.circuit_failure_threshold THEN 'open' ELSE 'closed' END)) OR (p_outcome='circuit_open' AND (p_next_circuit_state<>'open' OR p_next_consecutive_failures<>schedule_row.consecutive_failure_count)) OR (p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','circuit_open') AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0)) THEN RAISE EXCEPTION 'query performance circuit transition differs from schedule' USING ERRCODE='22023'; END IF;
 UPDATE control.collector_schedule SET active_run_id=NULL,next_due_at=CASE WHEN p_next_circuit_state='open' THEN now_utc+contract_row.circuit_open_interval ELSE now_utc+collection_interval END,circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,circuit_open_until=CASE WHEN p_next_circuit_state='open' THEN now_utc+contract_row.circuit_open_interval ELSE NULL END,last_completed_at=now_utc,last_succeeded_at=CASE WHEN p_outcome='succeeded' THEN now_utc ELSE last_succeeded_at END,last_outcome=p_outcome,updated_at=now_utc WHERE instance_id=p_instance_id AND collector_id='queries.performance';
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 'committed',item_count,p_output_item_count-item_count,0,bytes_count,now_utc;
END; $fn$;

CREATE OR REPLACE FUNCTION control.commit_query_text_links(
    p_run_id uuid, p_instance_id uuid, p_fence_token bigint, p_links jsonb)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, control, events, security, telemetry
AS $fn$
DECLARE
    v_work_key text;
    v_owner uuid;
    v_valid integer;
    v_persisted integer;
BEGIN
    IF p_run_id IS NULL OR p_instance_id IS NULL OR p_fence_token IS NULL
       OR p_links IS NULL
       OR p_fence_token <= 0 OR jsonb_typeof(p_links) <> 'array'
       OR jsonb_array_length(p_links) > 20000
       OR octet_length(p_links::text) > 8388608
       OR NOT control.query_performance_target_claim(p_instance_id)
    THEN
        RAISE EXCEPTION 'query text link preflight failed' USING ERRCODE = '22023';
    END IF;

    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        WHERE jsonb_typeof(item.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(item.value)) <> 4
           OR coalesce(item.value->>'database_id', '') !~ '^[0-9]{1,5}$'
           OR coalesce(item.value->>'query_fingerprint', '') !~ '^[0-9a-f]{64}$'
           OR coalesce(item.value->>'payload_id', '') !~* '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'
           OR coalesce(item.value->>'fingerprint', '') !~ '^[0-9a-f]{64}$'
           OR (item.value->>'database_id')::integer NOT BETWEEN 1 AND 32767
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        GROUP BY item.value->>'database_id', item.value->>'query_fingerprint'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query text links have an invalid or duplicate identity'
            USING ERRCODE = '22023';
    END IF;

    SELECT run.work_key, run.owner_execution_id INTO v_work_key, v_owner
    FROM telemetry.lock_collection_run_runtime(p_run_id) AS run
    WHERE run.instance_id = p_instance_id
      AND run.collector_id = 'queries.performance';
    IF v_work_key IS NULL OR v_owner IS NULL THEN
        RAISE EXCEPTION 'query text link run is missing' USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);

    WITH supplied AS (
        SELECT * FROM jsonb_to_recordset(p_links) AS entry(
            database_id integer, query_fingerprint text,
            payload_id uuid, fingerprint text)
    )
    SELECT count(*) INTO v_valid
    FROM supplied AS item
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = p_run_id
     AND query_row.instance_id = p_instance_id
     AND query_row.database_id = item.database_id
     AND query_row.query_fingerprint = decode(item.query_fingerprint, 'hex')
    JOIN security.protected_diagnostic_payload AS payload
      ON payload.payload_id = item.payload_id
     AND payload.instance_id = p_instance_id
     AND payload.payload_kind = 'query_text'
     AND payload.fingerprint = decode(item.fingerprint, 'hex');
    IF v_valid <> jsonb_array_length(p_links) THEN
        RAISE EXCEPTION 'query text link target, query, or payload mismatch'
            USING ERRCODE = '23503';
    END IF;

    INSERT INTO events.query_performance_content_link
        (collection_run_id, instance_id, database_id,
         query_fingerprint, content_reference, content_available)
    SELECT p_run_id, p_instance_id, item.database_id,
           decode(item.query_fingerprint, 'hex'), item.payload_id, true
    FROM jsonb_to_recordset(p_links) AS item(
        database_id integer, query_fingerprint text,
        payload_id uuid, fingerprint text)
    ON CONFLICT DO NOTHING;

    SELECT count(*) INTO v_persisted
    FROM jsonb_to_recordset(p_links) AS item(
        database_id integer, query_fingerprint text,
        payload_id uuid, fingerprint text)
    JOIN events.query_performance_content_link AS link
      ON link.collection_run_id = p_run_id
     AND link.instance_id = p_instance_id
     AND link.database_id = item.database_id
     AND link.query_fingerprint = decode(item.query_fingerprint, 'hex')
     AND link.content_reference = item.payload_id
     AND link.content_available;
    IF v_persisted <> v_valid THEN
        RAISE EXCEPTION 'query text link did not persist as available'
            USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);
    RETURN v_persisted;
END;
$fn$;

CREATE OR REPLACE FUNCTION control.commit_query_plan_links(
    p_run_id uuid, p_instance_id uuid, p_fence_token bigint, p_links jsonb)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path = pg_catalog, control, events, security, telemetry
AS $fn$
DECLARE
    v_work_key text;
    v_owner uuid;
    v_valid integer;
    v_persisted integer;
BEGIN
    IF p_run_id IS NULL OR p_instance_id IS NULL OR p_fence_token IS NULL
       OR p_fence_token <= 0 OR p_links IS NULL
       OR jsonb_typeof(p_links) <> 'array'
       OR jsonb_array_length(p_links) > 20000
       OR octet_length(p_links::text) > 8388608
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query plan link preflight failed' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        WHERE jsonb_typeof(item.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(item.value)) <> 5
           OR coalesce(item.value->>'database_id', '') !~ '^[0-9]{1,5}$'
           OR coalesce(item.value->>'query_fingerprint', '') !~ '^[0-9a-f]{64}$'
           OR coalesce(item.value->>'plan_fingerprint', '') !~ '^[0-9a-f]{64}$'
           OR coalesce(item.value->>'payload_id', '') !~*
              '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'
           OR coalesce(item.value->>'fingerprint', '') !~ '^[0-9a-f]{64}$'
    ) THEN
        RAISE EXCEPTION 'query plan link shape is invalid' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        WHERE (item.value->>'database_id')::integer NOT BETWEEN 1 AND 32767
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        GROUP BY item.value->>'database_id', item.value->>'query_fingerprint',
                 item.value->>'plan_fingerprint'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query plan links have an invalid or duplicate identity'
            USING ERRCODE = '22023';
    END IF;

    SELECT run.work_key, run.owner_execution_id INTO v_work_key, v_owner
    FROM telemetry.lock_collection_run_runtime(p_run_id) AS run
    WHERE run.instance_id = p_instance_id
      AND run.collector_id = 'queries.performance';
    IF v_work_key IS NULL OR v_owner IS NULL THEN
        RAISE EXCEPTION 'query plan link run is missing' USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);

    WITH supplied AS (
        SELECT * FROM jsonb_to_recordset(p_links) AS item(
            database_id integer, query_fingerprint text, plan_fingerprint text,
            payload_id uuid, fingerprint text)
    )
    SELECT count(*) INTO v_valid
    FROM supplied AS item
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = p_run_id
     AND query_row.instance_id = p_instance_id
     AND query_row.database_id = item.database_id
     AND query_row.query_fingerprint = decode(item.query_fingerprint, 'hex')
    JOIN events.query_performance_plan AS plan_row
      ON plan_row.collection_run_id = query_row.collection_run_id
     AND plan_row.database_id = query_row.database_id
     AND plan_row.query_fingerprint = query_row.query_fingerprint
     AND plan_row.plan_fingerprint = decode(item.plan_fingerprint, 'hex')
    JOIN security.protected_diagnostic_payload AS payload
      ON payload.payload_id = item.payload_id
     AND payload.instance_id = p_instance_id
     AND payload.payload_kind = 'execution_plan'
     AND payload.fingerprint = decode(item.fingerprint, 'hex');
    IF v_valid <> jsonb_array_length(p_links) THEN
        RAISE EXCEPTION 'query plan link target, plan, or payload mismatch'
            USING ERRCODE = '23503';
    END IF;

    INSERT INTO events.query_performance_plan_content_link
        (collection_run_id, instance_id, database_id, query_fingerprint,
         plan_fingerprint, content_reference, content_available)
    SELECT p_run_id, p_instance_id, item.database_id,
           decode(item.query_fingerprint, 'hex'),
           decode(item.plan_fingerprint, 'hex'), item.payload_id, true
    FROM jsonb_to_recordset(p_links) AS item(
        database_id integer, query_fingerprint text, plan_fingerprint text,
        payload_id uuid, fingerprint text)
    ON CONFLICT DO NOTHING;

    SELECT count(*) INTO v_persisted
    FROM jsonb_to_recordset(p_links) AS item(
        database_id integer, query_fingerprint text, plan_fingerprint text,
        payload_id uuid, fingerprint text)
    JOIN events.query_performance_plan_content_link AS link
      ON link.collection_run_id = p_run_id
     AND link.instance_id = p_instance_id
     AND link.database_id = item.database_id
     AND link.query_fingerprint = decode(item.query_fingerprint, 'hex')
     AND link.plan_fingerprint = decode(item.plan_fingerprint, 'hex')
     AND link.content_reference = item.payload_id
     AND link.content_available;
    IF v_persisted <> v_valid THEN
        RAISE EXCEPTION 'query plan link did not persist as available'
            USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);
    RETURN v_persisted;
END;
$fn$;

CREATE OR REPLACE FUNCTION control.commit_query_plan_wait_snapshots(
    p_run_id uuid, p_instance_id uuid, p_fence_token bigint, p_snapshots jsonb)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path = pg_catalog, control, events, telemetry
AS $fn$
DECLARE
    v_work_key text;
    v_owner uuid;
    v_valid integer;
    v_inserted integer;
BEGIN
    IF p_run_id IS NULL OR p_instance_id IS NULL OR p_fence_token IS NULL
       OR p_fence_token <= 0 OR p_snapshots IS NULL
       OR jsonb_typeof(p_snapshots) <> 'array'
       OR jsonb_array_length(p_snapshots) > 8
       OR octet_length(p_snapshots::text) > 8192
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query wait snapshot preflight failed' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value)
        WHERE jsonb_typeof(snapshot.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(snapshot.value)) <> 4
           OR coalesce(snapshot.value->>'database_id','') !~ '^[0-9]{1,5}$'
           OR coalesce(snapshot.value->>'query_fingerprint','') !~ '^[0-9a-f]{64}$'
           OR coalesce(snapshot.value->>'plan_fingerprint','') !~ '^[0-9a-f]{64}$'
           OR jsonb_typeof(snapshot.value->'categories') <> 'array'
    ) THEN
        RAISE EXCEPTION 'query wait snapshot shape is invalid' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value)
        WHERE (snapshot.value->>'database_id')::integer NOT BETWEEN 1 AND 32767
           OR jsonb_array_length(snapshot.value->'categories') > 32
           OR octet_length((snapshot.value->'categories')::text) > 4096
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value)
        GROUP BY snapshot.value->>'database_id',
                 snapshot.value->>'query_fingerprint',
                 snapshot.value->>'plan_fingerprint'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query wait snapshots have duplicate or invalid plans'
            USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value),
             jsonb_array_elements(snapshot.value->'categories') AS category(value)
        WHERE jsonb_typeof(category.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(category.value)) <> 2
           OR NOT CASE WHEN coalesce(category.value->>'category','') ~ '^[0-9]{1,2}$'
                 THEN (category.value->>'category')::integer BETWEEN 0 AND 31
                 ELSE false END
           OR NOT CASE WHEN coalesce(category.value->>'waitMilliseconds','') ~ '^[0-9]{1,19}$'
                 THEN (category.value->>'waitMilliseconds')::numeric
                      BETWEEN 1 AND 9223372036854775807
                 ELSE false END
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value),
             jsonb_array_elements(snapshot.value->'categories') AS category(value)
        GROUP BY snapshot.value->>'database_id',
                 snapshot.value->>'query_fingerprint',
                 snapshot.value->>'plan_fingerprint',
                 category.value->>'category'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query wait category shape or identity is invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT run.work_key, run.owner_execution_id INTO v_work_key, v_owner
    FROM telemetry.lock_collection_run_runtime(p_run_id) AS run
    WHERE run.instance_id = p_instance_id
      AND run.collector_id = 'queries.performance';
    IF v_work_key IS NULL OR v_owner IS NULL THEN
        RAISE EXCEPTION 'query wait run is missing' USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);

    WITH supplied AS (
        SELECT * FROM jsonb_to_recordset(p_snapshots) AS item(
            database_id integer, query_fingerprint text,
            plan_fingerprint text, categories jsonb)
    )
    SELECT count(*) INTO v_valid
    FROM supplied AS item
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = p_run_id
     AND query_row.instance_id = p_instance_id
     AND query_row.database_id = item.database_id
     AND query_row.query_fingerprint = decode(item.query_fingerprint,'hex')
    JOIN events.query_performance_plan AS plan_row
      ON plan_row.collection_run_id = query_row.collection_run_id
     AND plan_row.database_id = query_row.database_id
     AND plan_row.query_fingerprint = query_row.query_fingerprint
     AND plan_row.plan_fingerprint = decode(item.plan_fingerprint,'hex');
    IF v_valid <> jsonb_array_length(p_snapshots) THEN
        RAISE EXCEPTION 'query wait target, query, or plan mismatch'
            USING ERRCODE = '23503';
    END IF;

    INSERT INTO events.query_performance_wait_snapshot
        (collection_run_id, instance_id, database_id,
         query_fingerprint, plan_fingerprint, categories)
    SELECT p_run_id, p_instance_id, item.database_id,
           decode(item.query_fingerprint,'hex'),
           decode(item.plan_fingerprint,'hex'), item.categories
    FROM jsonb_to_recordset(p_snapshots) AS item(
        database_id integer, query_fingerprint text,
        plan_fingerprint text, categories jsonb)
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);
    RETURN v_inserted;
END;
$fn$;
