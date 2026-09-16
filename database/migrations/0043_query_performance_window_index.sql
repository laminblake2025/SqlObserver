-- Allow bounded time-window reads to prune historical observations before the
-- existing target-scoped joins and row-security checks. No query semantics,
-- authorization policies, stored evidence or timeout limits are changed.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
CREATE INDEX ix_query_performance_observation_window
    ON events.query_performance_observation
       (interval_start, interval_end, collection_run_id, database_id, query_fingerprint);
