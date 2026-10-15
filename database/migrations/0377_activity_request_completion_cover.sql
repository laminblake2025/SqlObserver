-- sqlobserver:nontransactional-index=telemetry.ix_activity_request_completion
CREATE INDEX CONCURRENTLY ix_activity_request_completion
    ON telemetry.collection_run_outcome (run_id, completed_at, outcome);
