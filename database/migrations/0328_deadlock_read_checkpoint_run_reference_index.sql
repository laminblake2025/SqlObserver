-- sqlobserver:nontransactional-index=control.ix_run_ref_deadlock_read_checkpoint
CREATE INDEX CONCURRENTLY ix_run_ref_deadlock_read_checkpoint
    ON control.deadlock_read_checkpoint (run_id);
