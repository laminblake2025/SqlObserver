-- Bind new protected query payloads to one monitored target. Historical rows
-- without an owner stay readable for migration/retention, but cannot be reused
-- as new query-content references.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE security.protected_diagnostic_payload
    ADD COLUMN instance_id uuid;

ALTER TABLE security.protected_diagnostic_payload
    ADD CONSTRAINT ck_protected_payload_target_required
    CHECK (instance_id IS NOT NULL) NOT VALID;

ALTER TABLE security.protected_diagnostic_payload
    ADD CONSTRAINT fk_protected_payload_target
    FOREIGN KEY (instance_id) REFERENCES control.observation_target(instance_id) NOT VALID;

GRANT SELECT (instance_id), INSERT (instance_id)
    ON TABLE security.protected_diagnostic_payload TO sqlobserver_collector;

COMMENT ON COLUMN security.protected_diagnostic_payload.instance_id IS
    'Exact monitored target for newly stored ciphertext; legacy null owners are not reusable.';
