-- New protected-content links must identify a persisted query in the same
-- collection run and on the same target. Existing links need reconciliation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE events.query_performance_query
    ADD CONSTRAINT uq_query_performance_query_target
    UNIQUE (collection_run_id, database_id, query_fingerprint, instance_id);

ALTER TABLE events.query_performance_content_link
    ADD CONSTRAINT fk_query_content_link_query_target
    FOREIGN KEY (collection_run_id, database_id, query_fingerprint, instance_id)
    REFERENCES events.query_performance_query
        (collection_run_id, database_id, query_fingerprint, instance_id)
    NOT VALID;
