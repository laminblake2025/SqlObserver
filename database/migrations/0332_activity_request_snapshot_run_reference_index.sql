-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_activity_request_snapshot
CREATE INDEX ix_run_ref_activity_request_snapshot
    ON ONLY telemetry.activity_request_snapshot (collection_run_id);
