-- sqlobserver:nontransactional-index=events.ix_query_performance_run_retention
CREATE INDEX CONCURRENTLY ix_query_performance_run_retention
    ON events.query_performance_run (instance_id, window_end, collection_run_id);
