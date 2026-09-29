-- Stage a fleet-gated collector-health read route; installation keeps original reads.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_health_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_health_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_health_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE VIEW reporting.collector_health_projection_legacy
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
REVOKE ALL ON reporting.collector_health_projection_legacy
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON reporting.collector_health_projection_legacy TO sqlobserver_migrator;

CREATE OR REPLACE VIEW reporting.collector_health_projection
WITH (security_barrier = true)
AS
SELECT source.*
FROM reporting.collector_health_projection_legacy AS source
CROSS JOIN system.collection_run_v2_health_read_route AS route
WHERE NOT route.use_v2
UNION ALL
SELECT successor.*
FROM reporting.collector_health_projection_v2_shadow AS successor
CROSS JOIN system.collection_run_v2_health_read_route AS route
WHERE route.use_v2;

-- The verifier retains the writer fence until this route update commits.
CREATE FUNCTION control.set_collection_run_v2_health_read_route(
 p_use_v2 boolean,p_max_pairs integer DEFAULT 10000)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,system
SET TimeZone='UTC'
AS $cutover$
DECLARE
 v_revision bigint;
BEGIN
 IF p_use_v2 IS NULL OR p_max_pairs IS NULL
    OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'collector health read route bounds are invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collector health read route requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_health_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_health_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'collector health read route is missing'
   USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_health_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_health_read_route(boolean,integer)
 TO sqlobserver_migrator;
