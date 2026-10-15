-- sqlobserver:nontransactional-index=telemetry.ix_activity_request_completion
-- Keep the outcome qualifier after completion time so a run lookup uses one
-- covering index range rather than separate probes for each allowed outcome.
CREATE INDEX CONCURRENTLY ix_activity_request_completion
    ON telemetry.collection_run_outcome (run_id, completed_at, outcome);
