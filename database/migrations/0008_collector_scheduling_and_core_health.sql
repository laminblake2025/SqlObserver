-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE TABLE control.collector_contract
(
    collector_id text NOT NULL,
    collector_version integer NOT NULL,
    execution_order integer NOT NULL,
    manifest_schema_version integer NOT NULL,
    output_schema_version integer NOT NULL,
    manifest_sha256 bytea NOT NULL,
    asset_bundle_sha256 bytea NOT NULL,
    default_interval interval NOT NULL,
    minimum_interval interval NOT NULL,
    execution_timeout interval NOT NULL,
    maximum_rows integer NOT NULL,
    maximum_response_bytes integer NOT NULL,
    estimated_cost text NOT NULL,
    maximum_attempts integer NOT NULL,
    circuit_failure_threshold integer NOT NULL,
    circuit_open_interval interval NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT pk_collector_contract PRIMARY KEY (collector_id, collector_version),
    CONSTRAINT uq_collector_contract_execution_order UNIQUE (execution_order),
    CONSTRAINT ck_collector_contract_id CHECK
    (
        octet_length(collector_id) BETWEEN 1 AND 128
        AND collector_id ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_collector_contract_versions CHECK
    (
        execution_order BETWEEN 1 AND 1000
        AND collector_version BETWEEN 1 AND 9999
        AND manifest_schema_version BETWEEN 1 AND 9999
        AND output_schema_version BETWEEN 1 AND 9999
    ),
    CONSTRAINT ck_collector_contract_digests CHECK
    (
        octet_length(manifest_sha256) = 32
        AND octet_length(asset_bundle_sha256) = 32
    ),
    CONSTRAINT ck_collector_contract_intervals CHECK
    (
        minimum_interval BETWEEN interval '1 second' AND interval '1 day'
        AND default_interval BETWEEN minimum_interval AND interval '1 day'
        AND execution_timeout BETWEEN interval '100 milliseconds' AND interval '10 minutes'
        AND execution_timeout < default_interval
    ),
    CONSTRAINT ck_collector_contract_bounds CHECK
    (
        maximum_rows BETWEEN 1 AND 100000
        AND maximum_response_bytes BETWEEN 1 AND 33554432
        AND estimated_cost IN ('low', 'moderate', 'high')
        AND maximum_attempts BETWEEN 1 AND 5
        AND circuit_failure_threshold BETWEEN 1 AND 20
        AND circuit_open_interval BETWEEN interval '5 seconds' AND interval '1 day'
    )
);

COMMENT ON TABLE control.collector_contract IS
    'Immutable bounded collector contract registry. Digests bind reviewed manifests and assets; payload SQL is never stored.';

INSERT INTO control.collector_contract
(
    collector_id,
    collector_version,
    execution_order,
    manifest_schema_version,
    output_schema_version,
    manifest_sha256,
    asset_bundle_sha256,
    default_interval,
    minimum_interval,
    execution_timeout,
    maximum_rows,
    maximum_response_bytes,
    estimated_cost,
    maximum_attempts,
    circuit_failure_threshold,
    circuit_open_interval
)
VALUES
(
    'engine.core',
    1,
    1,
    2,
    1,
    decode('f062ab816cdbb56e7ea9f77ed0042bf00df2bb9b0e6c08b907710f468d8755a1', 'hex'),
    decode('c2bd727d3c2f6278cea09c37acde007244cb681452fe865cfab1aadf7c84accf', 'hex'),
    interval '30 seconds',
    interval '10 seconds',
    interval '5 seconds',
    32,
    65536,
    'low',
    2,
    3,
    interval '5 minutes'
),
(
    'database.inventory',
    1,
    2,
    2,
    1,
    decode('ec1cbfea68854d111d11aab48b476addbf2416e99e639bf97ea58545d78af484', 'hex'),
    decode('c2bd727d3c2f6278cea09c37acde007244cb681452fe865cfab1aadf7c84accf', 'hex'),
    interval '1 minute',
    interval '30 seconds',
    interval '5 seconds',
    1000,
    1048576,
    'moderate',
    2,
    3,
    interval '5 minutes'
),
(
    'database.files',
    1,
    3,
    2,
    1,
    decode('06c9353fe554f737f933c0fa4938fef19f5c6c0dd6af8832c4671160f5af0dcd', 'hex'),
    decode('c2bd727d3c2f6278cea09c37acde007244cb681452fe865cfab1aadf7c84accf', 'hex'),
    interval '1 minute',
    interval '30 seconds',
    interval '5 seconds',
    1000,
    4194304,
    'moderate',
    2,
    3,
    interval '5 minutes'
);

CREATE TABLE control.collector_schedule
(
    instance_id uuid NOT NULL,
    collector_id text NOT NULL,
    collector_version integer NOT NULL,
    target_revision bigint NOT NULL,
    schedule_revision bigint NOT NULL DEFAULT 1,
    enabled boolean NOT NULL DEFAULT true,
    collection_interval interval NOT NULL,
    next_due_at timestamptz NOT NULL,
    active_run_id uuid,
    circuit_state text NOT NULL DEFAULT 'closed',
    consecutive_failure_count integer NOT NULL DEFAULT 0,
    circuit_open_until timestamptz,
    last_started_at timestamptz,
    last_completed_at timestamptz,
    last_succeeded_at timestamptz,
    last_outcome text,
    contention_count bigint NOT NULL DEFAULT 0,
    last_contention_at timestamptz,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT pk_collector_schedule PRIMARY KEY (instance_id, collector_id),
    CONSTRAINT fk_collector_schedule_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT fk_collector_schedule_contract FOREIGN KEY (collector_id, collector_version)
        REFERENCES control.collector_contract (collector_id, collector_version),
    CONSTRAINT ck_collector_schedule_revision CHECK
        (target_revision > 0 AND schedule_revision > 0),
    CONSTRAINT ck_collector_schedule_interval CHECK
        (collection_interval BETWEEN interval '1 second' AND interval '1 day'),
    CONSTRAINT ck_collector_schedule_circuit CHECK
    (
        circuit_state IN ('closed', 'open', 'half_open')
        AND
        (
            (circuit_state = 'open' AND circuit_open_until IS NOT NULL)
            OR (circuit_state <> 'open' AND circuit_open_until IS NULL)
        )
    ),
    CONSTRAINT ck_collector_schedule_failures CHECK
        (consecutive_failure_count BETWEEN 0 AND 1000000),
    CONSTRAINT ck_collector_schedule_times CHECK
    (
        isfinite(next_due_at)
        AND isfinite(created_at)
        AND isfinite(updated_at)
        AND updated_at >= created_at
        AND (circuit_open_until IS NULL OR isfinite(circuit_open_until))
        AND (last_started_at IS NULL OR isfinite(last_started_at))
        AND (last_completed_at IS NULL OR isfinite(last_completed_at))
        AND (last_succeeded_at IS NULL OR isfinite(last_succeeded_at))
        AND (last_contention_at IS NULL OR isfinite(last_contention_at))
        AND (last_completed_at IS NULL OR last_started_at IS NULL OR last_completed_at >= last_started_at)
        AND (last_succeeded_at IS NULL OR last_completed_at IS NULL OR last_succeeded_at <= last_completed_at)
    ),
    CONSTRAINT ck_collector_schedule_last_outcome CHECK
    (
        last_outcome IS NULL OR last_outcome IN
        (
            'succeeded', 'partial', 'timed_out', 'transient_failure',
            'permanent_failure', 'permission_denied', 'unsupported',
            'output_invalid', 'lease_lost', 'circuit_open'
        )
    ),
    CONSTRAINT ck_collector_schedule_contention CHECK (contention_count >= 0)
);

COMMENT ON TABLE control.collector_schedule IS
    'Repository-clock per-target collector schedule, single-open-run pointer, and durable circuit state.';

CREATE INDEX ix_collector_schedule_due
    ON control.collector_schedule (next_due_at, instance_id, collector_id)
    WHERE enabled;

CREATE TABLE telemetry.collection_run
(
    run_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL,
    collector_id text NOT NULL,
    collector_version integer NOT NULL,
    output_schema_version integer NOT NULL,
    target_revision bigint NOT NULL,
    schedule_revision bigint NOT NULL,
    work_key text NOT NULL,
    owner_execution_id uuid NOT NULL,
    fencing_token bigint NOT NULL,
    request_digest bytea NOT NULL,
    scheduled_for timestamptz NOT NULL,
    started_at timestamptz NOT NULL,
    CONSTRAINT fk_collection_run_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT fk_collection_run_contract FOREIGN KEY (collector_id, collector_version)
        REFERENCES control.collector_contract (collector_id, collector_version),
    CONSTRAINT ck_collection_run_versions CHECK
    (
        collector_version BETWEEN 1 AND 9999
        AND output_schema_version BETWEEN 1 AND 9999
        AND target_revision > 0
        AND schedule_revision > 0
        AND fencing_token > 0
    ),
    CONSTRAINT ck_collection_run_work_key CHECK
    (
        octet_length(work_key) BETWEEN 1 AND 256
        AND work_key = btrim(work_key)
    ),
    CONSTRAINT ck_collection_run_digest CHECK (octet_length(request_digest) = 32),
    CONSTRAINT ck_collection_run_times CHECK
    (
        isfinite(scheduled_for)
        AND isfinite(started_at)
        AND started_at >= scheduled_for - interval '1 day'
    )
);

COMMENT ON TABLE telemetry.collection_run IS
    'Append-only repository-clock collection start, bound to target/schedule revisions and a fenced worker execution.';

CREATE INDEX ix_collection_run_target_time
    ON telemetry.collection_run (instance_id, collector_id, started_at DESC, run_id DESC);

CREATE TABLE telemetry.collection_run_outcome
(
    run_id uuid PRIMARY KEY,
    outcome text NOT NULL,
    reason_code text NOT NULL,
    attempt_count integer NOT NULL,
    retry_count integer NOT NULL,
    duration_ms bigint NOT NULL,
    source_row_count integer NOT NULL,
    output_item_count integer NOT NULL,
    inserted_item_count integer NOT NULL,
    duplicate_item_count integer NOT NULL,
    rejected_item_count integer NOT NULL,
    response_bytes bigint NOT NULL,
    output_bytes bigint NOT NULL,
    persisted_bytes bigint NOT NULL,
    truncated boolean NOT NULL,
    loss_detected boolean NOT NULL,
    loss_kind text NOT NULL,
    loss_count_exact boolean NOT NULL,
    lost_row_count bigint NOT NULL,
    lost_byte_count bigint NOT NULL,
    completion_digest bytea NOT NULL,
    completed_at timestamptz NOT NULL,
    CONSTRAINT fk_collection_outcome_run FOREIGN KEY (run_id)
        REFERENCES telemetry.collection_run (run_id),
    CONSTRAINT ck_collection_outcome_value CHECK
    (
        outcome IN
        (
            'succeeded', 'partial', 'timed_out', 'transient_failure',
            'permanent_failure', 'permission_denied', 'unsupported',
            'output_invalid', 'lease_lost', 'circuit_open'
        )
    ),
    CONSTRAINT ck_collection_outcome_reason CHECK
    (
        reason_code IN
        (
            'completed', 'source_row_limit', 'response_byte_limit',
            'deadline_exceeded', 'transient_target_failure',
            'permanent_target_failure', 'required_permission_missing',
            'target_unsupported', 'output_validation_failed',
            'lease_ownership_lost', 'circuit_currently_open',
            'capability_profile_missing', 'capability_profile_stale',
            'capability_missing', 'target_version_unsupported',
            'target_platform_unsupported', 'target_edition_unsupported',
            'target_revision_changed'
        )
    ),
    CONSTRAINT ck_collection_outcome_reason_matrix CHECK
    (
        (outcome = 'succeeded' AND reason_code = 'completed' AND loss_kind = 'none')
        OR
        (
            outcome = 'partial'
            AND
            (
                (reason_code = 'source_row_limit' AND loss_kind = 'source_row_limit')
                OR
                (reason_code = 'response_byte_limit' AND loss_kind = 'response_byte_limit')
            )
        )
        OR (outcome = 'timed_out' AND reason_code = 'deadline_exceeded' AND loss_kind = 'none')
        OR
        (
            outcome = 'transient_failure'
            AND reason_code = 'transient_target_failure'
            AND loss_kind = 'none'
        )
        OR
        (
            outcome = 'permanent_failure'
            AND reason_code IN ('permanent_target_failure', 'target_revision_changed')
            AND loss_kind = 'none'
        )
        OR
        (
            outcome = 'permission_denied'
            AND reason_code = 'required_permission_missing'
            AND loss_kind = 'none'
        )
        OR
        (
            outcome = 'unsupported'
            AND reason_code IN
            (
                'target_unsupported', 'capability_profile_missing',
                'capability_profile_stale', 'capability_missing',
                'target_version_unsupported', 'target_platform_unsupported',
                'target_edition_unsupported'
            )
            AND loss_kind = 'none'
        )
        OR
        (
            outcome = 'output_invalid'
            AND reason_code = 'output_validation_failed'
            AND loss_kind = 'output_validation_failure'
        )
        OR
        (
            outcome = 'lease_lost'
            AND reason_code = 'lease_ownership_lost'
            AND loss_kind = 'none'
        )
        OR
        (
            outcome = 'circuit_open'
            AND reason_code = 'circuit_currently_open'
            AND loss_kind = 'none'
        )
    ),
    CONSTRAINT ck_collection_outcome_attempts CHECK
    (
        attempt_count BETWEEN 0 AND 2
        AND retry_count BETWEEN 0 AND 1
        AND
        (
            (attempt_count = 0 AND retry_count = 0)
            OR attempt_count = retry_count + 1
        )
    ),
    CONSTRAINT ck_collection_outcome_duration CHECK (duration_ms BETWEEN 0 AND 3600000),
    CONSTRAINT ck_collection_outcome_row_accounting CHECK
    (
        source_row_count BETWEEN 0 AND 100000
        AND output_item_count BETWEEN 0 AND 100000
        AND inserted_item_count BETWEEN 0 AND output_item_count
        AND duplicate_item_count BETWEEN 0 AND output_item_count
        AND rejected_item_count BETWEEN 0 AND output_item_count
        AND output_item_count = inserted_item_count + duplicate_item_count + rejected_item_count
    ),
    CONSTRAINT ck_collection_outcome_byte_accounting CHECK
    (
        response_bytes BETWEEN 0 AND 33554432
        AND output_bytes BETWEEN 0 AND 33554432
        AND persisted_bytes BETWEEN 0 AND 33554432
        AND (response_bytes = 0 OR persisted_bytes <= response_bytes)
        AND (response_bytes = 0 OR output_bytes <= response_bytes)
    ),
    CONSTRAINT ck_collection_outcome_loss CHECK
    (
        loss_kind IN
        (
            'none', 'source_row_limit', 'response_byte_limit',
            'output_validation_failure', 'ingestion_rejection'
        )
        AND ((loss_kind = 'none') = (NOT loss_detected))
        AND
        (loss_detected OR (NOT truncated AND rejected_item_count = 0))
        AND lost_row_count >= 0
        AND lost_byte_count >= 0
        AND
        (
            (loss_detected AND (lost_row_count > 0 OR lost_byte_count > 0))
            OR
            (
                NOT loss_detected
                AND lost_row_count = 0
                AND lost_byte_count = 0
                AND loss_count_exact
            )
        )
        AND
        (
            outcome <> 'succeeded'
            OR
            (
                reason_code = 'completed'
                AND NOT truncated
                AND NOT loss_detected
                AND rejected_item_count = 0
            )
        )
    ),
    CONSTRAINT ck_collection_outcome_digest CHECK (octet_length(completion_digest) = 32),
    CONSTRAINT ck_collection_outcome_completed_at CHECK (isfinite(completed_at))
);

COMMENT ON TABLE telemetry.collection_run_outcome IS
    'Append-only terminal run accounting. Successful runs cannot carry hidden truncation, rejection, or loss.';

CREATE INDEX ix_collection_outcome_completed
    ON telemetry.collection_run_outcome (completed_at DESC, run_id DESC);

CREATE TABLE telemetry.visibility_gap
(
    gap_id uuid PRIMARY KEY,
    run_id uuid NOT NULL UNIQUE,
    instance_id uuid NOT NULL,
    collector_id text NOT NULL,
    reason_code text NOT NULL,
    gap_started_at timestamptz NOT NULL,
    gap_ended_at timestamptz NOT NULL,
    lost_row_count bigint NOT NULL,
    lost_byte_count bigint NOT NULL,
    count_is_exact boolean NOT NULL,
    recorded_at timestamptz NOT NULL,
    CONSTRAINT fk_visibility_gap_run FOREIGN KEY (run_id)
        REFERENCES telemetry.collection_run (run_id),
    CONSTRAINT fk_visibility_gap_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT ck_visibility_gap_collector CHECK
    (
        octet_length(collector_id) BETWEEN 1 AND 128
        AND collector_id ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_visibility_gap_reason CHECK
    (
        reason_code IN
        (
            'source_row_limit', 'response_byte_limit', 'deadline_exceeded',
            'transient_target_failure', 'permanent_target_failure',
            'required_permission_missing', 'target_unsupported',
            'output_validation_failed', 'lease_ownership_lost',
            'circuit_currently_open', 'capability_profile_missing',
            'capability_profile_stale', 'capability_missing',
            'target_version_unsupported', 'target_platform_unsupported',
            'target_edition_unsupported', 'target_revision_changed',
            'ingestion_rejection'
        )
    ),
    CONSTRAINT ck_visibility_gap_times CHECK
    (
        isfinite(gap_started_at)
        AND isfinite(gap_ended_at)
        AND isfinite(recorded_at)
        AND gap_ended_at >= gap_started_at
        AND recorded_at >= gap_started_at
    ),
    CONSTRAINT ck_visibility_gap_counts CHECK
    (
        lost_row_count >= 0
        AND lost_byte_count >= 0
        AND (lost_row_count > 0 OR lost_byte_count > 0)
    )
);

COMMENT ON TABLE telemetry.visibility_gap IS
    'Append-only, user-visible collection loss interval with closed reason codes and explicit exact/unknown counts.';

CREATE INDEX ix_visibility_gap_target_time
    ON telemetry.visibility_gap (instance_id, collector_id, gap_started_at DESC, gap_id DESC);

ALTER TABLE telemetry.raw_metric_sample
    ADD COLUMN collection_run_id uuid;

ALTER TABLE telemetry.raw_metric_sample
    ADD CONSTRAINT ck_raw_metric_sample_collection_run_id CHECK
        (collection_run_id IS NULL OR collection_run_id <> '00000000-0000-0000-0000-000000000000'::uuid);

ALTER TABLE telemetry.raw_metric_sample
    ADD CONSTRAINT fk_raw_metric_sample_collection_run FOREIGN KEY (collection_run_id)
        REFERENCES telemetry.collection_run (run_id);

COMMENT ON COLUMN telemetry.raw_metric_sample.collection_run_id IS
    'M4 collection-run provenance. NULL is retained only for pre-M4 and generic M2 ingestion compatibility.';

CREATE TABLE telemetry.database_inventory_snapshot
(
    observed_at timestamptz NOT NULL,
    snapshot_id uuid NOT NULL,
    collection_run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL,
    database_id integer NOT NULL,
    database_name text NOT NULL,
    state_code text NOT NULL,
    is_read_only boolean NOT NULL,
    recovery_model text NOT NULL,
    user_access text NOT NULL,
    compatibility_level integer NOT NULL,
    collected_at timestamptz NOT NULL,
    CONSTRAINT pk_database_inventory_snapshot PRIMARY KEY (observed_at, snapshot_id),
    CONSTRAINT uq_database_inventory_run_database UNIQUE (collection_run_id, database_id),
    CONSTRAINT fk_database_inventory_run FOREIGN KEY (collection_run_id)
        REFERENCES telemetry.collection_run (run_id),
    CONSTRAINT fk_database_inventory_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT ck_database_inventory_id CHECK (database_id BETWEEN 1 AND 32767),
    CONSTRAINT ck_database_inventory_revision CHECK (target_revision > 0),
    CONSTRAINT ck_database_inventory_name CHECK
    (
        octet_length(database_name) BETWEEN 1 AND 1024
    ),
    CONSTRAINT ck_database_inventory_state CHECK
    (
        state_code IN
        (
            'online', 'restoring', 'recovering', 'recovery_pending', 'suspect',
            'emergency', 'offline', 'copying', 'offline_secondary', 'other'
        )
    ),
    CONSTRAINT ck_database_inventory_recovery CHECK
        (recovery_model IN ('full', 'bulk_logged', 'simple', 'other')),
    CONSTRAINT ck_database_inventory_user_access CHECK
        (user_access IN ('multi_user', 'restricted_user', 'single_user', 'other')),
    CONSTRAINT ck_database_inventory_compatibility CHECK
        (compatibility_level BETWEEN 0 AND 999),
    CONSTRAINT ck_database_inventory_times CHECK
    (
        isfinite(observed_at)
        AND isfinite(collected_at)
        AND collected_at >= observed_at - interval '1 day'
    )
);

COMMENT ON TABLE telemetry.database_inventory_snapshot IS
    'Bounded database identity/state snapshots. Names are untrusted display data and never telemetry labels.';

CREATE INDEX ix_database_inventory_latest
    ON telemetry.database_inventory_snapshot
        (instance_id, database_id, observed_at DESC, snapshot_id DESC);

CREATE TABLE telemetry.database_file_snapshot
(
    observed_at timestamptz NOT NULL,
    snapshot_id uuid NOT NULL,
    collection_run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL,
    database_id integer NOT NULL,
    file_id integer NOT NULL,
    logical_name text NOT NULL,
    file_type text NOT NULL,
    state_code text NOT NULL,
    size_bytes bigint NOT NULL,
    maximum_size_bytes bigint,
    growth_bytes bigint NOT NULL,
    growth_percent integer NOT NULL,
    read_count bigint NOT NULL,
    write_count bigint NOT NULL,
    bytes_read bigint NOT NULL,
    bytes_written bigint NOT NULL,
    io_stall_ms bigint NOT NULL,
    collected_at timestamptz NOT NULL,
    CONSTRAINT pk_database_file_snapshot PRIMARY KEY (observed_at, snapshot_id),
    CONSTRAINT uq_database_file_run_identity UNIQUE (collection_run_id, database_id, file_id),
    CONSTRAINT fk_database_file_run FOREIGN KEY (collection_run_id)
        REFERENCES telemetry.collection_run (run_id),
    CONSTRAINT fk_database_file_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT ck_database_file_identity CHECK
        (target_revision > 0 AND database_id BETWEEN 1 AND 32767 AND file_id BETWEEN 1 AND 65535),
    CONSTRAINT ck_database_file_name CHECK
    (
        octet_length(logical_name) BETWEEN 1 AND 1024
    ),
    CONSTRAINT ck_database_file_type CHECK
        (file_type IN ('rows', 'log', 'filestream', 'fulltext', 'other')),
    CONSTRAINT ck_database_file_state CHECK
        (state_code IN ('online', 'restoring', 'recovering', 'recovery_pending', 'suspect', 'emergency', 'offline', 'defunct', 'other')),
    CONSTRAINT ck_database_file_sizes CHECK
    (
        size_bytes >= 0
        AND (maximum_size_bytes IS NULL OR maximum_size_bytes >= size_bytes)
        AND growth_bytes >= 0
        AND growth_percent BETWEEN 0 AND 100
    ),
    CONSTRAINT ck_database_file_counters CHECK
    (
        read_count >= 0
        AND write_count >= 0
        AND bytes_read >= 0
        AND bytes_written >= 0
        AND io_stall_ms >= 0
    ),
    CONSTRAINT ck_database_file_times CHECK
    (
        isfinite(observed_at)
        AND isfinite(collected_at)
        AND collected_at >= observed_at - interval '1 day'
    )
);

COMMENT ON TABLE telemetry.database_file_snapshot IS
    'Bounded database-file capacity and I/O counters. Physical paths are intentionally absent.';

CREATE INDEX ix_database_file_latest
    ON telemetry.database_file_snapshot
        (instance_id, database_id, file_id, observed_at DESC, snapshot_id DESC);

ALTER TABLE control.collector_schedule
    ADD CONSTRAINT fk_collector_schedule_active_run FOREIGN KEY (active_run_id)
        REFERENCES telemetry.collection_run (run_id);

CREATE FUNCTION control.ensure_target_collector_schedules()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    captured_repository_time timestamptz;
    selected_schedule control.collector_schedule%ROWTYPE;
    invalidated_run telemetry.collection_run%ROWTYPE;
    invalidated_duration_ms bigint;
    invalidates_evidence boolean;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.revision = NEW.revision
           AND OLD.lifecycle_state = NEW.lifecycle_state
           AND OLD.host_name IS NOT DISTINCT FROM NEW.host_name
           AND OLD.instance_name IS NOT DISTINCT FROM NEW.instance_name
           AND OLD.tcp_port IS NOT DISTINCT FROM NEW.tcp_port
           AND OLD.certificate_host_name IS NOT DISTINCT FROM NEW.certificate_host_name
           AND OLD.connect_timeout = NEW.connect_timeout
           AND OLD.authentication_mode = NEW.authentication_mode
           AND OLD.transport_security_mode = NEW.transport_security_mode THEN
            RETURN NEW;
        END IF;

        invalidates_evidence :=
            OLD.revision <> NEW.revision
            OR OLD.host_name IS DISTINCT FROM NEW.host_name
            OR OLD.instance_name IS DISTINCT FROM NEW.instance_name
            OR OLD.tcp_port IS DISTINCT FROM NEW.tcp_port
            OR OLD.certificate_host_name IS DISTINCT FROM NEW.certificate_host_name
            OR OLD.connect_timeout <> NEW.connect_timeout
            OR OLD.authentication_mode <> NEW.authentication_mode
            OR OLD.transport_security_mode <> NEW.transport_security_mode
            OR (OLD.lifecycle_state = 'active' AND NEW.lifecycle_state <> 'active');
    ELSE
        invalidates_evidence := false;
    END IF;

    captured_repository_time := clock_timestamp();

    IF invalidates_evidence THEN
        FOR selected_schedule IN
            SELECT schedule.*
            FROM control.collector_schedule AS schedule
            WHERE schedule.instance_id = NEW.instance_id
            FOR UPDATE
        LOOP
            IF selected_schedule.active_run_id IS NOT NULL THEN
                SELECT run.*
                INTO invalidated_run
                FROM telemetry.collection_run AS run
                WHERE run.run_id = selected_schedule.active_run_id;

                IF FOUND AND NOT EXISTS
                (
                    SELECT 1
                    FROM telemetry.collection_run_outcome AS outcome
                    WHERE outcome.run_id = invalidated_run.run_id
                ) THEN
                    invalidated_duration_ms := least(
                        3600000,
                        greatest(
                            0,
                            floor(extract(epoch FROM
                                (captured_repository_time - invalidated_run.started_at)) * 1000)::bigint));
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
                        invalidated_run.run_id, 'permanent_failure', 'target_revision_changed',
                        1, 0, invalidated_duration_ms, 0, 0, 0, 0, 0, 0, 0, 0,
                        false, false, 'none', true, 0, 0,
                        invalidated_run.request_digest, captured_repository_time
                    );

                    INSERT INTO telemetry.visibility_gap
                    (
                        gap_id, run_id, instance_id, collector_id, reason_code,
                        gap_started_at, gap_ended_at, lost_row_count, lost_byte_count,
                        count_is_exact, recorded_at
                    )
                    VALUES
                    (
                        gen_random_uuid(), invalidated_run.run_id, NEW.instance_id,
                        selected_schedule.collector_id, 'target_revision_changed',
                        invalidated_run.scheduled_for, captured_repository_time,
                        1, 0, false, captured_repository_time
                    );
                END IF;
            END IF;

            UPDATE control.collector_schedule AS schedule
            SET
                target_revision = NEW.revision,
                schedule_revision = schedule.schedule_revision + 1,
                enabled = NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL,
                next_due_at = captured_repository_time,
                active_run_id = NULL,
                circuit_state = 'closed',
                consecutive_failure_count = 0,
                circuit_open_until = NULL,
                last_started_at = NULL,
                last_completed_at = NULL,
                last_succeeded_at = NULL,
                last_outcome = NULL,
                contention_count = 0,
                last_contention_at = NULL,
                updated_at = captured_repository_time
            WHERE schedule.instance_id = selected_schedule.instance_id
              AND schedule.collector_id = selected_schedule.collector_id;
        END LOOP;
    ELSIF TG_OP = 'UPDATE' THEN
        UPDATE control.collector_schedule AS schedule
        SET
            enabled = NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL,
            next_due_at = CASE
                WHEN NOT schedule.enabled
                     AND NEW.lifecycle_state = 'active'
                     AND NEW.host_name IS NOT NULL
                    THEN captured_repository_time
                ELSE schedule.next_due_at
            END,
            updated_at = captured_repository_time
        WHERE schedule.instance_id = NEW.instance_id
          AND schedule.target_revision = NEW.revision
          AND schedule.enabled IS DISTINCT FROM
              (NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL);
    END IF;

    IF NEW.lifecycle_state = 'active' AND NEW.host_name IS NOT NULL THEN
        INSERT INTO control.collector_schedule
        (
            instance_id, collector_id, collector_version, target_revision, schedule_revision,
            enabled, collection_interval, next_due_at, circuit_state,
            consecutive_failure_count, created_at, updated_at
        )
        SELECT
            NEW.instance_id, contract.collector_id, contract.collector_version, NEW.revision, 1,
            true, contract.default_interval, captured_repository_time, 'closed',
            0, captured_repository_time, captured_repository_time
        FROM control.collector_contract AS contract
        ON CONFLICT (instance_id, collector_id) DO NOTHING;
    END IF;

    RETURN NEW;
END
$sqlobserver$;

CREATE TRIGGER observation_target_collector_schedules
AFTER INSERT OR UPDATE OF
    revision,
    lifecycle_state,
    host_name,
    instance_name,
    tcp_port,
    certificate_host_name,
    connect_timeout,
    authentication_mode,
    transport_security_mode
ON control.observation_target
FOR EACH ROW EXECUTE FUNCTION control.ensure_target_collector_schedules();

-- Backfill targets that were already eligible before this trigger existed. The
-- repository clock is statement-stable and conflict handling makes re-entry safe.
INSERT INTO control.collector_schedule
(
    instance_id,
    collector_id,
    collector_version,
    target_revision,
    schedule_revision,
    enabled,
    collection_interval,
    next_due_at,
    circuit_state,
    consecutive_failure_count,
    created_at,
    updated_at
)
SELECT
    target.instance_id,
    contract.collector_id,
    contract.collector_version,
    target.revision,
    1,
    true,
    contract.default_interval,
    statement_timestamp(),
    'closed',
    0,
    statement_timestamp(),
    statement_timestamp()
FROM control.observation_target AS target
CROSS JOIN control.collector_contract AS contract
WHERE target.lifecycle_state = 'active'
  AND target.host_name IS NOT NULL
ON CONFLICT (instance_id, collector_id) DO NOTHING;

ALTER TABLE control.collector_schedule ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.collector_schedule FORCE ROW LEVEL SECURITY;
CREATE POLICY collector_schedule_owner_only
    ON control.collector_schedule
    FOR ALL
    TO sqlobserver_migrator
    USING (true)
    WITH CHECK (true);

ALTER TABLE telemetry.collection_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.collection_run FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_owner_only
    ON telemetry.collection_run
    FOR ALL
    TO sqlobserver_migrator
    USING (true)
    WITH CHECK (true);

ALTER TABLE telemetry.collection_run_outcome ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.collection_run_outcome FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_outcome_owner_only
    ON telemetry.collection_run_outcome
    FOR ALL
    TO sqlobserver_migrator
    USING (true)
    WITH CHECK (true);

ALTER TABLE telemetry.visibility_gap ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.visibility_gap FORCE ROW LEVEL SECURITY;
CREATE POLICY visibility_gap_owner_only
    ON telemetry.visibility_gap
    FOR ALL
    TO sqlobserver_migrator
    USING (true)
    WITH CHECK (true);

ALTER TABLE telemetry.database_inventory_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.database_inventory_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY database_inventory_snapshot_owner_only
    ON telemetry.database_inventory_snapshot
    FOR ALL
    TO sqlobserver_migrator
    USING (true)
    WITH CHECK (true);

ALTER TABLE telemetry.database_file_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.database_file_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY database_file_snapshot_owner_only
    ON telemetry.database_file_snapshot
    FOR ALL
    TO sqlobserver_migrator
    USING (true)
    WITH CHECK (true);

CREATE FUNCTION control.reject_collector_history_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
AS $sqlobserver$
BEGIN
    RAISE EXCEPTION 'collector contract and collection history are append-only'
        USING ERRCODE = '55000';
END
$sqlobserver$;

COMMENT ON FUNCTION control.ensure_target_collector_schedules() IS
    'Creates the fixed M4 schedules when a configured target first becomes active; existing durable schedule state is never overwritten.';

CREATE TRIGGER collector_contract_append_only
BEFORE UPDATE OR DELETE ON control.collector_contract
FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE TRIGGER collection_run_append_only
BEFORE UPDATE OR DELETE ON telemetry.collection_run
FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE TRIGGER collection_run_outcome_append_only
BEFORE UPDATE OR DELETE ON telemetry.collection_run_outcome
FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE TRIGGER visibility_gap_append_only
BEFORE UPDATE OR DELETE ON telemetry.visibility_gap
FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION control.reconcile_collector_catalog
(
    p_collector_ids text[],
    p_collector_versions integer[],
    p_manifest_sha256 bytea[],
    p_asset_bundle_sha256 bytea[],
    p_execution_orders integer[],
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint
)
RETURNS TABLE
(
    inserted_count integer,
    updated_count integer,
    unchanged_count integer,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    captured_repository_time timestamptz;
    catalog_count integer;
    matched_count integer;
BEGIN
    catalog_count := cardinality(p_collector_ids);
    IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile'
       OR p_owner_execution_id IS NULL
       OR p_owner_execution_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR catalog_count <> 3
       OR cardinality(p_collector_versions) IS DISTINCT FROM catalog_count
       OR cardinality(p_manifest_sha256) IS DISTINCT FROM catalog_count
       OR cardinality(p_asset_bundle_sha256) IS DISTINCT FROM catalog_count
       OR cardinality(p_execution_orders) IS DISTINCT FROM catalog_count
       OR p_collector_ids IS DISTINCT FROM ARRAY[
            'engine.core', 'database.inventory', 'database.files']::text[]
       OR p_collector_versions IS DISTINCT FROM ARRAY[1, 1, 1]::integer[]
       OR p_execution_orders IS DISTINCT FROM ARRAY[1, 2, 3]::integer[]
       OR EXISTS
       (
           SELECT 1
           FROM unnest(p_manifest_sha256, p_asset_bundle_sha256)
               AS digest(manifest_digest, bundle_digest)
           WHERE octet_length(digest.manifest_digest) <> 32
              OR octet_length(digest.bundle_digest) <> 32
       ) THEN
        RAISE EXCEPTION 'collector catalog must contain the exact ordered M4 contracts and bounded digests'
            USING ERRCODE = '22023';
    END IF;

    PERFORM control.assert_worker_lease(
        p_work_key,
        p_owner_execution_id,
        p_fencing_token);

    SELECT count(*)::integer
    INTO matched_count
    FROM unnest(
        p_collector_ids,
        p_collector_versions,
        p_manifest_sha256,
        p_asset_bundle_sha256,
        p_execution_orders
    ) AS supplied(collector_id, collector_version, manifest_digest, bundle_digest, execution_order)
    INNER JOIN control.collector_contract AS contract
        ON contract.collector_id = supplied.collector_id
       AND contract.collector_version = supplied.collector_version
       AND contract.manifest_sha256 = supplied.manifest_digest
       AND contract.asset_bundle_sha256 = supplied.bundle_digest
       AND contract.execution_order = supplied.execution_order;

    IF matched_count <> catalog_count THEN
        RAISE EXCEPTION 'collector catalog differs from the immutable repository contract registry'
            USING ERRCODE = '55000';
    END IF;

    captured_repository_time := clock_timestamp();

    INSERT INTO control.collector_schedule
    (
        instance_id,
        collector_id,
        collector_version,
        target_revision,
        schedule_revision,
        enabled,
        collection_interval,
        next_due_at,
        circuit_state,
        consecutive_failure_count,
        created_at,
        updated_at
    )
    SELECT
        target.instance_id,
        contract.collector_id,
        contract.collector_version,
        target.revision,
        1,
        true,
        contract.default_interval,
        captured_repository_time,
        'closed',
        0,
        captured_repository_time,
        captured_repository_time
    FROM control.observation_target AS target
    CROSS JOIN control.collector_contract AS contract
    WHERE target.host_name IS NOT NULL
      AND target.lifecycle_state = 'active'
      AND contract.collector_id = ANY(p_collector_ids)
    ON CONFLICT (instance_id, collector_id) DO NOTHING;

    PERFORM control.assert_worker_lease(
        p_work_key,
        p_owner_execution_id,
        p_fencing_token);

    RETURN QUERY SELECT 0, 0, catalog_count, captured_repository_time;
END
$sqlobserver$;

CREATE FUNCTION control.list_due_collector_work(p_max_items integer)
RETURNS TABLE
(
    target_instance_id uuid,
    target_revision bigint,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    collector_id text,
    collector_version integer,
    output_schema_version integer,
    schedule_revision bigint,
    scheduled_at timestamptz,
    circuit_state text,
    consecutive_failure_count integer,
    circuit_open_until timestamptz,
    has_more boolean,
    repository_time timestamptz
)
LANGUAGE sql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    WITH repository_clock AS MATERIALIZED
    (
        SELECT clock_timestamp() AS value
    ),
    due AS MATERIALIZED
    (
        SELECT
            target.instance_id,
            target.revision,
            target.host_name,
            target.instance_name,
            target.tcp_port,
            target.certificate_host_name,
            target.connect_timeout,
            target.authentication_mode,
            target.transport_security_mode,
            schedule.collector_id,
            schedule.collector_version,
            contract.output_schema_version,
            schedule.schedule_revision,
            schedule.next_due_at,
            CASE
                WHEN schedule.circuit_state = 'open'
                    AND schedule.circuit_open_until <= repository_clock.value
                    THEN 'half_open'
                ELSE schedule.circuit_state
            END AS effective_circuit_state,
            schedule.consecutive_failure_count,
            CASE
                WHEN schedule.circuit_state = 'open'
                    AND schedule.circuit_open_until <= repository_clock.value
                    THEN NULL::timestamptz
                ELSE schedule.circuit_open_until
            END AS effective_circuit_open_until,
            contract.execution_order,
            repository_clock.value AS repository_time
        FROM control.collector_schedule AS schedule
        INNER JOIN control.collector_contract AS contract
            ON contract.collector_id = schedule.collector_id
           AND contract.collector_version = schedule.collector_version
        INNER JOIN control.observation_target AS target
            ON target.instance_id = schedule.instance_id
        CROSS JOIN repository_clock
        LEFT JOIN telemetry.collection_run AS active_run
            ON active_run.run_id = schedule.active_run_id
        LEFT JOIN control.worker_lease AS active_lease
            ON active_lease.work_key = active_run.work_key
           AND active_lease.owner_execution_id = active_run.owner_execution_id
           AND active_lease.fencing_token = active_run.fencing_token
           AND active_lease.released_at IS NULL
           AND active_lease.expires_at > repository_clock.value
        WHERE p_max_items BETWEEN 1 AND 16
          AND target.host_name IS NOT NULL
          AND target.lifecycle_state = 'active'
          AND schedule.target_revision = target.revision
          AND schedule.enabled
          AND schedule.next_due_at <= repository_clock.value
          AND (schedule.circuit_state <> 'open' OR schedule.circuit_open_until <= repository_clock.value)
          AND (schedule.active_run_id IS NULL OR active_lease.work_key IS NULL)
          AND
          (
              schedule.collector_id = 'engine.core'
              OR
              (
                  schedule.collector_id = 'database.inventory'
                  AND EXISTS
                  (
                      SELECT 1
                      FROM control.collector_schedule AS engine_schedule
                      WHERE engine_schedule.instance_id = schedule.instance_id
                        AND engine_schedule.collector_id = 'engine.core'
                        AND engine_schedule.enabled
                        AND engine_schedule.last_outcome IN ('succeeded', 'partial')
                  )
              )
              OR
              (
                  schedule.collector_id = 'database.files'
                  AND EXISTS
                  (
                      SELECT 1
                      FROM control.collector_schedule AS engine_schedule
                      WHERE engine_schedule.instance_id = schedule.instance_id
                        AND engine_schedule.collector_id = 'engine.core'
                        AND engine_schedule.enabled
                        AND engine_schedule.last_outcome IN ('succeeded', 'partial')
                  )
                  AND EXISTS
                  (
                      SELECT 1
                      FROM control.collector_schedule AS inventory_schedule
                      WHERE inventory_schedule.instance_id = schedule.instance_id
                        AND inventory_schedule.collector_id = 'database.inventory'
                        AND inventory_schedule.enabled
                        AND inventory_schedule.last_outcome IN ('succeeded', 'partial')
                  )
              )
          )
        ORDER BY schedule.next_due_at, contract.execution_order, target.instance_id
        LIMIT p_max_items + 1
    )
    SELECT
        due.instance_id,
        due.revision,
        due.host_name,
        due.instance_name,
        due.tcp_port,
        due.certificate_host_name,
        due.connect_timeout,
        due.authentication_mode,
        due.transport_security_mode,
        due.collector_id,
        due.collector_version,
        due.output_schema_version,
        due.schedule_revision,
        due.next_due_at,
        due.effective_circuit_state,
        due.consecutive_failure_count,
        due.effective_circuit_open_until,
        (SELECT count(*) > p_max_items FROM due),
        due.repository_time
    FROM due
    ORDER BY due.next_due_at, due.execution_order, due.instance_id
    LIMIT p_max_items;
$sqlobserver$;

CREATE FUNCTION control.begin_collection_run
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
    p_request_digest bytea
)
RETURNS TABLE
(
    result_status text,
    started_at timestamptz,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_run telemetry.collection_run%ROWTYPE;
    abandoned_run telemetry.collection_run%ROWTYPE;
    selected_schedule control.collector_schedule%ROWTYPE;
    selected_target control.observation_target%ROWTYPE;
    captured_repository_time timestamptz;
    expected_work_key text;
    has_outcome boolean;
BEGIN
    IF p_run_id IS NULL
       OR p_run_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_instance_id IS NULL
       OR p_instance_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_owner_execution_id IS NULL
       OR p_owner_execution_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_target_revision IS NULL
       OR p_target_revision <= 0
       OR p_collector_id IS NULL
       OR p_collector_version IS NULL
       OR p_collector_version <= 0
       OR p_output_schema_version IS NULL
       OR p_output_schema_version <= 0
       OR p_schedule_revision IS NULL
       OR p_schedule_revision <= 0
       OR p_scheduled_at IS NULL
       OR NOT isfinite(p_scheduled_at)
       OR octet_length(p_request_digest) IS DISTINCT FROM 32 THEN
        RAISE EXCEPTION 'invalid bounded collection-run identity'
            USING ERRCODE = '22023';
    END IF;

    expected_work_key := format(
        'collector/run/%s/%s',
        p_collector_id,
        replace(p_instance_id::text, '-', ''));
    IF p_work_key IS DISTINCT FROM expected_work_key THEN
        RAISE EXCEPTION 'collection-run lease key does not match the target and collector'
            USING ERRCODE = '22023';
    END IF;

    SELECT run.*
    INTO selected_run
    FROM telemetry.collection_run AS run
    WHERE run.run_id = p_run_id;

    IF FOUND THEN
        IF selected_run.instance_id <> p_instance_id
           OR selected_run.target_revision <> p_target_revision
           OR selected_run.collector_id <> p_collector_id
           OR selected_run.collector_version <> p_collector_version
           OR selected_run.output_schema_version <> p_output_schema_version
           OR selected_run.schedule_revision <> p_schedule_revision
           OR selected_run.scheduled_for <> p_scheduled_at
           OR selected_run.work_key <> p_work_key
           OR selected_run.owner_execution_id <> p_owner_execution_id
           OR selected_run.fencing_token <> p_fencing_token
           OR selected_run.request_digest <> p_request_digest THEN
            RAISE EXCEPTION 'collector run identifier was replayed with divergent content'
                USING ERRCODE = '22023';
        END IF;

        SELECT EXISTS
        (
            SELECT 1
            FROM telemetry.collection_run_outcome AS outcome
            WHERE outcome.run_id = p_run_id
        )
        INTO has_outcome;

        IF has_outcome THEN
            RETURN QUERY SELECT 'replayed', selected_run.started_at, clock_timestamp();
            RETURN;
        END IF;
    END IF;

    BEGIN
        PERFORM control.assert_worker_lease(
            p_work_key,
            p_owner_execution_id,
            p_fencing_token);
    EXCEPTION
        WHEN SQLSTATE '55000' THEN
            RETURN QUERY SELECT 'lease_lost', NULL::timestamptz, clock_timestamp();
            RETURN;
    END;

    captured_repository_time := clock_timestamp();

    SELECT target.*
    INTO selected_target
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'target_not_found', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    IF selected_target.lifecycle_state <> 'active' OR selected_target.host_name IS NULL THEN
        RETURN QUERY SELECT 'target_inactive', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    IF selected_target.revision <> p_target_revision THEN
        RETURN QUERY SELECT 'target_revision_conflict', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    SELECT schedule.*
    INTO selected_schedule
    FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id
    FOR UPDATE;

    IF NOT FOUND
       OR NOT selected_schedule.enabled
       OR selected_schedule.collector_version <> p_collector_version
       OR selected_schedule.target_revision <> p_target_revision
       OR selected_schedule.schedule_revision <> p_schedule_revision
       OR selected_schedule.next_due_at <> p_scheduled_at
       OR selected_schedule.next_due_at > captured_repository_time
       OR
       (
           selected_schedule.circuit_state = 'open'
           AND selected_schedule.circuit_open_until > captured_repository_time
       ) THEN
        RETURN QUERY SELECT 'schedule_conflict', NULL::timestamptz, clock_timestamp();
        RETURN;
    END IF;

    IF selected_schedule.active_run_id IS NOT NULL
       AND selected_schedule.active_run_id <> p_run_id THEN
        SELECT run.*
        INTO abandoned_run
        FROM telemetry.collection_run AS run
        WHERE run.run_id = selected_schedule.active_run_id;

        IF NOT FOUND
           OR abandoned_run.instance_id <> p_instance_id
           OR abandoned_run.collector_id <> p_collector_id THEN
            RAISE EXCEPTION 'collector schedule references an invalid active run'
                USING ERRCODE = '55000';
        END IF;

        IF NOT EXISTS
        (
            SELECT 1
            FROM telemetry.collection_run_outcome AS outcome
            WHERE outcome.run_id = abandoned_run.run_id
        ) THEN
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
                abandoned_run.run_id, 'lease_lost', 'lease_ownership_lost', 1, 0,
                least(
                    3600000,
                    greatest(
                        0,
                        floor(extract(epoch FROM
                            (captured_repository_time - abandoned_run.started_at)) * 1000)::bigint)),
                0, 0, 0, 0, 0, 0, 0, 0, false, false, 'none', true, 0, 0,
                abandoned_run.request_digest, captured_repository_time
            );

            INSERT INTO telemetry.visibility_gap
            (
                gap_id, run_id, instance_id, collector_id, reason_code,
                gap_started_at, gap_ended_at, lost_row_count, lost_byte_count,
                count_is_exact, recorded_at
            )
            VALUES
            (
                gen_random_uuid(), abandoned_run.run_id, p_instance_id, p_collector_id,
                'lease_ownership_lost', abandoned_run.scheduled_for,
                captured_repository_time, 1, 0, false, captured_repository_time
            );
        END IF;

        UPDATE control.collector_schedule AS schedule
        SET
            active_run_id = NULL,
            circuit_state = 'closed',
            consecutive_failure_count = 0,
            circuit_open_until = NULL,
            last_completed_at = captured_repository_time,
            last_outcome = 'lease_lost',
            contention_count = schedule.contention_count + 1,
            last_contention_at = captured_repository_time,
            updated_at = captured_repository_time
        WHERE schedule.instance_id = p_instance_id
          AND schedule.collector_id = p_collector_id;
    END IF;

    IF selected_run.run_id IS NOT NULL THEN
        RETURN QUERY SELECT 'running_replay', selected_run.started_at, clock_timestamp();
        RETURN;
    END IF;

    INSERT INTO telemetry.collection_run
    (
        run_id,
        instance_id,
        collector_id,
        collector_version,
        output_schema_version,
        target_revision,
        schedule_revision,
        work_key,
        owner_execution_id,
        fencing_token,
        request_digest,
        scheduled_for,
        started_at
    )
    VALUES
    (
        p_run_id,
        p_instance_id,
        p_collector_id,
        p_collector_version,
        p_output_schema_version,
        p_target_revision,
        p_schedule_revision,
        p_work_key,
        p_owner_execution_id,
        p_fencing_token,
        p_request_digest,
        p_scheduled_at,
        captured_repository_time
    );

    UPDATE control.collector_schedule AS schedule
    SET
        active_run_id = p_run_id,
        circuit_state = CASE
            WHEN schedule.circuit_state = 'open' THEN 'half_open'
            ELSE schedule.circuit_state
        END,
        circuit_open_until = NULL,
        last_started_at = captured_repository_time,
        updated_at = captured_repository_time
    WHERE schedule.instance_id = p_instance_id
      AND schedule.collector_id = p_collector_id;

    RETURN QUERY SELECT 'started', captured_repository_time, captured_repository_time;
END
$sqlobserver$;

CREATE FUNCTION control.commit_collection_run
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
               metric_count <> 8
               OR ARRAY
                  (
                      SELECT metric.metric_key
                      FROM unnest(p_metric_keys) AS metric(metric_key)
                      ORDER BY metric.metric_key
                  ) IS DISTINCT FROM ARRAY
                  [
                      'engine.batch_requests_total',
                      'engine.committed_memory_bytes',
                      'engine.page_life_expectancy_seconds',
                      'engine.process_physical_memory_bytes',
                      'engine.sql_compilations_total',
                      'engine.sql_recompilations_total',
                      'engine.target_memory_bytes',
                      'engine.user_connections'
                  ]::text[]
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
                   'engine.target_memory_bytes'
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

CREATE VIEW reporting.collector_health_projection
WITH (security_barrier = true)
AS
SELECT
    schedule.instance_id,
    schedule.collector_id,
    schedule.collector_version,
    contract.output_schema_version,
    schedule.schedule_revision,
    schedule.target_revision AS schedule_target_revision,
    schedule.enabled,
    schedule.collection_interval,
    schedule.next_due_at,
    CASE
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until <= repository_clock.value
            THEN 'half_open'
        ELSE schedule.circuit_state
    END AS circuit_state,
    schedule.consecutive_failure_count,
    CASE
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until <= repository_clock.value
            THEN NULL::timestamptz
        ELSE schedule.circuit_open_until
    END AS circuit_open_until,
    schedule.last_started_at,
    schedule.last_completed_at,
    schedule.last_succeeded_at,
    latest_data_run.run_id AS latest_data_run_id,
    latest_run.run_id,
    latest_run.target_revision,
    latest_run.scheduled_for,
    latest_run.started_at,
    latest_outcome.outcome,
    latest_outcome.reason_code,
    latest_outcome.attempt_count,
    latest_outcome.retry_count,
    latest_outcome.duration_ms,
    latest_outcome.source_row_count,
    latest_outcome.output_item_count,
    latest_outcome.inserted_item_count,
    latest_outcome.duplicate_item_count,
    latest_outcome.rejected_item_count,
    latest_outcome.response_bytes,
    latest_outcome.output_bytes,
    latest_outcome.persisted_bytes,
    latest_outcome.loss_kind,
    latest_outcome.loss_count_exact,
    latest_outcome.lost_row_count,
    latest_outcome.lost_byte_count,
    latest_outcome.completed_at,
    CASE
        WHEN NOT schedule.enabled THEN 'disabled'
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until > repository_clock.value THEN 'unavailable'
        WHEN latest_outcome.run_id IS NULL THEN 'pending'
        WHEN latest_outcome.outcome = 'succeeded'
            AND latest_outcome.completed_at + schedule.collection_interval * 2 >= repository_clock.value
            THEN 'current'
        WHEN latest_outcome.outcome = 'succeeded' THEN 'stale'
        WHEN latest_outcome.outcome IN ('partial', 'output_invalid') THEN 'degraded'
        WHEN latest_outcome.outcome = 'unsupported' THEN 'unsupported'
        ELSE 'unavailable'
    END AS health_state,
    CASE
        WHEN NOT schedule.enabled THEN 'none'
        WHEN schedule.circuit_state = 'open'
            AND schedule.circuit_open_until > repository_clock.value THEN 'circuit_open'
        WHEN latest_outcome.run_id IS NULL THEN 'never_collected'
        WHEN latest_outcome.outcome = 'succeeded'
            AND latest_outcome.completed_at + schedule.collection_interval * 2 >= repository_clock.value
            THEN 'none'
        WHEN latest_outcome.outcome = 'succeeded' THEN 'evidence_stale'
        WHEN latest_outcome.outcome = 'partial' THEN 'sample_loss'
        WHEN latest_outcome.outcome = 'permission_denied' THEN 'permission_denied'
        WHEN latest_outcome.outcome = 'timed_out' THEN 'timed_out'
        WHEN latest_outcome.outcome = 'output_invalid' THEN 'output_invalid'
        WHEN latest_outcome.reason_code = 'capability_profile_missing' THEN 'capability_profile_missing'
        WHEN latest_outcome.reason_code = 'capability_profile_stale' THEN 'capability_profile_stale'
        WHEN latest_outcome.reason_code = 'capability_missing' THEN 'capability_missing'
        WHEN latest_outcome.reason_code = 'target_version_unsupported' THEN 'version_unsupported'
        WHEN latest_outcome.reason_code = 'target_platform_unsupported' THEN 'platform_unsupported'
        WHEN latest_outcome.reason_code = 'target_edition_unsupported' THEN 'edition_unsupported'
        ELSE 'collection_failed'
    END AS health_reason,
    repository_clock.value AS repository_time
FROM control.collector_schedule AS schedule
INNER JOIN control.collector_contract AS contract
    ON contract.collector_id = schedule.collector_id
   AND contract.collector_version = schedule.collector_version
INNER JOIN control.observation_target AS target
    ON target.instance_id = schedule.instance_id
   AND target.revision = schedule.target_revision
   AND target.lifecycle_state = 'active'
   AND target.host_name IS NOT NULL
CROSS JOIN LATERAL (SELECT statement_timestamp() AS value) AS repository_clock
LEFT JOIN LATERAL
(
    SELECT run.*
    FROM telemetry.collection_run AS run
    INNER JOIN telemetry.collection_run_outcome AS outcome
        ON outcome.run_id = run.run_id
    WHERE run.instance_id = schedule.instance_id
      AND run.collector_id = schedule.collector_id
      AND run.target_revision = schedule.target_revision
      AND run.schedule_revision = schedule.schedule_revision
    ORDER BY outcome.completed_at DESC, run.run_id DESC
    LIMIT 1
) AS latest_run ON true
LEFT JOIN telemetry.collection_run_outcome AS latest_outcome
    ON latest_outcome.run_id = latest_run.run_id
LEFT JOIN LATERAL
(
    SELECT run.run_id
    FROM telemetry.collection_run AS run
    INNER JOIN telemetry.collection_run_outcome AS outcome
        ON outcome.run_id = run.run_id
       AND outcome.outcome IN ('succeeded', 'partial')
    WHERE run.instance_id = schedule.instance_id
      AND run.collector_id = schedule.collector_id
      AND run.target_revision = schedule.target_revision
      AND run.schedule_revision = schedule.schedule_revision
    ORDER BY outcome.completed_at DESC, run.run_id DESC
    LIMIT 1
) AS latest_data_run ON true;

COMMENT ON VIEW reporting.collector_health_projection IS
    'Internal security-barrier health state. Absence, staleness, circuit state, and visible loss never become healthy evidence.';

CREATE FUNCTION control.assert_health_snapshot_cursor(p_is_current boolean)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
AS $sqlobserver$
BEGIN
    IF p_is_current IS NOT TRUE THEN
        RAISE EXCEPTION 'health cursor no longer matches the current snapshot'
            USING ERRCODE = '22023';
    END IF;

    RETURN true;
END
$sqlobserver$;

CREATE FUNCTION reporting.get_instance_health(p_instance_id uuid)
RETURNS TABLE
(
    instance_id uuid,
    collector_id text,
    collector_version integer,
    output_schema_version integer,
    health_state text,
    health_reason text,
    circuit_state text,
    consecutive_failure_count integer,
    circuit_open_until timestamptz,
    run_id uuid,
    target_revision bigint,
    run_outcome text,
    run_reason text,
    duration_ms bigint,
    attempt_count integer,
    source_row_count integer,
    output_item_count integer,
    response_bytes bigint,
    output_bytes bigint,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    inserted_item_count integer,
    duplicate_item_count integer,
    rejected_item_count integer,
    persisted_bytes bigint,
    scheduled_at timestamptz,
    last_attempt_at timestamptz,
    last_success_at timestamptz,
    next_due_at timestamptz,
    repository_time timestamptz,
    metric_observed_at timestamptz,
    metric_sample_id uuid,
    metric_key text,
    metric_value double precision,
    metric_dimensions jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
BEGIN
    IF p_instance_id IS NULL THEN
        RAISE EXCEPTION 'instance health requires one target identifier'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    SELECT
        health.instance_id,
        health.collector_id,
        health.collector_version,
        health.output_schema_version,
        health.health_state,
        health.health_reason,
        health.circuit_state,
        health.consecutive_failure_count,
        health.circuit_open_until,
        health.run_id,
        health.target_revision,
        health.outcome,
        health.reason_code,
        health.duration_ms,
        health.attempt_count,
        health.source_row_count,
        health.output_item_count,
        health.response_bytes,
        health.output_bytes,
        health.loss_kind,
        health.loss_count_exact,
        health.lost_row_count,
        health.lost_byte_count,
        health.inserted_item_count,
        health.duplicate_item_count,
        health.rejected_item_count,
        health.persisted_bytes,
        coalesce(health.scheduled_for, health.next_due_at),
        health.last_completed_at,
        health.last_succeeded_at,
        health.next_due_at,
        health.repository_time,
        metric.observed_at,
        metric.sample_id,
        metric.metric_key,
        metric.metric_value,
        metric.dimensions
    FROM reporting.collector_health_projection AS health
    LEFT JOIN LATERAL
    (
        SELECT DISTINCT ON (sample.metric_key)
            sample.observed_at,
            sample.sample_id,
            sample.metric_key,
            sample.metric_value,
            sample.dimensions
        FROM telemetry.raw_metric_sample AS sample
        WHERE sample.instance_id = p_instance_id
          AND sample.collection_run_id = health.latest_data_run_id
          AND sample.metric_key LIKE 'engine.%'
        ORDER BY sample.metric_key, sample.observed_at DESC, sample.sample_id DESC
        LIMIT 64
    ) AS metric ON true
    WHERE health.instance_id = p_instance_id
      AND health.collector_id = 'engine.core'
    ORDER BY metric.metric_key;
END
$sqlobserver$;

CREATE FUNCTION reporting.list_database_health
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_database_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    instance_id uuid,
    observation_target_revision bigint,
    database_id integer,
    database_name text,
    state_code text,
    recovery_model text,
    user_access text,
    is_read_only boolean,
    compatibility_level integer,
    observed_at timestamptz,
    collector_id text,
    collector_version integer,
    output_schema_version integer,
    health_state text,
    health_reason text,
    circuit_state text,
    consecutive_failure_count integer,
    circuit_open_until timestamptz,
    run_id uuid,
    target_revision bigint,
    run_outcome text,
    run_reason text,
    duration_ms bigint,
    attempt_count integer,
    source_row_count integer,
    output_item_count integer,
    response_bytes bigint,
    output_bytes bigint,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    inserted_item_count integer,
    duplicate_item_count integer,
    rejected_item_count integer,
    persisted_bytes bigint,
    scheduled_at timestamptz,
    last_attempt_at timestamptz,
    last_success_at timestamptz,
    next_due_at timestamptz,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    has_more boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
BEGIN
    IF p_instance_id IS NULL
       OR p_max_results NOT BETWEEN 1 AND 100
       OR ((p_snapshot_run_id IS NULL) <> (p_snapshot_target_revision IS NULL))
       OR ((p_snapshot_run_id IS NULL) <> (p_after_database_id IS NULL))
       OR (p_snapshot_target_revision IS NOT NULL AND p_snapshot_target_revision <= 0)
       OR (p_after_database_id IS NOT NULL AND p_after_database_id <= 0) THEN
        RAISE EXCEPTION 'database health requires a target, bounded limit, and valid cursor'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH collector_health AS MATERIALIZED
    (
        SELECT
            health.*,
            control.assert_health_snapshot_cursor
            (
                p_snapshot_run_id IS NULL
                OR
                (
                    health.latest_data_run_id = p_snapshot_run_id
                    AND health.schedule_target_revision = p_snapshot_target_revision
                )
            ) AS cursor_is_current
        FROM reporting.collector_health_projection AS health
        WHERE health.instance_id = p_instance_id
          AND health.collector_id = 'database.inventory'
    ),
    page AS MATERIALIZED
    (
        SELECT snapshot.*
        FROM telemetry.database_inventory_snapshot AS snapshot
        CROSS JOIN collector_health AS health
        WHERE snapshot.instance_id = p_instance_id
          AND snapshot.collection_run_id = health.latest_data_run_id
          AND health.cursor_is_current
          AND (p_after_database_id IS NULL OR snapshot.database_id > p_after_database_id)
        ORDER BY snapshot.database_id
        LIMIT p_max_results + 1
    )
    SELECT
        health.instance_id,
        page.target_revision,
        page.database_id,
        page.database_name,
        page.state_code,
        page.recovery_model,
        page.user_access,
        page.is_read_only,
        page.compatibility_level,
        page.observed_at,
        health.collector_id,
        health.collector_version,
        health.output_schema_version,
        health.health_state,
        health.health_reason,
        health.circuit_state,
        health.consecutive_failure_count,
        health.circuit_open_until,
        health.run_id,
        health.target_revision,
        health.outcome,
        health.reason_code,
        health.duration_ms,
        health.attempt_count,
        health.source_row_count,
        health.output_item_count,
        health.response_bytes,
        health.output_bytes,
        health.loss_kind,
        health.loss_count_exact,
        health.lost_row_count,
        health.lost_byte_count,
        health.inserted_item_count,
        health.duplicate_item_count,
        health.rejected_item_count,
        health.persisted_bytes,
        coalesce(health.scheduled_for, health.next_due_at),
        health.last_completed_at,
        health.last_succeeded_at,
        health.next_due_at,
        health.latest_data_run_id,
        health.schedule_target_revision,
        (SELECT count(*) > p_max_results FROM page),
        health.repository_time
    FROM collector_health AS health
    LEFT JOIN page
        ON true
    ORDER BY page.database_id NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;

CREATE FUNCTION reporting.list_database_file_health
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_database_id integer,
    p_after_file_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    instance_id uuid,
    observation_target_revision bigint,
    database_id integer,
    file_id integer,
    logical_name text,
    file_type text,
    state_code text,
    size_bytes bigint,
    maximum_size_bytes bigint,
    growth_bytes bigint,
    growth_percent integer,
    read_count bigint,
    write_count bigint,
    bytes_read bigint,
    bytes_written bigint,
    io_stall_ms bigint,
    observed_at timestamptz,
    collector_id text,
    collector_version integer,
    output_schema_version integer,
    health_state text,
    health_reason text,
    circuit_state text,
    consecutive_failure_count integer,
    circuit_open_until timestamptz,
    run_id uuid,
    target_revision bigint,
    run_outcome text,
    run_reason text,
    duration_ms bigint,
    attempt_count integer,
    source_row_count integer,
    output_item_count integer,
    response_bytes bigint,
    output_bytes bigint,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    inserted_item_count integer,
    duplicate_item_count integer,
    rejected_item_count integer,
    persisted_bytes bigint,
    scheduled_at timestamptz,
    last_attempt_at timestamptz,
    last_success_at timestamptz,
    next_due_at timestamptz,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    has_more boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
BEGIN
    IF p_instance_id IS NULL
       OR p_max_results NOT BETWEEN 1 AND 100
       OR ((p_snapshot_run_id IS NULL) <> (p_snapshot_target_revision IS NULL))
       OR ((p_after_database_id IS NULL) <> (p_after_file_id IS NULL))
       OR ((p_snapshot_run_id IS NULL) <> (p_after_database_id IS NULL))
       OR (p_snapshot_target_revision IS NOT NULL AND p_snapshot_target_revision <= 0)
       OR (p_after_database_id IS NOT NULL AND (p_after_database_id <= 0 OR p_after_file_id <= 0)) THEN
        RAISE EXCEPTION 'database-file health requires a target, bounded limit, and complete valid cursor'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH collector_health AS MATERIALIZED
    (
        SELECT
            health.*,
            control.assert_health_snapshot_cursor
            (
                p_snapshot_run_id IS NULL
                OR
                (
                    health.latest_data_run_id = p_snapshot_run_id
                    AND health.schedule_target_revision = p_snapshot_target_revision
                )
            ) AS cursor_is_current
        FROM reporting.collector_health_projection AS health
        WHERE health.instance_id = p_instance_id
          AND health.collector_id = 'database.files'
    ),
    page AS MATERIALIZED
    (
        SELECT snapshot.*
        FROM telemetry.database_file_snapshot AS snapshot
        CROSS JOIN collector_health AS health
        WHERE snapshot.instance_id = p_instance_id
          AND snapshot.collection_run_id = health.latest_data_run_id
          AND health.cursor_is_current
          AND
          (
              p_after_database_id IS NULL
              OR (snapshot.database_id, snapshot.file_id) > (p_after_database_id, p_after_file_id)
          )
        ORDER BY snapshot.database_id, snapshot.file_id
        LIMIT p_max_results + 1
    )
    SELECT
        health.instance_id,
        page.target_revision,
        page.database_id,
        page.file_id,
        page.logical_name,
        page.file_type,
        page.state_code,
        page.size_bytes,
        page.maximum_size_bytes,
        page.growth_bytes,
        page.growth_percent,
        page.read_count,
        page.write_count,
        page.bytes_read,
        page.bytes_written,
        page.io_stall_ms,
        page.observed_at,
        health.collector_id,
        health.collector_version,
        health.output_schema_version,
        health.health_state,
        health.health_reason,
        health.circuit_state,
        health.consecutive_failure_count,
        health.circuit_open_until,
        health.run_id,
        health.target_revision,
        health.outcome,
        health.reason_code,
        health.duration_ms,
        health.attempt_count,
        health.source_row_count,
        health.output_item_count,
        health.response_bytes,
        health.output_bytes,
        health.loss_kind,
        health.loss_count_exact,
        health.lost_row_count,
        health.lost_byte_count,
        health.inserted_item_count,
        health.duplicate_item_count,
        health.rejected_item_count,
        health.persisted_bytes,
        coalesce(health.scheduled_for, health.next_due_at),
        health.last_completed_at,
        health.last_succeeded_at,
        health.next_due_at,
        health.latest_data_run_id,
        health.schedule_target_revision,
        (SELECT count(*) > p_max_results FROM page),
        health.repository_time
    FROM collector_health AS health
    LEFT JOIN page
        ON true
    ORDER BY page.database_id NULLS LAST, page.file_id NULLS LAST
    LIMIT p_max_results;
END
$sqlobserver$;

COMMENT ON FUNCTION control.reconcile_collector_catalog(
    text[], integer[], bytea[], bytea[], integer[], text, uuid, bigint) IS
    'Lease-fenced reconciliation of the exact ordered M4 registry and per-target schedules.';
COMMENT ON FUNCTION control.list_due_collector_work(integer) IS
    'Returns at most sixteen repository-clock due target/collector pairs in deterministic dependency order.';
COMMENT ON FUNCTION control.begin_collection_run(
    uuid, uuid, bigint, text, integer, integer, bigint, timestamptz,
    text, uuid, bigint, bytea) IS
    'Starts a run before target I/O with target, schedule, due-time, replay, and lease fencing.';
COMMENT ON FUNCTION control.assert_health_snapshot_cursor(boolean) IS
    'Fails closed when a page cursor is not bound to the current target-revision snapshot in the statement snapshot.';
COMMENT ON FUNCTION reporting.get_instance_health(uuid) IS
    'Returns the target-filtered engine.core state and at most sixty-four latest run-bound metrics.';
COMMENT ON FUNCTION reporting.list_database_health(uuid, uuid, bigint, integer, integer) IS
    'Returns at most one hundred target-filtered database health rows after the stable cursor.';
COMMENT ON FUNCTION reporting.list_database_file_health(uuid, uuid, bigint, integer, integer, integer) IS
    'Returns at most one hundred target-filtered database-file health rows after the stable composite cursor.';

REVOKE ALL ON TABLE
    control.collector_contract,
    control.collector_schedule,
    telemetry.collection_run,
    telemetry.collection_run_outcome,
    telemetry.visibility_gap,
    telemetry.database_inventory_snapshot,
    telemetry.database_file_snapshot,
    reporting.collector_health_projection
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

-- Preserve the M2 generic ingestion path, but do not allow it to forge M4 run provenance.
REVOKE SELECT ON TABLE telemetry.raw_metric_sample FROM sqlobserver_server;
REVOKE INSERT ON TABLE telemetry.raw_metric_sample FROM sqlobserver_collector;
GRANT INSERT
(
    observed_at,
    sample_id,
    instance_id,
    metric_key,
    metric_value,
    dimensions,
    collected_at
)
    ON TABLE telemetry.raw_metric_sample
    TO sqlobserver_collector;

DO $sqlobserver$
DECLARE
    function_signature regprocedure;
    matched_count integer;
BEGIN
    SELECT count(*)::integer
    INTO matched_count
    FROM pg_catalog.pg_proc AS procedure
    INNER JOIN pg_catalog.pg_namespace AS namespace
        ON namespace.oid = procedure.pronamespace
    WHERE
    (
        namespace.nspname = 'control'
        AND procedure.proname IN
        (
            'ensure_target_collector_schedules',
            'reject_collector_history_mutation',
            'reconcile_collector_catalog',
            'list_due_collector_work',
            'begin_collection_run',
            'commit_collection_run',
            'assert_health_snapshot_cursor'
        )
    )
    OR
    (
        namespace.nspname = 'reporting'
        AND procedure.proname IN
        (
            'get_instance_health',
            'list_database_health',
            'list_database_file_health'
        )
    );

    IF matched_count <> 10 THEN
        RAISE EXCEPTION 'M4 runtime function grant set is incomplete or ambiguous'
            USING ERRCODE = '55000';
    END IF;

    FOR function_signature IN
        SELECT procedure.oid::regprocedure
        FROM pg_catalog.pg_proc AS procedure
        INNER JOIN pg_catalog.pg_namespace AS namespace
            ON namespace.oid = procedure.pronamespace
        WHERE
        (
            namespace.nspname = 'control'
            AND procedure.proname IN
            (
                'ensure_target_collector_schedules',
                'reject_collector_history_mutation',
                'reconcile_collector_catalog',
                'list_due_collector_work',
                'begin_collection_run',
                'commit_collection_run',
                'assert_health_snapshot_cursor'
            )
        )
        OR
        (
            namespace.nspname = 'reporting'
            AND procedure.proname IN
            (
                'get_instance_health',
                'list_database_health',
                'list_database_file_health'
            )
        )
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', function_signature);
    END LOOP;

    FOR function_signature IN
        SELECT procedure.oid::regprocedure
        FROM pg_catalog.pg_proc AS procedure
        INNER JOIN pg_catalog.pg_namespace AS namespace
            ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'control'
          AND procedure.proname IN
          (
              'reconcile_collector_catalog',
              'list_due_collector_work',
              'begin_collection_run',
              'commit_collection_run'
          )
    LOOP
        EXECUTE format(
            'GRANT EXECUTE ON FUNCTION %s TO sqlobserver_collector',
            function_signature);
    END LOOP;

    FOR function_signature IN
        SELECT procedure.oid::regprocedure
        FROM pg_catalog.pg_proc AS procedure
        INNER JOIN pg_catalog.pg_namespace AS namespace
            ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'reporting'
          AND procedure.proname IN
          (
              'get_instance_health',
              'list_database_health',
              'list_database_file_health'
          )
    LOOP
        EXECUTE format(
            'GRANT EXECUTE ON FUNCTION %s TO sqlobserver_server',
            function_signature);
    END LOOP;
END
$sqlobserver$;
