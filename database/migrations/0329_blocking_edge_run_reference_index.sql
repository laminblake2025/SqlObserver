-- sqlobserver:partitioned-concurrent-index=events.ix_run_ref_blocking_edge
CREATE INDEX ix_run_ref_blocking_edge
    ON ONLY events.blocking_edge (collection_run_id);
