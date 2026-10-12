-- sqlobserver:nontransactional-index=alerting.ix_run_ref_deadlock_source_snapshot
CREATE INDEX CONCURRENTLY ix_run_ref_deadlock_source_snapshot
    ON alerting.deadlock_source_snapshot (run_id);
