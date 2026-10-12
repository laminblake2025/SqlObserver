-- sqlobserver:partitioned-concurrent-index=telemetry.ix_server_wait_target_history
CREATE INDEX ix_server_wait_target_history
    ON ONLY telemetry.server_wait_snapshot (instance_id, observed_at, collection_run_id, wait_type);
