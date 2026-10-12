-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_backup_status_snapshot
CREATE INDEX ix_run_ref_backup_status_snapshot
    ON ONLY telemetry.backup_status_snapshot (run_id);
