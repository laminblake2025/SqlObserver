-- sqlobserver:nontransactional-index=events.ix_query_performance_query_target_backfill
CREATE INDEX CONCURRENTLY ix_query_performance_query_target_backfill
    ON events.query_performance_query (instance_id, collection_run_id, database_id, query_fingerprint);
