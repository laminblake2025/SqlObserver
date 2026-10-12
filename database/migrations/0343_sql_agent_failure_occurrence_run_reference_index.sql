-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_sql_agent_failure_occurrence
CREATE INDEX ix_run_ref_sql_agent_failure_occurrence
    ON ONLY telemetry.sql_agent_failure_occurrence (run_id);
