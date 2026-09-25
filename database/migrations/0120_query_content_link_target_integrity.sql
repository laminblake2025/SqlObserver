-- New query-content links must reference ciphertext owned by their exact target.
-- Historical links are left for reconciliation; NOT VALID enforces new writes.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE security.protected_diagnostic_payload
    ADD CONSTRAINT uq_protected_payload_id_target UNIQUE (payload_id, instance_id);

ALTER TABLE events.query_performance_content_link
    ADD CONSTRAINT fk_query_content_link_payload_target
    FOREIGN KEY (content_reference, instance_id)
    REFERENCES security.protected_diagnostic_payload (payload_id, instance_id)
    NOT VALID;
