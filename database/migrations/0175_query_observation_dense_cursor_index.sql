-- sqlobserver:nontransactional-index=events.ix_query_observation_dense_cursor
CREATE INDEX CONCURRENTLY ix_query_observation_dense_cursor
    ON events.query_performance_observation (instance_id, interval_end, collection_run_id, database_id, query_fingerprint, observation_key);
