-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_activity_session_snapshot
CREATE INDEX ix_run_ref_activity_session_snapshot
    ON ONLY telemetry.activity_session_snapshot (collection_run_id);
