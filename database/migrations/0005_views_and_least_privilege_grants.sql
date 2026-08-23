-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE VIEW reporting.partition_retention_preview
WITH (security_barrier = true)
AS
WITH ranked_partitions AS
(
    SELECT
        registry.parent_schema,
        registry.parent_table,
        registry.partition_schema,
        registry.partition_name,
        registry.partition_granularity,
        registry.range_start,
        registry.range_end,
        registry.lifecycle_state,
        policy.data_class,
        policy.enabled AS policy_enabled,
        policy.retain_for,
        policy.minimum_partitions_to_keep,
        clock_timestamp() AS repository_time,
        coalesce(greatest(round(relation.reltuples)::bigint, 0), 0) AS estimated_rows,
        coalesce(pg_total_relation_size(relation.oid::regclass), 0) AS estimated_bytes,
        row_number() OVER
        (
            PARTITION BY registry.parent_schema, registry.parent_table
            ORDER BY registry.range_end DESC
        ) AS newest_rank
    FROM system.partition_registry AS registry
    INNER JOIN system.retention_policy AS policy
        ON policy.parent_schema = registry.parent_schema
       AND policy.parent_table = registry.parent_table
    LEFT JOIN pg_class AS relation
        ON relation.oid = to_regclass(
            format('%I.%I', registry.partition_schema, registry.partition_name)
        )
    WHERE registry.lifecycle_state = 'attached'
)
SELECT
    parent_schema,
    parent_table,
    partition_schema,
    partition_name,
    partition_granularity,
    range_start,
    range_end,
    data_class,
    policy_enabled,
    retain_for,
    minimum_partitions_to_keep,
    repository_time,
    estimated_rows,
    estimated_bytes,
    false AS recovery_prerequisite_satisfied,
    policy_enabled
        AND retain_for IS NOT NULL
        AND newest_rank > minimum_partitions_to_keep
        AND range_end <= repository_time - retain_for
        AND false AS eligible_for_retention,
    CASE
        WHEN NOT policy_enabled THEN 'policy_disabled'
        WHEN retain_for IS NULL THEN 'duration_unconfigured'
        WHEN newest_rank <= minimum_partitions_to_keep THEN 'minimum_partition_floor'
        WHEN range_end > repository_time - retain_for THEN 'within_retention_window'
        ELSE 'recovery_prerequisite_unsatisfied'
    END AS retention_reason
FROM ranked_partitions;

COMMENT ON VIEW reporting.partition_retention_preview IS
    'Read-only repository-clock preview. It never detaches, drops, or otherwise mutates a partition.';

REVOKE ALL ON TABLE reporting.partition_retention_preview FROM PUBLIC;

GRANT USAGE ON SCHEMA control, telemetry, events, reporting, audit
    TO sqlobserver_server;
GRANT USAGE ON SCHEMA control, security, telemetry, events, reporting, audit
    TO sqlobserver_collector;
GRANT USAGE ON SCHEMA audit
    TO sqlobserver_auditor;

GRANT SELECT ON TABLE control.observation_target
    TO sqlobserver_server, sqlobserver_collector;
GRANT SELECT ON TABLE telemetry.raw_metric_sample, events.diagnostic_event
    TO sqlobserver_server;
GRANT SELECT (observed_at, sample_id)
    ON TABLE telemetry.raw_metric_sample
    TO sqlobserver_collector;
GRANT SELECT (occurred_at, event_id)
    ON TABLE events.diagnostic_event
    TO sqlobserver_collector;
GRANT INSERT ON TABLE telemetry.raw_metric_sample, events.diagnostic_event
    TO sqlobserver_collector;

GRANT SELECT (payload_id, payload_kind, fingerprint)
    ON TABLE security.protected_diagnostic_payload
    TO sqlobserver_collector;
GRANT INSERT
(
    payload_id,
    payload_kind,
    fingerprint,
    protection_algorithm,
    key_identifier,
    nonce,
    authentication_tag,
    ciphertext
)
    ON TABLE security.protected_diagnostic_payload
    TO sqlobserver_collector;

GRANT INSERT
(
    activity_id,
    actor_kind,
    actor_identifier,
    action_name,
    authorization_result,
    outcome,
    subject_kind,
    subject_identifier,
    correlation_id,
    parameter_digest,
    duration_ms,
    response_bytes,
    safe_details
)
    ON TABLE audit.activity
    TO sqlobserver_server, sqlobserver_collector;
GRANT SELECT ON TABLE audit.activity
    TO sqlobserver_auditor;

GRANT SELECT ON TABLE reporting.partition_retention_preview
    TO sqlobserver_server, sqlobserver_collector;

GRANT EXECUTE ON FUNCTION control.ensure_daily_metric_partition(date)
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.ensure_monthly_event_partition(date)
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.acquire_worker_lease(text, uuid, interval)
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.renew_worker_lease(text, uuid, bigint, interval)
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.release_worker_lease(text, uuid, bigint)
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.assert_worker_lease(text, uuid, bigint)
    TO sqlobserver_collector;
