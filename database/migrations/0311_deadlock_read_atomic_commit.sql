-- Stage the atomic incremental producer privately. No v2 catalog activation or
-- application EXECUTE grant until the source/alert paths are qualified together.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Keep canonical outcomes append-only. Only hashes are retained per page;
-- bounded source metadata and XML are never duplicated into this ledger.
CREATE TABLE events.deadlock_read_receipt
(
    completed_at timestamptz NOT NULL CHECK(isfinite(completed_at)),
    run_id uuid NOT NULL REFERENCES telemetry.collection_run_outcome(run_id),
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32),
    read_digest bytea NOT NULL CHECK(octet_length(read_digest)=32),
    predecessor_digest bytea NULL CHECK(predecessor_digest IS NULL OR octet_length(predecessor_digest)=32),
    checkpoint_digest bytea NOT NULL CHECK(octet_length(checkpoint_digest)=32),
    PRIMARY KEY(completed_at,run_id)
) PARTITION BY RANGE(completed_at);
ALTER TABLE events.deadlock_read_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.deadlock_read_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlock_read_receipt_owner ON events.deadlock_read_receipt
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON events.deadlock_read_receipt FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER deadlock_read_receipt_append_only BEFORE UPDATE OR DELETE ON events.deadlock_read_receipt
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

ALTER TABLE system.partition_registry DROP CONSTRAINT ck_partition_registry_parent;
ALTER TABLE system.partition_registry ADD CONSTRAINT ck_partition_registry_parent CHECK
  (parent_schema IN ('telemetry'::name,'events'::name,'analytics'::name,'alerting'::name)
   AND parent_table IN
  ('raw_metric_sample','diagnostic_event','activity_session_snapshot','activity_request_snapshot','server_wait_snapshot','blocking_edge',
   'database_inventory_snapshot','database_file_snapshot','host_metric_snapshot_v2','replication_snapshot_v2',
   'backup_status_snapshot','sql_agent_failure_scan_snapshot','sql_agent_failure_occurrence','tempdb_snapshot','tempdb_file_snapshot',
   'availability_group_replica_snapshot','availability_group_database_snapshot','query_performance_observation',
   'deadlock_summary','deadlock_participant','deadlock_relation','state_history','delivery_attempt','delivery_outbox',
   'metric_rollup_v2','evidence_packet_v2',
   'activity_session_snapshot_v2','activity_request_snapshot_v2','server_wait_snapshot_v2','blocking_edge_v2',
   'database_inventory_snapshot_v2','database_file_snapshot_v2','query_performance_observation_v2','query_performance_observation_v3',
   'deadlock_summary_v2','deadlock_participant_v2','deadlock_relation_v2','state_history_v2','delivery_attempt_v2','delivery_outbox_v2',
   'backup_status_snapshot_v2','sql_agent_failure_scan_snapshot_v2','sql_agent_failure_occurrence_v2','tempdb_snapshot_v2','tempdb_file_snapshot_v2',
   'availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2','sql_volume_snapshot',
   'query_store_group_watermark','query_store_group_delta','collection_run_v2',
   'availability_replica_evidence','availability_database_replica_evidence',
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline','deadlock_read_receipt'));

CREATE FUNCTION control.ensure_deadlock_read_receipt_partition(p_completed timestamptz)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $partition$
DECLARE v_start timestamptz; v_end timestamptz; v_name text; v_child oid;
BEGIN
    IF p_completed IS NULL OR NOT isfinite(p_completed)
        OR abs(extract(epoch FROM p_completed-clock_timestamp()))>300 THEN
        RAISE EXCEPTION 'Deadlock receipt partition requires current canonical completion' USING ERRCODE='22023'; END IF;
    v_start:=date_trunc('month',p_completed); v_end:=v_start+interval '1 month';
    v_name:='deadlock_read_receipt_p'||to_char(v_start,'YYYYMM');
    v_child:=to_regclass(format('events.%I',v_name));
    IF v_child IS NULL THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:deadlock-read-receipt:partition-set',0));
        -- Only creation uses the fleet-wide DDL lock. Existing partitions have
        -- no per-page ALTER/INSERT operations or global commit serialization.
        IF to_regclass(format('events.%I',v_name)) IS NULL THEN
            EXECUTE format('CREATE TABLE events.%I PARTITION OF events.deadlock_read_receipt FOR VALUES FROM (%L) TO (%L)',v_name,v_start,v_end);
            EXECUTE format('ALTER TABLE events.%I ENABLE ROW LEVEL SECURITY',v_name);
            EXECUTE format('ALTER TABLE events.%I FORCE ROW LEVEL SECURITY',v_name);
            EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_name);
            INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
            VALUES('events','deadlock_read_receipt','events',v_name,'month',v_start,v_end);
        END IF;
        v_child:=to_regclass(format('events.%I',v_name));
    END IF;
    IF NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=v_child AND inhparent='events.deadlock_read_receipt'::regclass)
        OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid=v_child AND relkind='r' AND relrowsecurity AND relforcerowsecurity
            AND pg_get_userbyid(relowner)='sqlobserver_migrator'
            AND pg_get_expr(relpartbound,oid)=format('FOR VALUES FROM (%L) TO (%L)',v_start,v_end)) THEN
        RAISE EXCEPTION 'Deadlock receipt partition identity differs' USING ERRCODE='55000'; END IF;
    IF NOT EXISTS(SELECT 1 FROM system.partition_registry WHERE parent_schema='events' AND parent_table='deadlock_read_receipt'
        AND partition_schema='events' AND partition_name=v_name AND partition_granularity='month' AND lifecycle_state='attached'
        AND range_start=v_start AND range_end=v_end) THEN
        RAISE EXCEPTION 'Deadlock receipt partition registry differs' USING ERRCODE='55000'; END IF;
END $partition$;
REVOKE ALL ON FUNCTION control.ensure_deadlock_read_receipt_partition(timestamptz)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.deadlock_read_progress(p_previous jsonb,p_epoch text,p_positions jsonb,p_ended boolean)
RETURNS TABLE(progress_kind text,bookmark jsonb,rows_read integer,complete_buffers integer)
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE
SET search_path=pg_catalog
AS $progress$
DECLARE v_row jsonb; v_last jsonb; v_frontier jsonb; v_buffers integer:=0; v_count integer;
    v_generation bigint; v_index integer; v_offset bigint;
BEGIN
    IF p_epoch IS NULL OR p_epoch !~ '^[0-9a-f]{64}$' OR p_ended IS NULL
        OR jsonb_typeof(p_positions) IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'Deadlock read progress shape is invalid' USING ERRCODE='22023'; END IF;
    v_count:=jsonb_array_length(p_positions);
    IF v_count>8193 THEN RAISE EXCEPTION 'Deadlock read progress exceeds its row budget' USING ERRCODE='22023'; END IF;
    -- Validate every row even when a changed source requires a reset.
    FOR v_row IN SELECT value FROM jsonb_array_elements(p_positions) LOOP
        IF jsonb_typeof(v_row) IS DISTINCT FROM 'object'
            OR (SELECT count(*) FROM jsonb_object_keys(v_row))<>3
            OR NOT v_row ?& ARRAY['generation','rolloverIndex','bufferOffset']
            OR jsonb_typeof(v_row->'generation') IS DISTINCT FROM 'number' OR v_row->>'generation' !~ '^[1-9][0-9]{0,18}$'
            OR jsonb_typeof(v_row->'rolloverIndex') IS DISTINCT FROM 'number' OR v_row->>'rolloverIndex' !~ '^(0|[1-9][0-9]{0,9})$'
            OR jsonb_typeof(v_row->'bufferOffset') IS DISTINCT FROM 'number' OR v_row->>'bufferOffset' !~ '^(0|[1-9][0-9]{0,8})$'
        THEN RAISE EXCEPTION 'Deadlock read position is invalid' USING ERRCODE='22023'; END IF;
        v_generation:=(v_row->>'generation')::bigint; v_index:=(v_row->>'rolloverIndex')::integer; v_offset:=(v_row->>'bufferOffset')::bigint;
        IF v_offset>105906176 THEN RAISE EXCEPTION 'Deadlock read offset exceeds trusted file bounds' USING ERRCODE='22023'; END IF;
    END LOOP;
    IF p_previous IS NOT NULL AND p_previous->>'sourceEpoch' IS DISTINCT FROM p_epoch THEN
        RETURN QUERY SELECT 'reset_required',NULL::jsonb,v_count,0; RETURN; END IF;
    v_last:=NULLIF(p_previous->'bookmark','null'::jsonb);
    FOR v_row IN SELECT value FROM jsonb_array_elements(p_positions) LOOP
        IF v_last IS NOT NULL THEN
            IF ((v_row->>'generation')::bigint=(v_last->>'generation')::bigint
                    AND (v_row->>'rolloverIndex')::integer<>(v_last->>'rolloverIndex')::integer)
                OR (v_row->>'generation')::bigint<(v_last->>'generation')::bigint
                OR ((v_row->>'generation')::bigint=(v_last->>'generation')::bigint
                    AND (v_row->>'bufferOffset')::bigint<(v_last->>'bufferOffset')::bigint)
                OR (v_buffers=0 AND v_row=v_last) THEN
                RETURN QUERY SELECT 'reset_required',NULL::jsonb,v_count,0; RETURN;
            END IF;
            IF v_row<>v_last AND v_buffers>0 THEN v_frontier:=v_last; END IF;
        END IF;
        IF v_row IS DISTINCT FROM v_last THEN v_buffers:=v_buffers+1; END IF;
        v_last:=v_row;
    END LOOP;
    IF p_ended AND v_buffers>0 THEN v_frontier:=v_last; END IF;
    RETURN QUERY SELECT CASE WHEN p_ended THEN 'caught_up' ELSE 'catching_up' END,
        coalesce(v_frontier,NULLIF(p_previous->'bookmark','null'::jsonb)),v_count,
        CASE WHEN p_ended THEN v_buffers ELSE greatest(0,v_buffers-1) END;
END $progress$;
REVOKE ALL ON FUNCTION control.deadlock_read_progress(jsonb,text,jsonb,boolean)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.commit_deadlock_read_collection_run
(
    p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_id text,
    p_collector_version integer,p_output_schema_version integer,p_schedule_revision bigint,
    p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
    p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,
    p_attempt_count integer,p_source_row_count integer,p_output_item_count integer,
    p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
    p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,
    p_next_consecutive_failures integer,p_occurred_ats timestamptz[],p_event_ids uuid[],
    p_fingerprints bytea[],p_participant_counts integer[],p_relation_counts integer[],
    p_parse_truncated boolean[],p_participant_json jsonb[],p_relation_json jsonb[],p_sizes integer[],
    p_read jsonb
)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,
    rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $commit$
DECLARE v_result record; v_outcome telemetry.collection_run_outcome%ROWTYPE; v_saved control.deadlock_read_checkpoint%ROWTYPE;
    v_receipt events.deadlock_read_receipt%ROWTYPE;
    v_proof jsonb; v_previous jsonb; v_window jsonb; v_progress record; v_bookmark jsonb;
    v_read_digest bytea; v_expected bytea; v_epoch bytea; v_state text; v_read_at timestamptz; v_retained timestamptz;
    v_ended boolean; v_loss boolean; v_resume boolean; v_saved_exists boolean; v_next_digest bytea;
BEGIN
    IF p_collector_id IS DISTINCT FROM 'deadlocks.system-health' OR p_collector_version IS DISTINCT FROM 2
        OR p_output_schema_version IS DISTINCT FROM 2
        OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    THEN RAISE EXCEPTION 'Deadlock incremental commit requires exact v2 target scope' USING ERRCODE='42501'; END IF;
    IF p_read IS NULL OR jsonb_typeof(p_read) IS DISTINCT FROM 'object'
        OR octet_length(p_read::text)>1048576
        OR (SELECT count(*) FROM jsonb_object_keys(p_read))<>9
        OR NOT p_read ?& ARRAY['schemaVersion','expectedCheckpointDigest','sourceState','sourceEpoch',
            'readAtUtc','retainedFromUtc','positions','reachedEnd','sourceLoss']
        OR p_read->'schemaVersion' IS DISTINCT FROM '1'::jsonb
        OR jsonb_typeof(p_read->'sourceState') IS DISTINCT FROM 'string'
        OR p_read->>'sourceState' NOT IN ('ready','missing','config_invalid','reset_required','failed')
        OR jsonb_typeof(p_read->'reachedEnd') IS DISTINCT FROM 'boolean'
        OR jsonb_typeof(p_read->'sourceLoss') IS DISTINCT FROM 'boolean'
        OR jsonb_typeof(p_read->'positions') IS DISTINCT FROM 'array'
        OR jsonb_array_length(p_read->'positions')>8193
        OR (p_read->'expectedCheckpointDigest'<>'null'::jsonb
            AND (jsonb_typeof(p_read->'expectedCheckpointDigest') IS DISTINCT FROM 'string'
                 OR p_read->>'expectedCheckpointDigest' !~ '^[0-9a-f]{64}$'))
        OR (p_read->'sourceEpoch'<>'null'::jsonb
            AND (jsonb_typeof(p_read->'sourceEpoch') IS DISTINCT FROM 'string' OR p_read->>'sourceEpoch' !~ '^[0-9a-f]{64}$'))
        OR (p_read->'readAtUtc'<>'null'::jsonb AND (jsonb_typeof(p_read->'readAtUtc') IS DISTINCT FROM 'string'
            OR p_read->>'readAtUtc' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z$'))
        OR (p_read->'retainedFromUtc'<>'null'::jsonb AND (jsonb_typeof(p_read->'retainedFromUtc') IS DISTINCT FROM 'string'
            OR p_read->>'retainedFromUtc' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z$'))
    THEN RAISE EXCEPTION 'Deadlock incremental source proof is invalid' USING ERRCODE='22023'; END IF;
    v_state:=p_read->>'sourceState'; v_epoch:=decode(p_read->>'sourceEpoch','hex');
    v_expected:=decode(p_read->>'expectedCheckpointDigest','hex');
    v_read_at:=(p_read->>'readAtUtc')::timestamptz; v_retained:=(p_read->>'retainedFromUtc')::timestamptz;
    v_ended:=(p_read->>'reachedEnd')::boolean; v_loss:=(p_read->>'sourceLoss')::boolean;
    IF (v_read_at IS NOT NULL AND (NOT isfinite(v_read_at) OR v_read_at<'0001-01-01T00:05:00Z'::timestamptz))
        OR (v_retained IS NOT NULL AND (NOT isfinite(v_retained) OR v_retained<'0001-01-01T00:00:00Z'::timestamptz
            OR v_read_at IS NULL OR v_retained>v_read_at))
        OR (v_state='ready' AND (v_epoch IS NULL OR v_read_at IS NULL OR p_outcome NOT IN ('succeeded','partial')))
        OR (v_state<>'ready' AND (v_epoch IS NOT NULL OR v_ended OR NOT v_loss
            OR jsonb_array_length(p_read->'positions')<>0 OR cardinality(p_event_ids)<>0
            OR p_outcome IN ('succeeded','partial')))
        OR (v_state='ready' AND v_loss AND p_loss_kind='none')
        OR (v_state='ready' AND cardinality(p_event_ids)>jsonb_array_length(p_read->'positions'))
        OR (v_state='ready' AND (p_source_row_count<>jsonb_array_length(p_read->'positions')
            OR p_response_bytes<128+64*jsonb_array_length(p_read->'positions')
            OR (v_ended AND jsonb_array_length(p_read->'positions')=8193)
            OR (NOT v_loss AND array_position(p_parse_truncated,true) IS NOT NULL)))
        OR p_response_bytes>1048576 OR p_source_row_count>8193
    THEN RAISE EXCEPTION 'Deadlock incremental source proof contradicts its outcome' USING ERRCODE='22023'; END IF;
    v_read_digest:=sha256(convert_to(p_read::text,'UTF8'));
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    -- Match the canonical writer lock order. Serialize predecessor comparison,
    -- events, outcome, snapshot and checkpoint under the same target lock.
    PERFORM pg_advisory_xact_lock(hashtextextended(p_instance_id::text,0));
    SELECT o.* INTO v_outcome FROM telemetry.collection_run_outcome o WHERE o.run_id=p_run_id;
    IF FOUND THEN
        SELECT r.* INTO v_receipt FROM events.deadlock_read_receipt r WHERE r.completed_at=v_outcome.completed_at AND r.run_id=p_run_id;
        IF NOT FOUND OR v_receipt.instance_id IS DISTINCT FROM p_instance_id
            OR v_receipt.completion_digest IS DISTINCT FROM v_outcome.completion_digest
            OR v_receipt.read_digest IS DISTINCT FROM v_read_digest
            OR v_receipt.predecessor_digest IS DISTINCT FROM v_expected
        THEN RAISE EXCEPTION 'Deadlock incremental replay source differs from committed receipt' USING ERRCODE='40001'; END IF;
        -- Canonical core validates the exact original identity, graphs and all
        -- accounting fields. It does not replace the latest snapshot on replay.
        SELECT r.* INTO STRICT v_result FROM control.commit_deadlock_collection_run_core(
            p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,p_output_schema_version,p_schedule_revision,
            p_scheduled_at,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_outcome,p_reason_code,p_duration_ms,
            p_attempt_count,p_source_row_count,p_output_item_count,p_response_bytes,p_output_bytes,p_loss_kind,p_minimum_lost_items,
            p_loss_count_is_exact,p_minimum_lost_bytes,p_next_circuit_state,p_next_consecutive_failures,p_occurred_ats,p_event_ids,
            p_fingerprints,p_participant_counts,p_relation_counts,p_parse_truncated,p_participant_json,p_relation_json,p_sizes) r;
        IF v_result.result_status<>'replayed' THEN RAISE EXCEPTION 'Deadlock incremental canonical replay is inconsistent' USING ERRCODE='55000'; END IF;
        PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
        RETURN QUERY SELECT 'replayed',0,0,0,0,v_result.committed_at; RETURN;
    END IF;
    SELECT r.payload INTO STRICT v_proof FROM control.read_deadlock_read_checkpoint(
        p_instance_id,p_target_revision,p_schedule_revision,p_run_id,p_owner_execution_id,p_fencing_token) r;
    SELECT c.* INTO v_saved FROM control.deadlock_read_checkpoint c WHERE c.instance_id=p_instance_id FOR UPDATE;
    v_saved_exists:=FOUND;
    IF (CASE WHEN v_saved_exists THEN v_saved.checkpoint_digest ELSE NULL END) IS DISTINCT FROM v_expected THEN
        RAISE EXCEPTION 'Deadlock incremental predecessor changed' USING ERRCODE='40001'; END IF;
    v_resume:=(v_proof->>'resumeAllowed')::boolean;
    IF v_resume THEN v_previous:=v_proof->'checkpoint'; END IF;
    IF v_state='ready' THEN
        SELECT r.* INTO STRICT v_progress FROM control.deadlock_read_progress(
            v_previous,encode(v_epoch,'hex'),p_read->'positions',v_ended) r;
        IF v_progress.progress_kind='reset_required' THEN
            -- A changed incarnation/regression is an explicit visibility gap,
            -- never an empty current window or an advanced bookmark.
            IF cardinality(p_event_ids)<>0 THEN RAISE EXCEPTION 'Reset source cannot publish deadlock events' USING ERRCODE='22023'; END IF;
            v_state:='reset_required'; v_loss:=true; v_ended:=false;
        ELSE v_bookmark:=v_progress.bookmark; END IF;
        IF v_previous IS NULL OR v_previous->>'sourceEpoch'<>encode(v_epoch,'hex') THEN
            v_previous:=NULL;
            IF v_retained IS NULL THEN RAISE EXCEPTION 'Cold deadlock read requires a retained boundary' USING ERRCODE='22023'; END IF;
        END IF;
    ELSE
        -- Keep bounded event history through outages when its revision still
        -- matches. Failed reads never prove end-of-stream or advance progress.
        IF v_previous IS NOT NULL THEN
            v_epoch:=decode(v_previous->>'sourceEpoch','hex');
            IF v_state<>'reset_required' THEN v_bookmark:=NULLIF(v_previous->'bookmark','null'::jsonb); END IF;
        END IF;
    END IF;
    SELECT r.* INTO STRICT v_result FROM control.commit_deadlock_collection_run_core(
        p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,p_output_schema_version,p_schedule_revision,
        p_scheduled_at,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_outcome,p_reason_code,p_duration_ms,
        p_attempt_count,p_source_row_count,p_output_item_count,p_response_bytes,p_output_bytes,p_loss_kind,p_minimum_lost_items,
        p_loss_count_is_exact,p_minimum_lost_bytes,p_next_circuit_state,p_next_consecutive_failures,p_occurred_ats,p_event_ids,
        p_fingerprints,p_participant_counts,p_relation_counts,p_parse_truncated,p_participant_json,p_relation_json,p_sizes) r;
    IF v_result.result_status<>'committed' THEN RAISE EXCEPTION 'Deadlock incremental active commit is inconsistent' USING ERRCODE='55000'; END IF;
    SELECT o.* INTO STRICT v_outcome FROM telemetry.collection_run_outcome o WHERE o.run_id=p_run_id;
    IF v_epoch IS NOT NULL THEN
        -- PostgreSQL's canonical completion anchors the rolling window. All
        -- event timestamps already have its microsecond precision. Clock skew
        -- invalidates coverage instead of publishing a current zero count.
        v_loss:=v_loss OR p_loss_kind<>'none' OR p_outcome NOT IN ('succeeded','partial')
            OR (v_read_at IS NOT NULL AND abs(extract(epoch FROM v_read_at-v_result.committed_at))>300)
            OR coalesce(v_retained>v_result.committed_at,false);
        -- A target-clock retention boundary in the future cannot manufacture
        -- old coverage. Keep a cold gap at canonical completion instead.
        IF v_retained>v_result.committed_at THEN v_retained:=v_result.committed_at; END IF;
        v_window:=control.merge_deadlock_read_window(NULLIF(v_previous->'window','null'::jsonb),p_instance_id,p_target_revision,
            v_epoch,v_result.committed_at,v_retained,p_event_ids,p_occurred_ats,p_parse_truncated,v_ended,v_loss);
    END IF;
    INSERT INTO alerting.deadlock_source_snapshot(instance_id,target_revision,schedule_revision,run_id,completed_at,
        completion_digest,event_ids,occurred_ats,parse_truncated)
    VALUES(p_instance_id,p_target_revision,p_schedule_revision,p_run_id,v_result.committed_at,v_outcome.completion_digest,
        p_event_ids,p_occurred_ats,p_parse_truncated)
    ON CONFLICT(instance_id) DO UPDATE SET target_revision=EXCLUDED.target_revision,schedule_revision=EXCLUDED.schedule_revision,
        run_id=EXCLUDED.run_id,completed_at=EXCLUDED.completed_at,completion_digest=EXCLUDED.completion_digest,
        event_ids=EXCLUDED.event_ids,occurred_ats=EXCLUDED.occurred_ats,parse_truncated=EXCLUDED.parse_truncated;
    INSERT INTO control.deadlock_read_checkpoint(instance_id,target_revision,schedule_revision,run_id,completed_at,
        completion_digest,source_state,source_epoch,file_generation,rollover_index,buffer_offset,window_state)
    VALUES(p_instance_id,p_target_revision,p_schedule_revision,p_run_id,v_result.committed_at,v_outcome.completion_digest,v_state,
        v_epoch,(v_bookmark->>'generation')::bigint,(v_bookmark->>'rolloverIndex')::integer,(v_bookmark->>'bufferOffset')::bigint,v_window)
    ON CONFLICT(instance_id) DO UPDATE SET target_revision=EXCLUDED.target_revision,schedule_revision=EXCLUDED.schedule_revision,
        run_id=EXCLUDED.run_id,completed_at=EXCLUDED.completed_at,completion_digest=EXCLUDED.completion_digest,
        source_state=EXCLUDED.source_state,source_epoch=EXCLUDED.source_epoch,file_generation=EXCLUDED.file_generation,
        rollover_index=EXCLUDED.rollover_index,buffer_offset=EXCLUDED.buffer_offset,window_state=EXCLUDED.window_state
    RETURNING checkpoint_digest INTO v_next_digest;
    PERFORM control.ensure_deadlock_read_receipt_partition(v_result.committed_at);
    INSERT INTO events.deadlock_read_receipt(completed_at,run_id,instance_id,completion_digest,read_digest,predecessor_digest,checkpoint_digest)
    VALUES(v_result.committed_at,p_run_id,p_instance_id,v_outcome.completion_digest,v_read_digest,v_expected,v_next_digest);
    IF (v_window->>'reason' IN ('source_incomplete','window_overflow','coverage_gap') OR v_state<>'ready')
        AND NOT EXISTS(SELECT 1 FROM telemetry.visibility_gap g WHERE g.run_id=p_run_id) THEN
        INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,
            lost_row_count,lost_byte_count,count_is_exact,recorded_at)
        VALUES(gen_random_uuid(),p_run_id,p_instance_id,p_collector_id,'output_validation_failed',p_scheduled_at,
            v_result.committed_at,1,0,false,v_result.committed_at);
    END IF;
    -- The last fence covers every write, including the source proof receipt.
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    RETURN QUERY SELECT v_result.result_status,v_result.inserted_count,v_result.duplicate_count,v_result.rejected_count,
        v_result.persisted_bytes,v_result.committed_at;
END $commit$;
REVOKE ALL ON FUNCTION control.commit_deadlock_read_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[],jsonb)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
