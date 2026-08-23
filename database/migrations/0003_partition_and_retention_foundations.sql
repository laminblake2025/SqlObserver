-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE TABLE system.partition_registry
(
    parent_schema name NOT NULL,
    parent_table name NOT NULL,
    partition_schema name NOT NULL,
    partition_name name NOT NULL,
    partition_granularity text NOT NULL,
    range_start timestamptz NOT NULL,
    range_end timestamptz NOT NULL,
    lifecycle_state text NOT NULL DEFAULT 'attached',
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    detached_at timestamptz,
    dropped_at timestamptz,
    CONSTRAINT pk_partition_registry PRIMARY KEY (
        parent_schema,
        parent_table,
        range_start
    ),
    CONSTRAINT uq_partition_registry_name UNIQUE (
        partition_schema,
        partition_name
    ),
    CONSTRAINT ck_partition_registry_parent CHECK (
        (parent_schema = 'telemetry'::name AND parent_table = 'raw_metric_sample'::name)
        OR (parent_schema = 'events'::name AND parent_table = 'diagnostic_event'::name)
    ),
    CONSTRAINT ck_partition_registry_schema CHECK (
        partition_schema = parent_schema
    ),
    CONSTRAINT ck_partition_registry_granularity CHECK (
        partition_granularity IN ('day', 'month')
    ),
    CONSTRAINT ck_partition_registry_range CHECK (range_end > range_start),
    CONSTRAINT ck_partition_registry_state CHECK (
        lifecycle_state IN ('attached', 'detached', 'dropped')
    ),
    CONSTRAINT ck_partition_registry_lifecycle_times CHECK (
        (detached_at IS NULL OR detached_at >= created_at)
        AND (dropped_at IS NULL OR detached_at IS NOT NULL)
        AND (dropped_at IS NULL OR dropped_at >= detached_at)
        AND (lifecycle_state <> 'attached' OR (detached_at IS NULL AND dropped_at IS NULL))
        AND (lifecycle_state <> 'detached' OR (detached_at IS NOT NULL AND dropped_at IS NULL))
        AND (lifecycle_state <> 'dropped' OR dropped_at IS NOT NULL)
    )
);

COMMENT ON TABLE system.partition_registry IS
    'Authoritative UTC bounds and lifecycle evidence for allowlisted repository partitions.';

CREATE INDEX ix_partition_registry_expiry_scan
    ON system.partition_registry (parent_schema, parent_table, lifecycle_state, range_end);

CREATE TABLE system.retention_policy
(
    data_class text PRIMARY KEY,
    parent_schema name NOT NULL,
    parent_table name NOT NULL,
    partition_granularity text NOT NULL,
    enabled boolean NOT NULL DEFAULT false,
    retain_for interval,
    minimum_partitions_to_keep integer NOT NULL DEFAULT 2,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_by text NOT NULL DEFAULT session_user,
    CONSTRAINT uq_retention_policy_parent UNIQUE (parent_schema, parent_table),
    CONSTRAINT ck_retention_policy_data_class CHECK (
        data_class IN ('raw_metric_samples', 'diagnostic_events')
    ),
    CONSTRAINT ck_retention_policy_parent CHECK (
        (data_class = 'raw_metric_samples'
            AND parent_schema = 'telemetry'::name
            AND parent_table = 'raw_metric_sample'::name
            AND partition_granularity = 'day')
        OR (data_class = 'diagnostic_events'
            AND parent_schema = 'events'::name
            AND parent_table = 'diagnostic_event'::name
            AND partition_granularity = 'month')
    ),
    CONSTRAINT ck_retention_policy_enabled_duration CHECK (
        NOT enabled OR retain_for IS NOT NULL
    ),
    CONSTRAINT ck_retention_policy_duration CHECK (
        retain_for IS NULL OR retain_for >= interval '1 day'
    ),
    CONSTRAINT ck_retention_policy_minimum_partitions CHECK (
        minimum_partitions_to_keep >= 1
    )
);

COMMENT ON TABLE system.retention_policy IS
    'Previewable partition-retention configuration. M2 installs disabled rows and no detach/drop executor.';
COMMENT ON COLUMN system.retention_policy.retain_for IS
    'Intentionally NULL until an operator-reviewed capacity and compliance policy is supplied.';

INSERT INTO system.retention_policy
(
    data_class,
    parent_schema,
    parent_table,
    partition_granularity,
    enabled,
    retain_for,
    minimum_partitions_to_keep
)
VALUES
    ('raw_metric_samples', 'telemetry', 'raw_metric_sample', 'day', false, NULL, 2),
    ('diagnostic_events', 'events', 'diagnostic_event', 'month', false, NULL, 2);

REVOKE ALL ON TABLE system.partition_registry, system.retention_policy FROM PUBLIC;
