-- Explicit replacement of the core commit contract. It preserves the original
-- fenced lifecycle and accepts historical 8/9/11 metric runs alongside the new 12
-- metric query. Keeping the function text in this migration avoids runtime
-- pg_get_functiondef rewriting.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
CREATE OR REPLACE FUNCTION control.commit_collection_run
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
    p_metric_observed_ats timestamptz[],
    p_metric_sample_ids uuid[],
    p_metric_keys text[],
    p_metric_values double precision[],
    p_metric_dimensions jsonb[],
    p_metric_sizes integer[],
    p_database_observed_ats timestamptz[],
    p_database_ids integer[],
    p_database_names text[],
    p_database_states text[],
    p_database_recovery_models text[],
    p_database_user_access text[],
    p_database_is_read_only boolean[],
    p_database_compatibility_levels integer[],
    p_database_sizes integer[],
    p_file_observed_ats timestamptz[],
    p_file_database_ids integer[],
    p_file_ids integer[],
    p_file_logical_names text[],
    p_file_types text[],
    p_file_states text[],
    p_file_size_bytes bigint[],
    p_file_maximum_size_bytes bigint[],
    p_file_growth_bytes bigint[],
    p_file_growth_percents integer[],
    p_file_read_counts bigint[],
    p_file_write_counts bigint[],
    p_file_bytes_read bigint[],
    p_file_bytes_written bigint[],
    p_file_io_stall_ms bigint[],
    p_file_sizes integer[]
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
    begin_status text;
    begin_started_at timestamptz;
    captured_repository_time timestamptz;
    selected_contract control.collector_contract%ROWTYPE;
    selected_schedule control.collector_schedule%ROWTYPE;
    existing_run telemetry.collection_run%ROWTYPE;
    existing_outcome telemetry.collection_run_outcome%ROWTYPE;
    metric_count integer := cardinality(p_metric_sample_ids);
    database_count integer := cardinality(p_database_ids);
    file_count integer := cardinality(p_file_ids);
    inserted_metrics integer := 0;
    inserted_metric_bytes bigint := 0;
    inserted_databases integer := 0;
    inserted_database_bytes bigint := 0;
    inserted_files integer := 0;
    inserted_file_bytes bigint := 0;
    total_inserted integer;
    total_duplicates integer;
    total_rejected integer;
    total_persisted_bytes bigint;
    effective_loss_detected boolean;
    effective_truncated boolean;
    effective_gap_reason text;
    effective_gap_lost_items bigint;
    effective_gap_lost_bytes bigint;
    effective_gap_exact boolean;
    next_open_until timestamptz;
    effective_completion_digest bytea;
BEGIN
    -- Cheap bounded preflight must run before canonical JSON construction and
    -- hashing so collector-role input cannot force unbounded materialization.
    IF p_run_id IS NULL
       OR p_run_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_instance_id IS NULL
       OR p_instance_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_owner_execution_id IS NULL
       OR p_owner_execution_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR octet_length(p_request_digest) IS DISTINCT FROM 32
       OR p_collector_id IS NULL
       OR p_collector_id NOT IN ('engine.core', 'database.inventory', 'database.files')
       OR p_outcome IS NULL
       OR p_outcome NOT IN
       (
           'succeeded', 'partial', 'timed_out', 'transient_failure',
           'permanent_failure', 'permission_denied', 'unsupported',
           'output_invalid'
       )
       OR p_reason_code IS NULL
       OR p_reason_code NOT IN
       (
           'completed', 'source_row_limit', 'response_byte_limit',
           'deadline_exceeded', 'transient_target_failure',
           'permanent_target_failure', 'required_permission_missing',
           'target_unsupported', 'output_validation_failed',
           'capability_profile_missing', 'capability_profile_stale',
           'capability_missing', 'target_version_unsupported',
           'target_platform_unsupported', 'target_edition_unsupported'
       )
       OR p_loss_kind IS NULL
       OR p_loss_kind NOT IN
       (
           'none', 'source_row_limit', 'response_byte_limit',
           'output_validation_failure', 'ingestion_rejection'
       )
       OR p_next_circuit_state IS NULL
       OR p_next_circuit_state NOT IN ('closed', 'open', 'half_open')
       OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
       OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 1 AND 2
       OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 100000
       OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 100000
       OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 33554432
       OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 33554432
       OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 100000
       OR p_loss_count_is_exact IS NULL
       OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 33554432
       OR p_next_consecutive_failures IS NULL
       OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
       OR metric_count IS NULL OR metric_count NOT BETWEEN 0 AND 32
       OR database_count IS NULL OR database_count NOT BETWEEN 0 AND 1000
       OR file_count IS NULL OR file_count NOT BETWEEN 0 AND 1000
       OR EXISTS
       (
           SELECT 1
           FROM
           (
               VALUES
                   (array_ndims(p_metric_observed_ats)),
                   (array_ndims(p_metric_sample_ids)),
                   (array_ndims(p_metric_keys)),
                   (array_ndims(p_metric_values)),
                   (array_ndims(p_metric_dimensions)),
                   (array_ndims(p_metric_sizes)),
                   (array_ndims(p_database_observed_ats)),
                   (array_ndims(p_database_ids)),
                   (array_ndims(p_database_names)),
                   (array_ndims(p_database_states)),
                   (array_ndims(p_database_recovery_models)),
                   (array_ndims(p_database_user_access)),
                   (array_ndims(p_database_is_read_only)),
                   (array_ndims(p_database_compatibility_levels)),
                   (array_ndims(p_database_sizes)),
                   (array_ndims(p_file_observed_ats)),
                   (array_ndims(p_file_database_ids)),
                   (array_ndims(p_file_ids)),
                   (array_ndims(p_file_logical_names)),
                   (array_ndims(p_file_types)),
                   (array_ndims(p_file_states)),
                   (array_ndims(p_file_size_bytes)),
                   (array_ndims(p_file_maximum_size_bytes)),
                   (array_ndims(p_file_growth_bytes)),
                   (array_ndims(p_file_growth_percents)),
                   (array_ndims(p_file_read_counts)),
                   (array_ndims(p_file_write_counts)),
                   (array_ndims(p_file_bytes_read)),
                   (array_ndims(p_file_bytes_written)),
                   (array_ndims(p_file_io_stall_ms)),
                   (array_ndims(p_file_sizes))
           ) AS dimensions(dimension_count)
           WHERE dimensions.dimension_count IS NOT NULL
             AND dimensions.dimension_count <> 1
       )
       OR cardinality(p_metric_observed_ats) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_keys) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_values) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_dimensions) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_sizes) IS DISTINCT FROM metric_count
       OR cardinality(p_database_observed_ats) IS DISTINCT FROM database_count
       OR cardinality(p_database_names) IS DISTINCT FROM database_count
       OR cardinality(p_database_states) IS DISTINCT FROM database_count
       OR cardinality(p_database_recovery_models) IS DISTINCT FROM database_count
       OR cardinality(p_database_user_access) IS DISTINCT FROM database_count
       OR cardinality(p_database_is_read_only) IS DISTINCT FROM database_count
       OR cardinality(p_database_compatibility_levels) IS DISTINCT FROM database_count
       OR cardinality(p_database_sizes) IS DISTINCT FROM database_count
       OR cardinality(p_file_observed_ats) IS DISTINCT FROM file_count
       OR cardinality(p_file_database_ids) IS DISTINCT FROM file_count
       OR cardinality(p_file_logical_names) IS DISTINCT FROM file_count
       OR cardinality(p_file_types) IS DISTINCT FROM file_count
       OR cardinality(p_file_states) IS DISTINCT FROM file_count
       OR cardinality(p_file_size_bytes) IS DISTINCT FROM file_count
       OR cardinality(p_file_maximum_size_bytes) IS DISTINCT FROM file_count
       OR cardinality(p_file_growth_bytes) IS DISTINCT FROM file_count
       OR cardinality(p_file_growth_percents) IS DISTINCT FROM file_count
       OR cardinality(p_file_read_counts) IS DISTINCT FROM file_count
       OR cardinality(p_file_write_counts) IS DISTINCT FROM file_count
       OR cardinality(p_file_bytes_read) IS DISTINCT FROM file_count
       OR cardinality(p_file_bytes_written) IS DISTINCT FROM file_count
       OR cardinality(p_file_io_stall_ms) IS DISTINCT FROM file_count
       OR cardinality(p_file_sizes) IS DISTINCT FROM file_count THEN
        RAISE EXCEPTION 'collector commit preflight identity, scalar, array shape, or cardinality is invalid'
            USING ERRCODE = '22023';
    END IF;

    -- Array cardinalities are now proven to be within their fixed contract
    -- bounds, so bounded element inspection cannot materialize untrusted pages.
    IF EXISTS
       (
           SELECT 1
           FROM unnest(p_metric_keys) AS metric(metric_key)
           GROUP BY metric.metric_key
           HAVING count(*) > 1
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(
               p_metric_sample_ids,
               p_metric_keys,
               p_metric_values,
               p_metric_dimensions,
               p_metric_sizes)
               AS metric(sample_id, metric_key, metric_value, dimensions, item_size)
           WHERE metric.sample_id IS NULL
              OR metric.sample_id = '00000000-0000-0000-0000-000000000000'::uuid
              OR metric.metric_key IS NULL
              OR octet_length(metric.metric_key) NOT BETWEEN 1 AND 128
              OR metric.metric_value IS NULL
              OR metric.metric_value IN
                 (
                     'NaN'::double precision,
                     'Infinity'::double precision,
                     '-Infinity'::double precision
                 )
              OR (p_collector_id = 'engine.core' AND metric.metric_value < 0)
              OR metric.dimensions IS NULL
              OR jsonb_typeof(metric.dimensions) <> 'object'
              OR octet_length(metric.dimensions::text) > 16384
              OR metric.item_size IS NULL
              OR metric.item_size <= 0
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(
               p_database_names,
               p_database_states,
               p_database_recovery_models,
               p_database_user_access,
               p_database_sizes)
               AS database(database_name, state_code, recovery_model, user_access, item_size)
           WHERE database.database_name IS NULL
              OR octet_length(database.database_name) NOT BETWEEN 1 AND 1024
              OR database.state_code IS NULL OR octet_length(database.state_code) NOT BETWEEN 1 AND 32
              OR database.recovery_model IS NULL OR octet_length(database.recovery_model) NOT BETWEEN 1 AND 32
              OR database.user_access IS NULL OR octet_length(database.user_access) NOT BETWEEN 1 AND 32
              OR database.item_size IS NULL OR database.item_size <= 0
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_file_logical_names, p_file_types, p_file_states, p_file_sizes)
               AS file(logical_name, file_type, state_code, item_size)
           WHERE file.logical_name IS NULL
              OR octet_length(file.logical_name) NOT BETWEEN 1 AND 1024
              OR file.file_type IS NULL OR octet_length(file.file_type) NOT BETWEEN 1 AND 32
              OR file.state_code IS NULL OR octet_length(file.state_code) NOT BETWEEN 1 AND 32
              OR file.item_size IS NULL OR file.item_size <= 0
       ) THEN
        RAISE EXCEPTION 'collector commit preflight exceeded a fixed M4 bound'
            USING ERRCODE = '22023';
    END IF;

    effective_completion_digest := sha256(convert_to(
        jsonb_build_object(
            'format', 1,
            'summary', jsonb_build_object(
                'outcome', p_outcome,
                'reason', p_reason_code,
                'duration_ms', p_duration_ms,
                'attempt_count', p_attempt_count,
                'source_rows', p_source_row_count,
                'output_items', p_output_item_count,
                'response_bytes', p_response_bytes,
                'output_bytes', p_output_bytes,
                'loss_kind', p_loss_kind,
                'minimum_lost_items', p_minimum_lost_items,
                'loss_count_is_exact', p_loss_count_is_exact,
                'minimum_lost_bytes', p_minimum_lost_bytes,
                'next_circuit_state', p_next_circuit_state,
                'next_consecutive_failures', p_next_consecutive_failures),
            'metrics', jsonb_build_object(
                'observed_at', to_jsonb(p_metric_observed_ats),
                'sample_ids', to_jsonb(p_metric_sample_ids),
                'keys', to_jsonb(p_metric_keys),
                'values', to_jsonb(p_metric_values),
                'dimensions', to_jsonb(p_metric_dimensions),
                'sizes', to_jsonb(p_metric_sizes)),
            'databases', jsonb_build_object(
                'observed_at', to_jsonb(p_database_observed_ats),
                'ids', to_jsonb(p_database_ids),
                'names', to_jsonb(p_database_names),
                'states', to_jsonb(p_database_states),
                'recovery_models', to_jsonb(p_database_recovery_models),
                'user_access', to_jsonb(p_database_user_access),
                'is_read_only', to_jsonb(p_database_is_read_only),
                'compatibility_levels', to_jsonb(p_database_compatibility_levels),
                'sizes', to_jsonb(p_database_sizes)),
            'files', jsonb_build_object(
                'observed_at', to_jsonb(p_file_observed_ats),
                'database_ids', to_jsonb(p_file_database_ids),
                'file_ids', to_jsonb(p_file_ids),
                'logical_names', to_jsonb(p_file_logical_names),
                'types', to_jsonb(p_file_types),
                'states', to_jsonb(p_file_states),
                'size_bytes', to_jsonb(p_file_size_bytes),
                'maximum_size_bytes', to_jsonb(p_file_maximum_size_bytes),
                'growth_bytes', to_jsonb(p_file_growth_bytes),
                'growth_percents', to_jsonb(p_file_growth_percents),
                'read_counts', to_jsonb(p_file_read_counts),
                'write_counts', to_jsonb(p_file_write_counts),
                'bytes_read', to_jsonb(p_file_bytes_read),
                'bytes_written', to_jsonb(p_file_bytes_written),
                'io_stall_ms', to_jsonb(p_file_io_stall_ms),
                'sizes', to_jsonb(p_file_sizes)))::text,
        'UTF8'));

    SELECT run.*
    INTO existing_run
    FROM telemetry.collection_run AS run
    WHERE run.run_id = p_run_id;

    IF FOUND THEN
        SELECT outcome.*
        INTO existing_outcome
        FROM telemetry.collection_run_outcome AS outcome
        WHERE outcome.run_id = p_run_id;

        IF FOUND THEN
            IF existing_run.instance_id <> p_instance_id
               OR existing_run.target_revision <> p_target_revision
               OR existing_run.collector_id <> p_collector_id
               OR existing_run.collector_version <> p_collector_version
               OR existing_run.output_schema_version <> p_output_schema_version
               OR existing_run.schedule_revision <> p_schedule_revision
               OR existing_run.scheduled_for <> p_scheduled_at
               OR existing_run.work_key <> p_work_key
               OR existing_run.owner_execution_id <> p_owner_execution_id
               OR existing_run.fencing_token <> p_fencing_token
               OR existing_run.request_digest <> p_request_digest
               OR existing_outcome.completion_digest <> effective_completion_digest THEN
                RAISE EXCEPTION 'collector run replay differs from the committed payload'
                    USING ERRCODE = '22023';
            END IF;

            RETURN QUERY SELECT
                'replayed',
                existing_outcome.inserted_item_count,
                existing_outcome.duplicate_item_count,
                existing_outcome.rejected_item_count,
                existing_outcome.persisted_bytes::integer,
                existing_outcome.completed_at;
            RETURN;
        END IF;
    END IF;

    IF p_outcome NOT IN
       (
            'succeeded', 'partial', 'timed_out', 'transient_failure',
            'permanent_failure', 'permission_denied', 'unsupported',
            'output_invalid'
       )
       OR p_reason_code NOT IN
       (
            'completed', 'source_row_limit', 'response_byte_limit',
            'deadline_exceeded', 'transient_target_failure',
            'permanent_target_failure', 'required_permission_missing',
            'target_unsupported', 'output_validation_failed',
            'capability_profile_missing', 'capability_profile_stale',
            'capability_missing', 'target_version_unsupported',
            'target_platform_unsupported', 'target_edition_unsupported'
       )
       OR p_loss_kind NOT IN
       (
            'none', 'source_row_limit', 'response_byte_limit',
            'output_validation_failure', 'ingestion_rejection'
       )
       OR p_next_circuit_state NOT IN ('closed', 'open', 'half_open')
       OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
       OR p_duration_ms NOT BETWEEN 0 AND 3600000
       OR p_attempt_count NOT BETWEEN 1 AND 2
       OR p_source_row_count NOT BETWEEN 0 AND 100000
       OR p_output_item_count NOT BETWEEN 0 AND 100000
       OR p_response_bytes NOT BETWEEN 0 AND 33554432
       OR p_output_bytes NOT BETWEEN 0 AND 33554432
       OR (p_response_bytes <> 0 AND p_output_bytes > p_response_bytes)
       OR p_minimum_lost_items NOT BETWEEN 0 AND 100000
       OR p_minimum_lost_bytes NOT BETWEEN 0 AND 33554432
       OR ((p_loss_kind = 'none') IS DISTINCT FROM
            (p_minimum_lost_items = 0 AND p_minimum_lost_bytes = 0 AND p_loss_count_is_exact))
       OR (p_loss_kind <> 'none' AND p_minimum_lost_items = 0 AND p_minimum_lost_bytes = 0)
       OR (p_outcome = 'succeeded' AND
            (p_reason_code <> 'completed' OR p_loss_kind <> 'none'))
       OR (p_outcome = 'partial' AND p_loss_kind = 'none')
       OR NOT
       (
           (p_outcome = 'succeeded' AND p_reason_code = 'completed' AND p_loss_kind = 'none')
           OR
           (
               p_outcome = 'partial'
               AND
               (
                   (p_reason_code = 'source_row_limit' AND p_loss_kind = 'source_row_limit')
                   OR
                   (p_reason_code = 'response_byte_limit' AND p_loss_kind = 'response_byte_limit')
               )
           )
           OR (p_outcome = 'timed_out' AND p_reason_code = 'deadline_exceeded' AND p_loss_kind = 'none')
           OR
           (
               p_outcome = 'transient_failure'
               AND p_reason_code = 'transient_target_failure'
               AND p_loss_kind = 'none'
           )
           OR
           (
               p_outcome = 'permanent_failure'
               AND p_reason_code = 'permanent_target_failure'
               AND p_loss_kind = 'none'
           )
           OR
           (
               p_outcome = 'permission_denied'
               AND p_reason_code = 'required_permission_missing'
               AND p_loss_kind = 'none'
           )
           OR
           (
               p_outcome = 'unsupported'
               AND p_reason_code IN
               (
                   'target_unsupported', 'capability_profile_missing',
                   'capability_profile_stale', 'capability_missing',
                   'target_version_unsupported', 'target_platform_unsupported',
                   'target_edition_unsupported'
               )
               AND p_loss_kind = 'none'
           )
           OR
           (
               p_outcome = 'output_invalid'
               AND p_reason_code = 'output_validation_failed'
               AND p_loss_kind = 'output_validation_failure'
           )
       )
       OR
       (
           p_outcome = 'output_invalid'
           AND
           (
               p_reason_code <> 'output_validation_failed'
               OR p_loss_kind <> 'output_validation_failure'
               OR p_minimum_lost_items <> greatest(1, p_output_item_count)
               OR p_loss_count_is_exact <> (p_output_item_count > 0)
               OR p_minimum_lost_bytes <> p_output_bytes
           )
       )
       OR
       (
           p_outcome NOT IN ('succeeded', 'partial', 'output_invalid')
           AND (p_output_item_count <> 0 OR p_output_bytes <> 0)
       ) THEN
        RAISE EXCEPTION 'invalid bounded collector outcome or circuit arguments'
            USING ERRCODE = '22023';
    END IF;

    IF metric_count IS NULL OR database_count IS NULL OR file_count IS NULL
       OR cardinality(p_metric_observed_ats) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_keys) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_values) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_dimensions) IS DISTINCT FROM metric_count
       OR cardinality(p_metric_sizes) IS DISTINCT FROM metric_count
       OR cardinality(p_database_observed_ats) IS DISTINCT FROM database_count
       OR cardinality(p_database_names) IS DISTINCT FROM database_count
       OR cardinality(p_database_states) IS DISTINCT FROM database_count
       OR cardinality(p_database_recovery_models) IS DISTINCT FROM database_count
       OR cardinality(p_database_user_access) IS DISTINCT FROM database_count
       OR cardinality(p_database_is_read_only) IS DISTINCT FROM database_count
       OR cardinality(p_database_compatibility_levels) IS DISTINCT FROM database_count
       OR cardinality(p_database_sizes) IS DISTINCT FROM database_count
       OR cardinality(p_file_observed_ats) IS DISTINCT FROM file_count
       OR cardinality(p_file_database_ids) IS DISTINCT FROM file_count
       OR cardinality(p_file_logical_names) IS DISTINCT FROM file_count
       OR cardinality(p_file_types) IS DISTINCT FROM file_count
       OR cardinality(p_file_states) IS DISTINCT FROM file_count
       OR cardinality(p_file_size_bytes) IS DISTINCT FROM file_count
       OR cardinality(p_file_maximum_size_bytes) IS DISTINCT FROM file_count
       OR cardinality(p_file_growth_bytes) IS DISTINCT FROM file_count
       OR cardinality(p_file_growth_percents) IS DISTINCT FROM file_count
       OR cardinality(p_file_read_counts) IS DISTINCT FROM file_count
       OR cardinality(p_file_write_counts) IS DISTINCT FROM file_count
       OR cardinality(p_file_bytes_read) IS DISTINCT FROM file_count
       OR cardinality(p_file_bytes_written) IS DISTINCT FROM file_count
       OR cardinality(p_file_io_stall_ms) IS DISTINCT FROM file_count
       OR cardinality(p_file_sizes) IS DISTINCT FROM file_count
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_metric_keys, p_metric_sizes) AS metric(metric_key, item_size)
           WHERE metric.item_size IS DISTINCT FROM
               (98 + octet_length(metric.metric_key))
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_database_names, p_database_sizes) AS database(database_name, item_size)
           WHERE database.item_size IS DISTINCT FROM
               (96 + octet_length(database.database_name))
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_file_logical_names, p_file_sizes) AS file(logical_name, item_size)
           WHERE file.item_size IS DISTINCT FROM
               (192 + octet_length(file.logical_name))
       )
       OR
       (
           p_outcome = 'output_invalid'
           AND metric_count + database_count + file_count <> 0
       )
       OR
       (
           p_outcome <> 'output_invalid'
           AND
           (
               metric_count + database_count + file_count <> p_output_item_count
               OR coalesce((SELECT sum(value) FROM unnest(p_metric_sizes) AS size(value)), 0)
                  + coalesce((SELECT sum(value) FROM unnest(p_database_sizes) AS size(value)), 0)
                  + coalesce((SELECT sum(value) FROM unnest(p_file_sizes) AS size(value)), 0)
                  <> p_output_bytes
           )
       ) THEN
        RAISE EXCEPTION 'collector payload arrays and accounting do not match'
            USING ERRCODE = '22023';
    END IF;

    SELECT contract.*
    INTO selected_contract
    FROM control.collector_contract AS contract
    WHERE contract.collector_id = p_collector_id
      AND contract.collector_version = p_collector_version;

    IF NOT FOUND
       OR selected_contract.output_schema_version <> p_output_schema_version
       OR
       (
           p_outcome <> 'output_invalid'
           AND
           (
               p_source_row_count > selected_contract.maximum_rows
               OR p_response_bytes > selected_contract.maximum_response_bytes
               OR p_output_bytes > selected_contract.maximum_response_bytes
           )
       )
       OR p_attempt_count > selected_contract.maximum_attempts
       OR metric_count > selected_contract.maximum_rows
       OR database_count > selected_contract.maximum_rows
       OR file_count > selected_contract.maximum_rows
       OR (p_collector_id = 'engine.core' AND (database_count <> 0 OR file_count <> 0))
       OR (p_collector_id = 'database.inventory' AND (metric_count <> 0 OR file_count <> 0))
       OR (p_collector_id = 'database.files' AND (metric_count <> 0 OR database_count <> 0))
       OR
       (
           p_collector_id = 'engine.core'
           AND p_outcome IN ('succeeded', 'partial')
           AND
           (
               metric_count NOT IN (8, 9, 11, 12)
               OR ARRAY
                  (
                      SELECT metric.metric_key
                      FROM unnest(p_metric_keys) AS metric(metric_key)
                      ORDER BY metric.metric_key
                  ) IS DISTINCT FROM CASE metric_count
                      WHEN 8 THEN ARRAY[
                      'engine.batch_requests_total',
                      'engine.committed_memory_bytes',
                      'engine.page_life_expectancy_seconds',
                      'engine.process_physical_memory_bytes',
                      'engine.sql_compilations_total',
                      'engine.sql_recompilations_total',
                      'engine.target_memory_bytes',
                      'engine.user_connections'
                  ]::text[]
                      WHEN 9 THEN ARRAY[
                      'engine.batch_requests_total',
                      'engine.committed_memory_bytes',
                      'engine.page_life_expectancy_seconds',
                      'engine.process_physical_memory_bytes',
                      'engine.sql_compilations_total',
                      'engine.sql_recompilations_total',
                      'engine.start_time_key',
                      'engine.target_memory_bytes',
                      'engine.user_connections'
                  ]::text[]
                      WHEN 11 THEN ARRAY[
                      'engine.batch_requests_total',
                      'engine.committed_memory_bytes',
                      'engine.os_available_memory_bytes',
                      'engine.page_life_expectancy_seconds',
                      'engine.process_physical_memory_bytes',
                      'engine.scheduler_runnable_tasks',
                      'engine.sql_compilations_total',
                      'engine.sql_recompilations_total',
                      'engine.start_time_key',
                      'engine.target_memory_bytes',
                      'engine.user_connections'
                  ]::text[]
                      WHEN 12 THEN ARRAY[
                      'engine.batch_requests_total',
                      'engine.committed_memory_bytes',
                      'engine.memory_grants_pending',
                      'engine.os_available_memory_bytes',
                      'engine.page_life_expectancy_seconds',
                      'engine.process_physical_memory_bytes',
                      'engine.scheduler_runnable_tasks',
                      'engine.sql_compilations_total',
                      'engine.sql_recompilations_total',
                      'engine.start_time_key',
                      'engine.target_memory_bytes',
                      'engine.user_connections'
                  ]::text[]
                      ELSE NULL::text[]
                  END
           )
       )
       OR
       (
           p_collector_id = 'engine.core'
           AND EXISTS
           (
               SELECT 1
               FROM unnest(p_metric_keys, p_metric_dimensions)
                   AS metric(metric_key, dimensions)
               WHERE metric.metric_key NOT IN
               (
                   'engine.batch_requests_total',
                   'engine.sql_compilations_total',
                   'engine.sql_recompilations_total',
                   'engine.page_life_expectancy_seconds',
                   'engine.user_connections',
                   'engine.process_physical_memory_bytes',
                   'engine.committed_memory_bytes',
                   'engine.target_memory_bytes',
                   'engine.start_time_key',
                   'engine.os_available_memory_bytes',
                   'engine.memory_grants_pending',
                   'engine.scheduler_runnable_tasks'
               )
               OR metric.dimensions <> '{}'::jsonb
           )
       ) THEN
        RAISE EXCEPTION 'collector payload does not match the immutable contract'
            USING ERRCODE = '22023';
    END IF;

    SELECT result.result_status, result.started_at, result.repository_time
    INTO begin_status, begin_started_at, captured_repository_time
    FROM control.begin_collection_run
    (
        p_run_id,
        p_instance_id,
        p_target_revision,
        p_collector_id,
        p_collector_version,
        p_output_schema_version,
        p_schedule_revision,
        p_scheduled_at,
        p_work_key,
        p_owner_execution_id,
        p_fencing_token,
        p_request_digest
    ) AS result;

    IF begin_status = 'replayed' THEN
        SELECT outcome.*
        INTO existing_outcome
        FROM telemetry.collection_run_outcome AS outcome
        WHERE outcome.run_id = p_run_id;
        IF existing_outcome.completion_digest <> effective_completion_digest THEN
            RAISE EXCEPTION 'collector run replay differs from the committed payload'
                USING ERRCODE = '22023';
        END IF;

        RETURN QUERY SELECT
            'replayed',
            existing_outcome.inserted_item_count,
            existing_outcome.duplicate_item_count,
            existing_outcome.rejected_item_count,
            existing_outcome.persisted_bytes::integer,
            existing_outcome.completed_at;
        RETURN;
    ELSIF begin_status NOT IN ('started', 'running_replay') THEN
        RETURN QUERY SELECT begin_status, 0, 0, 0, 0, NULL::timestamptz;
        RETURN;
    END IF;

    captured_repository_time := clock_timestamp();

    -- Target clocks may differ slightly from the repository clock, but observations
    -- must remain within a closed five-minute skew window around the durable run.
    IF EXISTS
       (
           SELECT 1
           FROM unnest(p_metric_observed_ats) AS observation(observed_at)
           WHERE observation.observed_at IS NULL
              OR NOT isfinite(observation.observed_at)
              OR observation.observed_at < begin_started_at - interval '5 minutes'
              OR observation.observed_at > captured_repository_time + interval '5 minutes'
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_database_observed_ats) AS observation(observed_at)
           WHERE observation.observed_at IS NULL
              OR NOT isfinite(observation.observed_at)
              OR observation.observed_at < begin_started_at - interval '5 minutes'
              OR observation.observed_at > captured_repository_time + interval '5 minutes'
       )
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_file_observed_ats) AS observation(observed_at)
           WHERE observation.observed_at IS NULL
              OR NOT isfinite(observation.observed_at)
              OR observation.observed_at < begin_started_at - interval '5 minutes'
              OR observation.observed_at > captured_repository_time + interval '5 minutes'
       ) THEN
        RAISE EXCEPTION 'collector observation timestamp exceeds the allowed run-clock skew window'
            USING ERRCODE = '22023';
    END IF;

    IF metric_count > 0 THEN
        PERFORM control.ensure_daily_metric_partition(partition_day)
        FROM
        (
            SELECT DISTINCT (observed_at AT TIME ZONE 'UTC')::date AS partition_day
            FROM unnest(p_metric_observed_ats) AS metric(observed_at)
        ) AS requested_partitions;

        WITH input AS MATERIALIZED
        (
            SELECT *
            FROM unnest(
                p_metric_observed_ats,
                p_metric_sample_ids,
                p_metric_keys,
                p_metric_values,
                p_metric_dimensions,
                p_metric_sizes
            ) AS metric(observed_at, sample_id, metric_key, metric_value, dimensions, item_size)
        ),
        inserted AS
        (
            INSERT INTO telemetry.raw_metric_sample
            (
                observed_at,
                sample_id,
                instance_id,
                metric_key,
                metric_value,
                dimensions,
                collected_at,
                collection_run_id
            )
            SELECT
                metric.observed_at,
                metric.sample_id,
                p_instance_id,
                metric.metric_key,
                metric.metric_value,
                metric.dimensions,
                captured_repository_time,
                p_run_id
            FROM input AS metric
            ON CONFLICT (observed_at, sample_id) DO NOTHING
            RETURNING observed_at, sample_id
        )
        SELECT count(*)::integer, coalesce(sum(input.item_size), 0)::bigint
        INTO inserted_metrics, inserted_metric_bytes
        FROM inserted
        INNER JOIN input
            ON input.observed_at = inserted.observed_at
           AND input.sample_id = inserted.sample_id;

        IF NOT control.validate_metric_replay
        (
            p_metric_observed_ats,
            p_metric_sample_ids,
            array_fill(p_instance_id, ARRAY[metric_count]),
            p_metric_keys,
            p_metric_values,
            p_metric_dimensions
        ) THEN
            RAISE EXCEPTION 'metric replay identity contains divergent content'
                USING ERRCODE = '22023';
        END IF;

        IF EXISTS
        (
            SELECT 1
            FROM unnest(p_metric_observed_ats, p_metric_sample_ids)
                AS candidate(observed_at, sample_id)
            LEFT JOIN telemetry.raw_metric_sample AS stored
                ON stored.observed_at = candidate.observed_at
               AND stored.sample_id = candidate.sample_id
            WHERE stored.collection_run_id IS DISTINCT FROM p_run_id
        ) THEN
            RAISE EXCEPTION 'metric replay identity belongs to a different ingestion provenance'
                USING ERRCODE = '22023';
        END IF;
    END IF;

    IF database_count > 0 THEN
        INSERT INTO telemetry.database_inventory_snapshot
        (
            observed_at,
            snapshot_id,
            collection_run_id,
            instance_id,
            target_revision,
            database_id,
            database_name,
            state_code,
            is_read_only,
            recovery_model,
            user_access,
            compatibility_level,
            collected_at
        )
        SELECT
            database.observed_at,
            gen_random_uuid(),
            p_run_id,
            p_instance_id,
            p_target_revision,
            database.database_id,
            database.database_name,
            database.state_code,
            database.is_read_only,
            database.recovery_model,
            database.user_access,
            database.compatibility_level,
            captured_repository_time
        FROM unnest(
            p_database_observed_ats,
            p_database_ids,
            p_database_names,
            p_database_states,
            p_database_is_read_only,
            p_database_recovery_models,
            p_database_user_access,
            p_database_compatibility_levels
        ) AS database(
            observed_at,
            database_id,
            database_name,
            state_code,
            is_read_only,
            recovery_model,
            user_access,
            compatibility_level);
        GET DIAGNOSTICS inserted_databases = ROW_COUNT;
        SELECT coalesce(sum(value), 0)::bigint
        INTO inserted_database_bytes
        FROM unnest(p_database_sizes) AS size(value);
    END IF;

    IF file_count > 0 THEN
        INSERT INTO telemetry.database_file_snapshot
        (
            observed_at,
            snapshot_id,
            collection_run_id,
            instance_id,
            target_revision,
            database_id,
            file_id,
            logical_name,
            file_type,
            state_code,
            size_bytes,
            maximum_size_bytes,
            growth_bytes,
            growth_percent,
            read_count,
            write_count,
            bytes_read,
            bytes_written,
            io_stall_ms,
            collected_at
        )
        SELECT
            file.observed_at,
            gen_random_uuid(),
            p_run_id,
            p_instance_id,
            p_target_revision,
            file.database_id,
            file.file_id,
            file.logical_name,
            file.file_type,
            file.state_code,
            file.size_bytes,
            file.maximum_size_bytes,
            file.growth_bytes,
            file.growth_percent,
            file.read_count,
            file.write_count,
            file.bytes_read,
            file.bytes_written,
            file.io_stall_ms,
            captured_repository_time
        FROM unnest(
            p_file_observed_ats,
            p_file_database_ids,
            p_file_ids,
            p_file_logical_names,
            p_file_types,
            p_file_states,
            p_file_size_bytes,
            p_file_maximum_size_bytes,
            p_file_growth_bytes,
            p_file_growth_percents,
            p_file_read_counts,
            p_file_write_counts,
            p_file_bytes_read,
            p_file_bytes_written,
            p_file_io_stall_ms
        ) AS file(
            observed_at,
            database_id,
            file_id,
            logical_name,
            file_type,
            state_code,
            size_bytes,
            maximum_size_bytes,
            growth_bytes,
            growth_percent,
            read_count,
            write_count,
            bytes_read,
            bytes_written,
            io_stall_ms);
        GET DIAGNOSTICS inserted_files = ROW_COUNT;
        SELECT coalesce(sum(value), 0)::bigint
        INTO inserted_file_bytes
        FROM unnest(p_file_sizes) AS size(value);
    END IF;

    total_inserted := inserted_metrics + inserted_databases + inserted_files;
    total_duplicates := metric_count - inserted_metrics;
    total_rejected := p_output_item_count - total_inserted - total_duplicates;
    IF total_rejected < 0 THEN
        RAISE EXCEPTION 'collector output accounting underflowed after persistence'
            USING ERRCODE = '22023';
    END IF;
    total_persisted_bytes := inserted_metric_bytes + inserted_database_bytes + inserted_file_bytes;
    effective_loss_detected := p_loss_kind <> 'none';
    effective_truncated := p_loss_kind IN ('source_row_limit', 'response_byte_limit');

    INSERT INTO telemetry.collection_run_outcome
    (
        run_id,
        outcome,
        reason_code,
        attempt_count,
        retry_count,
        duration_ms,
        source_row_count,
        output_item_count,
        inserted_item_count,
        duplicate_item_count,
        rejected_item_count,
        response_bytes,
        output_bytes,
        persisted_bytes,
        truncated,
        loss_detected,
        loss_kind,
        loss_count_exact,
        lost_row_count,
        lost_byte_count,
        completion_digest,
        completed_at
    )
    VALUES
    (
        p_run_id,
        p_outcome,
        p_reason_code,
        p_attempt_count,
        greatest(p_attempt_count - 1, 0),
        p_duration_ms,
        p_source_row_count,
        p_output_item_count,
        total_inserted,
        total_duplicates,
        total_rejected,
        p_response_bytes,
        p_output_bytes,
        total_persisted_bytes,
        effective_truncated,
        effective_loss_detected,
        p_loss_kind,
        p_loss_count_is_exact,
        p_minimum_lost_items,
        p_minimum_lost_bytes,
        effective_completion_digest,
        captured_repository_time
    );

    IF p_outcome <> 'succeeded' OR effective_loss_detected THEN
        effective_gap_reason := CASE
            WHEN p_loss_kind = 'ingestion_rejection' THEN 'ingestion_rejection'
            WHEN p_loss_kind = 'output_validation_failure' THEN 'output_validation_failed'
            WHEN p_loss_kind = 'source_row_limit' THEN 'source_row_limit'
            WHEN p_loss_kind = 'response_byte_limit' THEN 'response_byte_limit'
            ELSE p_reason_code
        END;
        effective_gap_lost_items := CASE
            WHEN effective_loss_detected THEN p_minimum_lost_items
            ELSE 1
        END;
        effective_gap_lost_bytes := CASE
            WHEN effective_loss_detected THEN p_minimum_lost_bytes
            ELSE 0
        END;
        effective_gap_exact := CASE
            WHEN effective_loss_detected THEN p_loss_count_is_exact
            ELSE false
        END;

        INSERT INTO telemetry.visibility_gap
        (
            gap_id,
            run_id,
            instance_id,
            collector_id,
            reason_code,
            gap_started_at,
            gap_ended_at,
            lost_row_count,
            lost_byte_count,
            count_is_exact,
            recorded_at
        )
        VALUES
        (
            gen_random_uuid(),
            p_run_id,
            p_instance_id,
            p_collector_id,
            effective_gap_reason,
            p_scheduled_at,
            captured_repository_time,
            effective_gap_lost_items,
            effective_gap_lost_bytes,
            effective_gap_exact,
            captured_repository_time
        );
    END IF;

    SELECT schedule.*
    INTO selected_schedule
    FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id
    FOR UPDATE;

    IF selected_schedule.active_run_id <> p_run_id
       OR selected_schedule.schedule_revision <> p_schedule_revision THEN
        RAISE EXCEPTION 'collector schedule changed before commit'
            USING ERRCODE = '55000';
    END IF;

    IF
    (
        p_outcome IN ('succeeded', 'partial')
        AND
        (
            p_next_circuit_state <> 'closed'
            OR p_next_consecutive_failures <> 0
        )
    )
    OR
    (
        p_outcome IN ('transient_failure', 'timed_out')
        AND
        (
            p_next_consecutive_failures <> selected_schedule.consecutive_failure_count + 1
            OR p_next_circuit_state <> CASE
                WHEN selected_schedule.circuit_state = 'half_open'
                     OR selected_schedule.consecutive_failure_count + 1 >=
                        selected_contract.circuit_failure_threshold
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
        RAISE EXCEPTION 'collector circuit transition differs from the authoritative schedule state'
            USING ERRCODE = '22023';
    END IF;

    next_open_until := CASE
        WHEN p_next_circuit_state = 'open'
            THEN captured_repository_time + selected_contract.circuit_open_interval
        ELSE NULL
    END;

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
            WHEN p_outcome = 'succeeded' THEN captured_repository_time
            ELSE schedule.last_succeeded_at
        END,
        last_outcome = p_outcome,
        updated_at = captured_repository_time
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id;

    PERFORM control.assert_worker_lease(
        p_work_key,
        p_owner_execution_id,
        p_fencing_token);

    RETURN QUERY SELECT
        'committed',
        total_inserted,
        total_duplicates,
        total_rejected,
        total_persisted_bytes::integer,
        captured_repository_time;
END
$sqlobserver$;

-- All three M4 assets share one pinned bundle. Keep their existing schedules and
-- run history; a worker with the old bundle must fail reconciliation after this.
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $bundle_upgrade$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('75621c2f975603bd3ea769af5c721523dfa8a15dda2b8933e2e97e470f5cc9c7','hex')
 WHERE collector_id IN ('engine.core','database.inventory','database.files') AND collector_version=1
   AND asset_bundle_sha256=decode('0fb5fc1ccb326611a60a800343791542010ab14cd508f18fff88efafd9bd8b3e','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>3 THEN RAISE EXCEPTION 'Unexpected prior core bundle registry' USING ERRCODE='55000'; END IF;
END $bundle_upgrade$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;

CREATE OR REPLACE FUNCTION reporting.overview_workload_history(
 p_instance_id uuid, p_revision bigint, p_from timestamptz,
 p_to timestamptz, p_cutoff timestamptz)
RETURNS TABLE(metric_key text, bucket_at timestamptz, metric_value double precision, sample_count integer)
LANGUAGE plpgsql SECURITY DEFINER STABLE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $overview$
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'') <> 'Viewer'
 OR coalesce(current_setting('sqlobserver.target_scope',true),'') IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'overview scope rejected' USING ERRCODE='42501'; END IF;
 IF p_instance_id IS NULL OR p_revision IS NULL OR p_revision < 1 OR p_from IS NULL OR p_to IS NULL OR p_cutoff IS NULL
 OR p_to <= p_from OR p_to-p_from > interval '31 days' OR p_to > p_cutoff + interval '1 minute'
 THEN RAISE EXCEPTION 'overview window rejected' USING ERRCODE='22023'; END IF;
 IF NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id AND t.revision=p_revision)
 THEN RAISE EXCEPTION 'overview revision changed' USING ERRCODE='40001'; END IF;
 RETURN QUERY
 WITH runs AS (
  SELECT r.run_id, max(s.observed_at) AS at,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.user_connections') AS connections,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.batch_requests_total') AS batches,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.start_time_key') AS epoch,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.process_physical_memory_bytes') AS sql_memory,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.os_available_memory_bytes') AS os_available_memory,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.memory_grants_pending') AS grants_pending,
   max(s.metric_value) FILTER (WHERE s.metric_key='engine.scheduler_runnable_tasks') AS runnable_tasks
  FROM telemetry.raw_metric_sample s
  JOIN telemetry.collection_run r ON r.run_id=s.collection_run_id AND r.instance_id=s.instance_id
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE s.instance_id=p_instance_id AND r.target_revision=p_revision AND r.collector_id='engine.core'
   AND s.observed_at >= p_from-interval '2 minutes' AND s.observed_at < p_to
   AND s.collected_at <= p_cutoff AND o.completed_at <= p_cutoff AND o.outcome='succeeded'
   AND NOT o.loss_detected AND s.dimensions='{}'::jsonb
   AND s.metric_key IN ('engine.user_connections','engine.batch_requests_total',
    'engine.start_time_key','engine.process_physical_memory_bytes',
    'engine.os_available_memory_bytes','engine.memory_grants_pending','engine.scheduler_runnable_tasks')
  GROUP BY r.run_id
 ), paired AS (
  SELECT *, lag(at) OVER w AS prior_at, lag(batches) OVER w AS prior_batches, lag(epoch) OVER w AS prior_epoch
  FROM runs WINDOW w AS (ORDER BY at,run_id)
 ), measured AS (
  SELECT 'engine.user_connections'::text AS metric, at, connections AS value, 1::double precision AS weight FROM paired WHERE at>=p_from
  UNION ALL
  SELECT 'engine.process_physical_memory_bytes', at, sql_memory, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT 'engine.os_available_memory_bytes', at, os_available_memory, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT 'engine.memory_grants_pending', at, grants_pending, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT 'engine.scheduler_runnable_tasks', at, runnable_tasks, 1::double precision FROM paired WHERE at>=p_from
  UNION ALL
  SELECT 'engine.batch_requests_per_second', at,
   CASE WHEN epoch IS NOT NULL AND epoch=prior_epoch AND at>prior_at AND at-prior_at<=interval '2 minutes'
     AND batches>=prior_batches AND batches<=9007199254740991 AND prior_batches<=9007199254740991
    THEN (batches-prior_batches)/extract(epoch FROM at-prior_at)::double precision ELSE NULL END,
   extract(epoch FROM at-prior_at)::double precision
  FROM paired WHERE at>=p_from
 )
 SELECT v.metric, date_bin(CASE WHEN p_to-p_from>interval '1 day' THEN interval '1 hour' WHEN p_to-p_from>interval '6 hours' THEN interval '15 minutes' ELSE interval '5 minutes' END,
  v.at, timestamptz '2000-01-01 00:00:00Z'),
  (sum(v.value*v.weight)/nullif(sum(v.weight) FILTER (WHERE v.value IS NOT NULL),0))::double precision, count(v.value)::integer
 FROM measured v GROUP BY 1,2 ORDER BY 1,2;
END $overview$;

REVOKE ALL ON FUNCTION reporting.overview_workload_history(uuid,bigint,timestamptz,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reporting.overview_workload_history(uuid,bigint,timestamptz,timestamptz,timestamptz) TO sqlobserver_server;
