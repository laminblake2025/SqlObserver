-- Add a nullable health fact for the future version-2 database inventory source.
-- Historical version-1 rows have no log-reuse evidence and must remain unknown.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE telemetry.database_inventory_snapshot
    ADD COLUMN log_reuse_wait_desc text;

ALTER TABLE telemetry.database_inventory_snapshot
    ADD CONSTRAINT ck_database_inventory_log_reuse_wait_desc
    CHECK (
        log_reuse_wait_desc IS NULL OR
        (octet_length(log_reuse_wait_desc) BETWEEN 1 AND 60 AND
         log_reuse_wait_desc ~ '^[A-Z][A-Z0-9_]*$')
    ) NOT VALID;

COMMENT ON COLUMN telemetry.database_inventory_snapshot.log_reuse_wait_desc IS
    'Current SQL Server log-reuse wait as of the last checkpoint. NULL means the source did not provide this fact; it does not mean NOTHING.';
