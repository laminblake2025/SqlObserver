-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_availability_group_database_snapshot
CREATE INDEX ix_run_ref_availability_group_database_snapshot
    ON ONLY telemetry.availability_group_database_snapshot (run_id);
