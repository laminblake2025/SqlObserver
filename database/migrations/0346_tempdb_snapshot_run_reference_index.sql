-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_tempdb_snapshot
CREATE INDEX ix_run_ref_tempdb_snapshot
    ON ONLY telemetry.tempdb_snapshot (run_id);
