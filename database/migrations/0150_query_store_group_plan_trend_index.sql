-- sqlobserver:partitioned-concurrent-index=events.ix_query_store_group_delta_plan_window
CREATE INDEX ix_query_store_group_delta_plan_window
    ON ONLY events.query_store_group_delta (instance_id, database_id, query_fingerprint, plan_fingerprint, observed_at);
