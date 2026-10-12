-- sqlobserver:nontransactional-index=telemetry.ix_run_ref_sql_agent_failure
CREATE INDEX CONCURRENTLY ix_run_ref_sql_agent_failure
    ON telemetry.sql_agent_failure (run_id);
