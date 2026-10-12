-- sqlobserver:nontransactional-index=events.ix_run_ref_deadlock_summary
CREATE INDEX CONCURRENTLY ix_run_ref_deadlock_summary
    ON events.deadlock_summary (collection_run_id);
