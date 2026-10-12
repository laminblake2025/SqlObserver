-- Complete M5 activity runs with a visibility gap when target timestamps exceed
-- the five-minute clock window. Historical rows and applied migrations stay intact.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
CREATE OR REPLACE FUNCTION control.commit_activity_collection_run
(
    p_run_id uuid,
    p_instance_id uuid,
    p_target_revision bigint,
    p_collector_id text,
    p_collector_version integer,
    p_output_schema_version integer,
    p_schedule_revision bigint,
    p_scheduled_at timestamptz,
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint,
    p_request_digest bytea,
    p_outcome text,
    p_reason_code text,
    p_duration_ms bigint,
    p_attempt_count integer,
    p_source_row_count integer,
    p_output_item_count integer,
    p_response_bytes bigint,
    p_output_bytes bigint,
    p_loss_kind text,
    p_minimum_lost_items integer,
    p_loss_count_is_exact boolean,
    p_minimum_lost_bytes integer,
    p_next_circuit_state text,
    p_next_consecutive_failures integer,
    p_session_observed_ats timestamptz[],
    p_session_ids integer[],
    p_session_statuses text[],
    p_session_is_user_process boolean[],
    p_session_database_ids integer[],
    p_session_open_transaction_counts integer[],
    p_session_cpu_ms bigint[],
    p_session_memory_usage_pages bigint[],
    p_session_reads bigint[],
    p_session_writes bigint[],
    p_session_logical_reads bigint[],
    p_session_total_elapsed_ms bigint[],
    p_session_sizes integer[],
    p_request_observed_ats timestamptz[],
    p_request_session_ids integer[],
    p_request_ids integer[],
    p_request_statuses text[],
    p_request_commands text[],
    p_request_database_ids integer[],
    p_request_cpu_ms bigint[],
    p_request_total_elapsed_ms bigint[],
    p_request_reads bigint[],
    p_request_writes bigint[],
    p_request_logical_reads bigint[],
    p_request_row_counts bigint[],
    p_request_percent_complete double precision[],
    p_request_sizes integer[],
    p_wait_observed_ats timestamptz[],
    p_wait_types text[],
    p_waiting_tasks_counts bigint[],
    p_wait_time_ms bigint[],
    p_maximum_wait_time_ms bigint[],
    p_signal_wait_time_ms bigint[],
    p_wait_sizes integer[],
    p_blocking_observed_ats timestamptz[],
    p_blocked_session_ids integer[],
    p_blocker_kinds text[],
    p_blocker_session_ids integer[],
    p_blocking_wait_types text[],
    p_waiting_task_counts bigint[],
    p_wait_duration_ms bigint[],
    p_root_blocker_session_ids integer[],
    p_chain_depths integer[],
    p_chain_states text[],
    p_blocking_sizes integer[]
)
RETURNS TABLE
(
    result_status text,
    inserted_count integer,
    duplicate_count integer,
    rejected_count integer,
    persisted_bytes integer,
    committed_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_contract control.collector_contract%ROWTYPE;
    selected_schedule control.collector_schedule%ROWTYPE;
    existing_outcome telemetry.collection_run_outcome%ROWTYPE;
    session_count integer := coalesce(cardinality(p_session_observed_ats), -1);
    request_count integer := coalesce(cardinality(p_request_observed_ats), -1);
    wait_count integer := coalesce(cardinality(p_wait_observed_ats), -1);
    blocking_count integer := coalesce(cardinality(p_blocking_observed_ats), -1);
    payload_count integer;
    payload_bytes bigint;
    expected_payload_limit integer;
    begin_status text;
    begin_started_at timestamptz;
    captured_repository_time timestamptz;
    effective_completion_digest bytea;
    effective_loss_detected boolean;
    effective_truncated boolean;
    effective_gap_reason text;
    effective_gap_lost_items bigint;
    effective_gap_lost_bytes bigint;
    effective_gap_exact boolean;
    total_inserted integer;
    total_duplicates integer := 0;
    total_rejected integer;
    total_persisted_bytes bigint;
    next_open_until timestamptz;
    clock_skew_rejected boolean := false;
    submitted_outcome text := p_outcome;
BEGIN
    -- Cheap cardinality and scalar preflight precedes JSON construction and hashing.
    IF session_count < 0 OR request_count < 0 OR wait_count < 0 OR blocking_count < 0
       OR session_count > 512 OR request_count > 512 OR wait_count > 2048 OR blocking_count > 1024
       OR p_output_item_count NOT BETWEEN 0 AND 100000
       OR p_source_row_count NOT BETWEEN 0 AND 100000
       OR p_response_bytes NOT BETWEEN 0 AND 33554432
       OR p_output_bytes NOT BETWEEN 0 AND 33554432
       OR p_duration_ms NOT BETWEEN 0 AND 3600000
       OR p_attempt_count NOT BETWEEN 0 AND 2
       OR p_minimum_lost_items < 0 OR p_minimum_lost_bytes < 0
       OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
       OR cardinality(p_session_ids) IS DISTINCT FROM session_count
       OR cardinality(p_session_statuses) IS DISTINCT FROM session_count
       OR cardinality(p_session_is_user_process) IS DISTINCT FROM session_count
       OR cardinality(p_session_database_ids) IS DISTINCT FROM session_count
       OR cardinality(p_session_open_transaction_counts) IS DISTINCT FROM session_count
       OR cardinality(p_session_cpu_ms) IS DISTINCT FROM session_count
       OR cardinality(p_session_memory_usage_pages) IS DISTINCT FROM session_count
       OR cardinality(p_session_reads) IS DISTINCT FROM session_count
       OR cardinality(p_session_writes) IS DISTINCT FROM session_count
       OR cardinality(p_session_logical_reads) IS DISTINCT FROM session_count
       OR cardinality(p_session_total_elapsed_ms) IS DISTINCT FROM session_count
       OR cardinality(p_session_sizes) IS DISTINCT FROM session_count
       OR cardinality(p_request_session_ids) IS DISTINCT FROM request_count
       OR cardinality(p_request_ids) IS DISTINCT FROM request_count
       OR cardinality(p_request_statuses) IS DISTINCT FROM request_count
       OR cardinality(p_request_commands) IS DISTINCT FROM request_count
       OR cardinality(p_request_database_ids) IS DISTINCT FROM request_count
       OR cardinality(p_request_cpu_ms) IS DISTINCT FROM request_count
       OR cardinality(p_request_total_elapsed_ms) IS DISTINCT FROM request_count
       OR cardinality(p_request_reads) IS DISTINCT FROM request_count
       OR cardinality(p_request_writes) IS DISTINCT FROM request_count
       OR cardinality(p_request_logical_reads) IS DISTINCT FROM request_count
       OR cardinality(p_request_row_counts) IS DISTINCT FROM request_count
       OR cardinality(p_request_percent_complete) IS DISTINCT FROM request_count
       OR cardinality(p_request_sizes) IS DISTINCT FROM request_count
       OR cardinality(p_wait_types) IS DISTINCT FROM wait_count
       OR cardinality(p_waiting_tasks_counts) IS DISTINCT FROM wait_count
       OR cardinality(p_wait_time_ms) IS DISTINCT FROM wait_count
       OR cardinality(p_maximum_wait_time_ms) IS DISTINCT FROM wait_count
       OR cardinality(p_signal_wait_time_ms) IS DISTINCT FROM wait_count
       OR cardinality(p_wait_sizes) IS DISTINCT FROM wait_count
       OR cardinality(p_blocked_session_ids) IS DISTINCT FROM blocking_count
       OR cardinality(p_blocker_kinds) IS DISTINCT FROM blocking_count
       OR cardinality(p_blocker_session_ids) IS DISTINCT FROM blocking_count
       OR cardinality(p_blocking_wait_types) IS DISTINCT FROM blocking_count
       OR cardinality(p_waiting_task_counts) IS DISTINCT FROM blocking_count
       OR cardinality(p_wait_duration_ms) IS DISTINCT FROM blocking_count
       OR cardinality(p_root_blocker_session_ids) IS DISTINCT FROM blocking_count
       OR cardinality(p_chain_depths) IS DISTINCT FROM blocking_count
       OR cardinality(p_chain_states) IS DISTINCT FROM blocking_count
       OR cardinality(p_blocking_sizes) IS DISTINCT FROM blocking_count THEN
        RAISE EXCEPTION 'activity collector payload failed bounded array preflight'
            USING ERRCODE = '22023';
    END IF;

    payload_count := session_count + request_count + wait_count + blocking_count;
    SELECT contract.*
    INTO selected_contract
    FROM control.collector_contract AS contract
    WHERE contract.collector_id = p_collector_id
      AND contract.collector_version = p_collector_version;

    expected_payload_limit := CASE p_collector_id
        WHEN 'activity.sessions' THEN 512
        WHEN 'activity.requests' THEN 512
        WHEN 'waits.server' THEN 2048
        WHEN 'blocking.current' THEN 1024
        ELSE NULL
    END;
    IF NOT FOUND
       OR expected_payload_limit IS NULL
       OR selected_contract.output_schema_version <> p_output_schema_version
       OR p_source_row_count > selected_contract.maximum_rows
       OR p_response_bytes > selected_contract.maximum_response_bytes
       OR p_output_bytes > selected_contract.maximum_response_bytes
       OR p_attempt_count > selected_contract.maximum_attempts
       OR payload_count > expected_payload_limit
       OR (p_collector_id = 'activity.sessions' AND payload_count <> session_count)
       OR (p_collector_id = 'activity.requests' AND payload_count <> request_count)
       OR (p_collector_id = 'waits.server' AND payload_count <> wait_count)
       OR (p_collector_id = 'blocking.current' AND payload_count <> blocking_count) THEN
        RAISE EXCEPTION 'activity collector payload does not match the immutable contract'
            USING ERRCODE = '22023';
    END IF;

    SELECT
        coalesce((SELECT sum(value) FROM unnest(p_session_sizes) AS size(value)), 0)
        + coalesce((SELECT sum(value) FROM unnest(p_request_sizes) AS size(value)), 0)
        + coalesce((SELECT sum(value) FROM unnest(p_wait_sizes) AS size(value)), 0)
        + coalesce((SELECT sum(value) FROM unnest(p_blocking_sizes) AS size(value)), 0)
    INTO payload_bytes;

    IF EXISTS (SELECT 1 FROM unnest(p_session_sizes) AS value WHERE value <> 160)
       OR EXISTS (SELECT 1 FROM unnest(p_request_sizes) AS value WHERE value <> 176)
       OR EXISTS
       (
           SELECT 1 FROM unnest(p_wait_types, p_wait_sizes) AS item(wait_type, item_size)
           WHERE item.item_size IS DISTINCT FROM 112 + octet_length(item.wait_type)
       )
       OR EXISTS
       (
           SELECT 1 FROM unnest(p_blocking_wait_types, p_blocking_sizes) AS item(wait_type, item_size)
           WHERE item.item_size IS DISTINCT FROM 128 + octet_length(item.wait_type)
       )
       OR (p_outcome = 'output_invalid' AND payload_count <> 0)
       OR (p_outcome <> 'output_invalid' AND
           (payload_count <> p_output_item_count OR payload_bytes <> p_output_bytes)) THEN
        RAISE EXCEPTION 'activity collector payload and accounting do not match'
            USING ERRCODE = '22023';
    END IF;

    IF p_outcome NOT IN
       ('succeeded', 'partial', 'timed_out', 'transient_failure', 'permanent_failure',
        'permission_denied', 'unsupported', 'output_invalid', 'lease_lost', 'circuit_open')
       OR p_next_circuit_state NOT IN ('closed', 'open', 'half_open')
       OR p_loss_kind NOT IN
          ('none', 'source_row_limit', 'response_byte_limit', 'output_validation_failure',
           'ingestion_rejection', 'blocking_graph_limit')
       OR ((p_loss_kind = 'none') <> (p_minimum_lost_items = 0 AND p_minimum_lost_bytes = 0 AND p_loss_count_is_exact))
       OR (p_loss_kind <> 'none' AND p_minimum_lost_items = 0 AND p_minimum_lost_bytes = 0)
       OR (p_outcome = 'succeeded' AND
           (p_reason_code <> 'completed' OR p_loss_kind <> 'none'))
       OR (p_outcome = 'partial' AND NOT
           ((p_reason_code = 'source_row_limit' AND p_loss_kind = 'source_row_limit')
            OR (p_reason_code = 'response_byte_limit' AND p_loss_kind = 'response_byte_limit')
            OR (p_reason_code = 'blocking_graph_limit' AND p_loss_kind = 'blocking_graph_limit')))
       OR (p_reason_code = 'blocking_graph_limit' AND p_collector_id <> 'blocking.current')
       OR (p_outcome NOT IN ('succeeded', 'partial', 'output_invalid') AND payload_count <> 0)
       OR (p_outcome = 'output_invalid' AND p_loss_kind <> 'output_validation_failure') THEN
        RAISE EXCEPTION 'activity collector outcome and loss matrix is invalid'
            USING ERRCODE = '22023';
    END IF;

    IF EXISTS
       (
           SELECT 1
           FROM unnest(
               p_session_ids, p_session_statuses, p_session_database_ids,
               p_session_open_transaction_counts, p_session_cpu_ms,
               p_session_memory_usage_pages, p_session_reads, p_session_writes,
               p_session_logical_reads, p_session_total_elapsed_ms)
               AS item(session_id, status_code, database_id, open_transactions,
                       cpu_ms, memory_pages, reads, writes, logical_reads, elapsed_ms)
           WHERE item.session_id NOT BETWEEN 1 AND 32767
              OR item.status_code NOT IN ('running', 'sleeping', 'dormant', 'preconnect', 'other')
              OR (item.database_id IS NOT NULL AND item.database_id NOT BETWEEN 1 AND 32767)
              OR item.open_transactions < 0 OR item.cpu_ms < 0 OR item.memory_pages < 0
              OR item.reads < 0 OR item.writes < 0 OR item.logical_reads < 0 OR item.elapsed_ms < 0
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(
               p_request_session_ids, p_request_ids, p_request_statuses, p_request_commands,
               p_request_database_ids, p_request_cpu_ms, p_request_total_elapsed_ms,
               p_request_reads, p_request_writes, p_request_logical_reads,
               p_request_row_counts, p_request_percent_complete)
               AS item(session_id, request_id, status_code, command_code, database_id,
                       cpu_ms, elapsed_ms, reads, writes, logical_reads, row_count, percent_complete)
           WHERE item.session_id NOT BETWEEN 1 AND 32767 OR item.request_id < 0
              OR item.status_code NOT IN ('background', 'running', 'runnable', 'sleeping', 'suspended', 'other')
              OR item.command_code NOT IN ('select', 'insert', 'update', 'delete', 'merge', 'backup', 'restore', 'dbcc', 'other')
              OR (item.database_id IS NOT NULL AND item.database_id NOT BETWEEN 1 AND 32767)
              OR item.cpu_ms < 0 OR item.elapsed_ms < 0 OR item.reads < 0 OR item.writes < 0
              OR item.logical_reads < 0 OR item.row_count < 0
              OR item.percent_complete NOT BETWEEN 0 AND 100
              OR item.percent_complete IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_wait_types, p_waiting_tasks_counts, p_wait_time_ms,
                       p_maximum_wait_time_ms, p_signal_wait_time_ms)
               AS item(wait_type, tasks, wait_ms, maximum_ms, signal_ms)
           WHERE item.wait_type !~ '^[A-Z][A-Z0-9_]{0,119}$'
              OR item.tasks < 0 OR item.wait_ms < 0 OR item.maximum_ms < 0 OR item.signal_ms < 0
              OR item.maximum_ms > item.wait_ms OR item.signal_ms > item.wait_ms
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(
               p_blocked_session_ids, p_blocker_kinds, p_blocker_session_ids,
               p_blocking_wait_types, p_waiting_task_counts, p_wait_duration_ms,
               p_root_blocker_session_ids, p_chain_depths, p_chain_states)
               AS item(blocked_id, blocker_kind, blocker_id, wait_type, task_count,
                       duration_ms, root_id, chain_depth, chain_state)
           WHERE item.blocked_id NOT BETWEEN 1 AND 32767
              OR item.blocker_kind NOT IN
                 ('session', 'orphaned_distributed_transaction', 'deferred_recovery',
                  'undetermined', 'async_latch', 'other')
              OR ((item.blocker_kind = 'session') <> (item.blocker_id IS NOT NULL))
              OR (item.blocker_id IS NOT NULL AND item.blocker_id NOT BETWEEN 1 AND 32767)
              OR item.wait_type !~ '^[A-Z][A-Z0-9_]{0,119}$'
              OR item.task_count <= 0 OR item.duration_ms < 0
              OR (item.root_id IS NOT NULL AND item.root_id NOT BETWEEN 1 AND 32767)
              OR item.chain_depth NOT BETWEEN 1 AND 32
              OR item.chain_state NOT IN ('resolved', 'cycle', 'depth_limit', 'external_blocker')
       ) THEN
        RAISE EXCEPTION 'activity collector payload contains an invalid bounded value'
            USING ERRCODE = '22023';
    END IF;

    effective_completion_digest := sha256(convert_to(jsonb_build_object(
        'contract', 'sqlobserver.activity-completion.v1',
        'run', p_run_id, 'target', p_instance_id, 'revision', p_target_revision,
        'collector', p_collector_id, 'collectorVersion', p_collector_version,
        'outputVersion', p_output_schema_version, 'scheduleRevision', p_schedule_revision,
        'scheduledAt', p_scheduled_at, 'requestDigest', encode(p_request_digest, 'hex'),
        'outcome', p_outcome, 'reason', p_reason_code, 'durationMs', p_duration_ms,
        'attempts', p_attempt_count, 'sourceRows', p_source_row_count,
        'outputItems', p_output_item_count, 'responseBytes', p_response_bytes,
        'outputBytes', p_output_bytes, 'lossKind', p_loss_kind,
        'minimumLostItems', p_minimum_lost_items, 'lossExact', p_loss_count_is_exact,
        'minimumLostBytes', p_minimum_lost_bytes, 'nextCircuit', p_next_circuit_state,
        'nextFailures', p_next_consecutive_failures,
        'sessions', jsonb_build_array(
            p_session_observed_ats, p_session_ids, p_session_statuses,
            p_session_is_user_process, p_session_database_ids,
            p_session_open_transaction_counts, p_session_cpu_ms,
            p_session_memory_usage_pages, p_session_reads, p_session_writes,
            p_session_logical_reads, p_session_total_elapsed_ms, p_session_sizes),
        'requests', jsonb_build_array(
            p_request_observed_ats, p_request_session_ids, p_request_ids,
            p_request_statuses, p_request_commands, p_request_database_ids,
            p_request_cpu_ms, p_request_total_elapsed_ms, p_request_reads,
            p_request_writes, p_request_logical_reads, p_request_row_counts,
            p_request_percent_complete, p_request_sizes),
        'waits', jsonb_build_array(
            p_wait_observed_ats, p_wait_types, p_waiting_tasks_counts,
            p_wait_time_ms, p_maximum_wait_time_ms, p_signal_wait_time_ms, p_wait_sizes),
        'blocking', jsonb_build_array(
            p_blocking_observed_ats, p_blocked_session_ids, p_blocker_kinds,
            p_blocker_session_ids, p_blocking_wait_types, p_waiting_task_counts,
            p_wait_duration_ms, p_root_blocker_session_ids, p_chain_depths,
            p_chain_states, p_blocking_sizes))::text, 'UTF8'));

    SELECT result.result_status, result.started_at, result.repository_time
    INTO begin_status, begin_started_at, captured_repository_time
    FROM control.begin_collection_run
    (
        p_run_id, p_instance_id, p_target_revision, p_collector_id,
        p_collector_version, p_output_schema_version, p_schedule_revision,
        p_scheduled_at, p_work_key, p_owner_execution_id, p_fencing_token,
        p_request_digest
    ) AS result;

    IF begin_status = 'replayed' THEN
        SELECT outcome.* INTO existing_outcome
        FROM telemetry.collection_run_outcome AS outcome
        WHERE outcome.run_id = p_run_id;
        IF existing_outcome.completion_digest <> effective_completion_digest THEN
            RAISE EXCEPTION 'activity collector replay differs from the committed payload'
                USING ERRCODE = '22023';
        END IF;

        RETURN QUERY SELECT
            'replayed', existing_outcome.inserted_item_count,
            existing_outcome.duplicate_item_count, existing_outcome.rejected_item_count,
            existing_outcome.persisted_bytes::integer, existing_outcome.completed_at;
        RETURN;
    ELSIF begin_status NOT IN ('started', 'running_replay') THEN
        RETURN QUERY SELECT begin_status, 0, 0, 0, 0, NULL::timestamptz;
        RETURN;
    END IF;

    captured_repository_time := clock_timestamp();

    -- Invalid timestamp values are malformed input. Finite target-clock skew
    -- rejects the payload with an explicit gap and completes the fenced run.
    IF EXISTS
       (
           SELECT 1 FROM unnest(
               p_session_observed_ats || p_request_observed_ats ||
               p_wait_observed_ats || p_blocking_observed_ats) AS item(observed_at)
           WHERE item.observed_at IS NULL OR NOT isfinite(item.observed_at)
       ) THEN
        RAISE EXCEPTION 'activity observation timestamp is invalid'
            USING ERRCODE = '22023';
    END IF;

    IF EXISTS
       (
           SELECT 1 FROM unnest(
               p_session_observed_ats || p_request_observed_ats ||
               p_wait_observed_ats || p_blocking_observed_ats) AS item(observed_at)
           WHERE item.observed_at < begin_started_at - interval '5 minutes'
              OR item.observed_at > captured_repository_time + interval '5 minutes'
       ) THEN
        -- The digest above commits to the original request, including every
        -- rejected row. All four observation families share one atomic result.
        clock_skew_rejected := true;
        session_count := 0;
        request_count := 0;
        wait_count := 0;
        blocking_count := 0;
        payload_count := 0;
        payload_bytes := 0;
        p_minimum_lost_items := p_minimum_lost_items + greatest(1, p_output_item_count);
        p_minimum_lost_bytes := p_minimum_lost_bytes + p_output_bytes;
        p_loss_count_is_exact := p_loss_kind = 'none' AND p_output_item_count > 0;
        p_outcome := 'output_invalid';
        p_reason_code := 'output_validation_failed';
        p_loss_kind := 'output_validation_failure';
    END IF;

    IF NOT clock_skew_rejected THEN
    PERFORM control.ensure_activity_daily_partitions(partition_day)
    FROM
    (
        SELECT DISTINCT (observed_at AT TIME ZONE 'UTC')::date AS partition_day
        FROM unnest(p_session_observed_ats || p_request_observed_ats || p_wait_observed_ats)
            AS item(observed_at)
    ) AS requested;
    PERFORM control.ensure_blocking_monthly_partition(partition_month)
    FROM
    (
        SELECT DISTINCT date_trunc('month', observed_at AT TIME ZONE 'UTC')::date AS partition_month
        FROM unnest(p_blocking_observed_ats) AS item(observed_at)
    ) AS requested;

    INSERT INTO telemetry.activity_session_snapshot
    (
        observed_at, collection_run_id, instance_id, target_revision, session_id,
        status_code, is_user_process, database_id, open_transaction_count,
        cpu_ms, memory_usage_pages, reads, writes, logical_reads,
        total_elapsed_ms, collected_at
    )
    SELECT
        item.observed_at, p_run_id, p_instance_id, p_target_revision, item.session_id,
        item.status_code, item.is_user, item.database_id, item.open_transactions,
        item.cpu_ms, item.memory_pages, item.reads, item.writes, item.logical_reads,
        item.elapsed_ms, captured_repository_time
    FROM unnest(
        p_session_observed_ats, p_session_ids, p_session_statuses,
        p_session_is_user_process, p_session_database_ids,
        p_session_open_transaction_counts, p_session_cpu_ms,
        p_session_memory_usage_pages, p_session_reads, p_session_writes,
        p_session_logical_reads, p_session_total_elapsed_ms)
        AS item(observed_at, session_id, status_code, is_user, database_id,
                open_transactions, cpu_ms, memory_pages, reads, writes,
                logical_reads, elapsed_ms);

    INSERT INTO telemetry.activity_request_snapshot
    (
        observed_at, collection_run_id, instance_id, target_revision,
        session_id, request_id, status_code, command_code, database_id,
        cpu_ms, total_elapsed_ms, reads, writes, logical_reads, row_count,
        percent_complete, collected_at
    )
    SELECT
        item.observed_at, p_run_id, p_instance_id, p_target_revision,
        item.session_id, item.request_id, item.status_code, item.command_code,
        item.database_id, item.cpu_ms, item.elapsed_ms, item.reads, item.writes,
        item.logical_reads, item.row_count, item.percent_complete,
        captured_repository_time
    FROM unnest(
        p_request_observed_ats, p_request_session_ids, p_request_ids,
        p_request_statuses, p_request_commands, p_request_database_ids,
        p_request_cpu_ms, p_request_total_elapsed_ms, p_request_reads,
        p_request_writes, p_request_logical_reads, p_request_row_counts,
        p_request_percent_complete)
        AS item(observed_at, session_id, request_id, status_code, command_code,
                database_id, cpu_ms, elapsed_ms, reads, writes, logical_reads,
                row_count, percent_complete);

    INSERT INTO telemetry.server_wait_snapshot
    (
        observed_at, collection_run_id, instance_id, target_revision,
        wait_type, waiting_tasks_count, wait_time_ms, maximum_wait_time_ms,
        signal_wait_time_ms, collected_at
    )
    SELECT
        item.observed_at, p_run_id, p_instance_id, p_target_revision,
        item.wait_type, item.tasks, item.wait_ms, item.maximum_ms,
        item.signal_ms, captured_repository_time
    FROM unnest(
        p_wait_observed_ats, p_wait_types, p_waiting_tasks_counts,
        p_wait_time_ms, p_maximum_wait_time_ms, p_signal_wait_time_ms)
        AS item(observed_at, wait_type, tasks, wait_ms, maximum_ms, signal_ms);

    INSERT INTO events.blocking_edge
    (
        observed_at, edge_id, collection_run_id, instance_id, target_revision,
        blocked_session_id, blocker_kind, blocker_session_id, wait_type,
        waiting_task_count, wait_duration_ms, root_blocker_session_id,
        chain_depth, chain_state, collected_at
    )
    SELECT
        item.observed_at, gen_random_uuid(), p_run_id, p_instance_id, p_target_revision,
        item.blocked_id, item.blocker_kind, item.blocker_id, item.wait_type,
        item.task_count, item.duration_ms, item.root_id, item.chain_depth,
        item.chain_state, captured_repository_time
    FROM unnest(
        p_blocking_observed_ats, p_blocked_session_ids, p_blocker_kinds,
        p_blocker_session_ids, p_blocking_wait_types, p_waiting_task_counts,
        p_wait_duration_ms, p_root_blocker_session_ids, p_chain_depths,
        p_chain_states)
        AS item(observed_at, blocked_id, blocker_kind, blocker_id, wait_type,
                task_count, duration_ms, root_id, chain_depth, chain_state);

    END IF;

    total_inserted := payload_count;
    total_rejected := p_output_item_count - total_inserted;
    IF total_rejected < 0 THEN
        RAISE EXCEPTION 'activity collector output accounting underflowed after persistence'
            USING ERRCODE = '22023';
    END IF;
    total_persisted_bytes := payload_bytes;
    effective_loss_detected := p_loss_kind <> 'none';
    effective_truncated := p_loss_kind IN ('source_row_limit', 'response_byte_limit', 'blocking_graph_limit');

    PERFORM control.assert_worker_lease(p_work_key, p_owner_execution_id, p_fencing_token);
    SELECT schedule.* INTO selected_schedule
    FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id
    FOR UPDATE;

    IF selected_schedule.active_run_id <> p_run_id
       OR selected_schedule.schedule_revision <> p_schedule_revision THEN
        RAISE EXCEPTION 'activity collector schedule changed before commit'
            USING ERRCODE = '55000';
    END IF;

    IF
    (
        submitted_outcome IN ('succeeded', 'partial')
        AND (p_next_circuit_state <> 'closed' OR p_next_consecutive_failures <> 0)
    )
    OR
    (
        submitted_outcome IN ('transient_failure', 'timed_out')
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
        submitted_outcome NOT IN ('succeeded', 'partial', 'transient_failure', 'timed_out')
        AND
        (
            p_next_circuit_state <> selected_schedule.circuit_state
            OR p_next_consecutive_failures <> selected_schedule.consecutive_failure_count
        )
    ) THEN
        RAISE EXCEPTION 'activity collector circuit transition differs from the authoritative schedule state'
            USING ERRCODE = '22023';
    END IF;

    next_open_until := CASE
        WHEN p_next_circuit_state = 'open'
            THEN captured_repository_time + selected_contract.circuit_open_interval
        ELSE NULL
    END;

    INSERT INTO telemetry.collection_run_outcome
    (
        run_id, outcome, reason_code, attempt_count, retry_count, duration_ms,
        source_row_count, output_item_count, inserted_item_count,
        duplicate_item_count, rejected_item_count, response_bytes, output_bytes,
        persisted_bytes, truncated, loss_detected, loss_kind, loss_count_exact,
        lost_row_count, lost_byte_count, completion_digest, completed_at
    )
    VALUES
    (
        p_run_id, p_outcome, p_reason_code, p_attempt_count,
        greatest(p_attempt_count - 1, 0), p_duration_ms, p_source_row_count,
        p_output_item_count, total_inserted, total_duplicates, total_rejected,
        p_response_bytes, p_output_bytes, total_persisted_bytes,
        effective_truncated, effective_loss_detected, p_loss_kind,
        p_loss_count_is_exact, p_minimum_lost_items, p_minimum_lost_bytes,
        effective_completion_digest, captured_repository_time
    );

    IF p_outcome <> 'succeeded' OR effective_loss_detected THEN
        effective_gap_reason := CASE
            WHEN clock_skew_rejected THEN 'target_clock_skew'
            WHEN p_loss_kind = 'ingestion_rejection' THEN 'ingestion_rejection'
            WHEN p_loss_kind = 'output_validation_failure' THEN 'output_validation_failed'
            WHEN p_loss_kind = 'source_row_limit' THEN 'source_row_limit'
            WHEN p_loss_kind = 'response_byte_limit' THEN 'response_byte_limit'
            WHEN p_loss_kind = 'blocking_graph_limit' THEN 'blocking_graph_limit'
            ELSE p_reason_code
        END;
        effective_gap_lost_items := CASE WHEN effective_loss_detected THEN p_minimum_lost_items ELSE 1 END;
        effective_gap_lost_bytes := CASE WHEN effective_loss_detected THEN p_minimum_lost_bytes ELSE 0 END;
        effective_gap_exact := CASE WHEN effective_loss_detected THEN p_loss_count_is_exact ELSE false END;
        INSERT INTO telemetry.visibility_gap
        (
            gap_id, run_id, instance_id, collector_id, reason_code,
            gap_started_at, gap_ended_at, lost_row_count, lost_byte_count,
            count_is_exact, recorded_at
        )
        VALUES
        (
            gen_random_uuid(), p_run_id, p_instance_id, p_collector_id,
            effective_gap_reason, p_scheduled_at, captured_repository_time,
            effective_gap_lost_items, effective_gap_lost_bytes,
            effective_gap_exact, captured_repository_time
        );
    END IF;

    UPDATE control.collector_schedule AS schedule
    SET
        active_run_id = NULL,
        next_due_at = CASE
            WHEN p_next_circuit_state = 'open' THEN next_open_until
            ELSE captured_repository_time + schedule.collection_interval
        END,
        circuit_state = p_next_circuit_state,
        consecutive_failure_count = p_next_consecutive_failures,
        circuit_open_until = next_open_until,
        last_completed_at = captured_repository_time,
        last_succeeded_at = CASE
            WHEN p_outcome IN ('succeeded', 'partial') THEN captured_repository_time
            ELSE schedule.last_succeeded_at
        END,
        last_outcome = p_outcome,
        updated_at = captured_repository_time
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id;

    PERFORM control.assert_worker_lease(p_work_key, p_owner_execution_id, p_fencing_token);
    RETURN QUERY SELECT
        'committed', total_inserted, total_duplicates, total_rejected,
        total_persisted_bytes::integer, captured_repository_time;
END
$sqlobserver$;
