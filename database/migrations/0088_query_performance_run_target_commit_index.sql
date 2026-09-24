-- sqlobserver:nontransactional-index=events.ix_query_performance_run_target_commit
CREATE INDEX CONCURRENTLY ix_query_performance_run_target_commit
    ON events.query_performance_run (instance_id, committed_at, collection_run_id);
