-- sqlobserver:partitioned-concurrent-index=telemetry.ix_activity_request_run_scope_v2
CREATE INDEX ix_activity_request_run_scope_v2
    ON ONLY telemetry.collection_run_v2 (instance_id, collector_id, target_revision, run_id);
