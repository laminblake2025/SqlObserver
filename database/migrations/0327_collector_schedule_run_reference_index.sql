-- sqlobserver:nontransactional-index=control.ix_run_ref_collector_schedule
CREATE INDEX CONCURRENTLY ix_run_ref_collector_schedule
    ON control.collector_schedule (active_run_id);
