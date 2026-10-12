-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_sql_volume_snapshot
CREATE INDEX ix_run_ref_sql_volume_snapshot
    ON ONLY telemetry.sql_volume_snapshot (run_id);
