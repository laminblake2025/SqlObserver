-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_sql_agent_failure_scan_snapshot
CREATE INDEX ix_run_ref_sql_agent_failure_scan_snapshot
    ON ONLY telemetry.sql_agent_failure_scan_snapshot (run_id);
