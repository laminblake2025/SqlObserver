-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_server_wait_snapshot
CREATE INDEX ix_run_ref_server_wait_snapshot
    ON ONLY telemetry.server_wait_snapshot (collection_run_id);
