-- Private view parity surfaces for M5 evidence and fleet collector health.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE VIEW reporting.activity_snapshot_evidence_v2_shadow
WITH (security_barrier = true)
AS
SELECT
    run.instance_id,
    run.run_id,
    run.target_revision,
    run.collector_id,
    outcome.outcome,
    outcome.reason_code,
    outcome.loss_kind,
    outcome.loss_count_exact,
    outcome.lost_row_count,
    outcome.lost_byte_count,
    outcome.completed_at
FROM telemetry.collection_run_v2 AS run
INNER JOIN telemetry.collection_run_outcome AS outcome
    ON outcome.run_id = run.run_id
WHERE outcome.outcome IN ('succeeded', 'partial')
  AND run.collector_id IN
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current');
REVOKE ALL ON reporting.activity_snapshot_evidence_v2_shadow
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON reporting.activity_snapshot_evidence_v2_shadow TO sqlobserver_migrator;

CREATE VIEW reporting.collector_health_projection_v2_shadow
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
    FROM telemetry.collection_run_v2 AS run
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
    FROM telemetry.collection_run_v2 AS run
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
REVOKE ALL ON reporting.collector_health_projection_v2_shadow
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON reporting.collector_health_projection_v2_shadow TO sqlobserver_migrator;
