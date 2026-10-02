-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_availability_group_replica_snapshot
CREATE INDEX ix_run_ref_availability_group_replica_snapshot
    ON ONLY telemetry.availability_group_replica_snapshot (run_id);
