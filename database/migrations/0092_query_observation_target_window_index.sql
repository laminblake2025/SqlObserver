-- sqlobserver:nontransactional-index=events.ix_query_observation_target_window
CREATE INDEX CONCURRENTLY ix_query_observation_target_window
    ON events.query_performance_observation (instance_id, interval_end);
