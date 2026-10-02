-- sqlobserver:partitioned-concurrent-index=events.ix_run_ref_deadlock_read_receipt
CREATE INDEX ix_run_ref_deadlock_read_receipt
    ON ONLY events.deadlock_read_receipt (run_id);
