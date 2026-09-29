-- Keep M5 evidence on the original run table until its fenced reader route is activated.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE VIEW reporting.activity_snapshot_evidence_legacy
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
FROM telemetry.collection_run AS run
INNER JOIN telemetry.collection_run_outcome AS outcome
    ON outcome.run_id = run.run_id
WHERE outcome.outcome IN ('succeeded', 'partial')
  AND run.collector_id IN
      ('activity.sessions', 'activity.requests', 'waits.server', 'blocking.current');
REVOKE ALL ON reporting.activity_snapshot_evidence_legacy
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON reporting.activity_snapshot_evidence_legacy TO sqlobserver_migrator;

CREATE OR REPLACE VIEW reporting.activity_snapshot_evidence
WITH (security_barrier = true)
AS
SELECT source.*
FROM reporting.activity_snapshot_evidence_legacy AS source
CROSS JOIN system.collection_run_v2_activity_read_route AS route
WHERE NOT route.use_v2
UNION ALL
SELECT successor.*
FROM reporting.activity_snapshot_evidence_v2_shadow AS successor
CROSS JOIN system.collection_run_v2_activity_read_route AS route
WHERE route.use_v2;
