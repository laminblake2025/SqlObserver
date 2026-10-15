-- sqlobserver:nontransactional-index=telemetry.ix_activity_request_run_scope
CREATE INDEX CONCURRENTLY ix_activity_request_run_scope
    ON telemetry.collection_run (instance_id, collector_id, target_revision, run_id);
