-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_availability_replica_evidence
CREATE INDEX ix_run_ref_availability_replica_evidence
    ON ONLY telemetry.availability_replica_evidence (run_id);
