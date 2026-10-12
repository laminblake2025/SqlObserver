-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_backup_v2_scan
CREATE INDEX ix_run_ref_backup_v2_scan
    ON ONLY telemetry.backup_v2_scan (run_id);
