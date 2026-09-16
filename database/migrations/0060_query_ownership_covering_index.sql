-- Serve the existing correlated ownership/RLS predicate from the index. Retain
-- every target/role check; avoid random heap reads for a large historical window.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
CREATE INDEX ix_query_performance_query_ownership
    ON events.query_performance_query
       (collection_run_id, database_id, query_fingerprint) INCLUDE (instance_id);
