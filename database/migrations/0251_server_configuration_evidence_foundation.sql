-- Stage private exact-value evidence. No collector is registered or enabled yet.
-- The fenced writer, read API, and retention route follow in separate migrations.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.server_configuration_snapshot (
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
    configuration_id integer NOT NULL CHECK (configuration_id BETWEEN 1 AND 1000000),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    server_major_version smallint NOT NULL CHECK (server_major_version BETWEEN 15 AND 17),
    configuration_name text NOT NULL CHECK (
        octet_length(configuration_name) BETWEEN 1 AND 256
        AND btrim(configuration_name) <> ''
        AND configuration_name !~ '[[:cntrl:]]'),
    source_type text NOT NULL CHECK (
        octet_length(source_type) BETWEEN 1 AND 32
        AND source_type ~ '^[a-z][a-z0-9_]*$'),
    configured_value text NOT NULL CHECK (
        octet_length(configured_value) BETWEEN 1 AND 128
        AND configured_value !~ '[[:cntrl:]]'),
    in_use_value text NOT NULL CHECK (
        octet_length(in_use_value) BETWEEN 1 AND 128
        AND in_use_value !~ '[[:cntrl:]]'),
    is_dynamic boolean NOT NULL,
    is_advanced boolean NOT NULL,
    observed_at timestamptz NOT NULL CHECK (isfinite(observed_at)),
    collected_at timestamptz NOT NULL DEFAULT clock_timestamp()
        CHECK (isfinite(collected_at)),
    CONSTRAINT pk_server_configuration_snapshot
        PRIMARY KEY (instance_id,run_id,configuration_id),
    CONSTRAINT uq_server_configuration_run_config
        UNIQUE (run_id,configuration_id)
);
CREATE INDEX ix_server_configuration_snapshot_target_time
    ON telemetry.server_configuration_snapshot
    (instance_id,observed_at DESC,run_id,configuration_id);
COMMENT ON TABLE telemetry.server_configuration_snapshot IS
    'Exact configured and in-use SQL Server values from a complete, fenced configuration scan. The collector is not registered by this migration.';

ALTER TABLE telemetry.server_configuration_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.server_configuration_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY server_configuration_snapshot_scope
    ON telemetry.server_configuration_snapshot
    USING (instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK (instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY server_configuration_snapshot_migrator
    ON telemetry.server_configuration_snapshot FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
REVOKE ALL ON telemetry.server_configuration_snapshot
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER server_configuration_snapshot_append_only
    BEFORE UPDATE OR DELETE ON telemetry.server_configuration_snapshot
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE TABLE events.server_configuration_change (
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    run_id uuid NOT NULL,
    configuration_id integer NOT NULL,
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    server_major_version smallint NOT NULL CHECK (server_major_version BETWEEN 15 AND 17),
    configuration_name text NOT NULL CHECK (
        octet_length(configuration_name) BETWEEN 1 AND 256
        AND btrim(configuration_name) <> ''
        AND configuration_name !~ '[[:cntrl:]]'),
    source_type text NOT NULL CHECK (
        octet_length(source_type) BETWEEN 1 AND 32
        AND source_type ~ '^[a-z][a-z0-9_]*$'),
    previous_configured_value text NOT NULL CHECK (
        octet_length(previous_configured_value) BETWEEN 1 AND 128
        AND previous_configured_value !~ '[[:cntrl:]]'),
    configured_value text NOT NULL CHECK (
        octet_length(configured_value) BETWEEN 1 AND 128
        AND configured_value !~ '[[:cntrl:]]'),
    previous_in_use_value text NOT NULL CHECK (
        octet_length(previous_in_use_value) BETWEEN 1 AND 128
        AND previous_in_use_value !~ '[[:cntrl:]]'),
    in_use_value text NOT NULL CHECK (
        octet_length(in_use_value) BETWEEN 1 AND 128
        AND in_use_value !~ '[[:cntrl:]]'),
    configured_changed boolean NOT NULL,
    in_use_changed boolean NOT NULL,
    previous_observed_at timestamptz NOT NULL CHECK (isfinite(previous_observed_at)),
    observed_at timestamptz NOT NULL CHECK (isfinite(observed_at)),
    detected_at timestamptz NOT NULL DEFAULT clock_timestamp()
        CHECK (isfinite(detected_at)),
    CONSTRAINT pk_server_configuration_change
        PRIMARY KEY (instance_id,run_id,configuration_id),
    CONSTRAINT fk_server_configuration_change_snapshot
        FOREIGN KEY (instance_id,run_id,configuration_id)
        REFERENCES telemetry.server_configuration_snapshot
            (instance_id,run_id,configuration_id),
    CONSTRAINT ck_server_configuration_change_difference CHECK (
        configured_changed=(previous_configured_value IS DISTINCT FROM configured_value)
        AND in_use_changed=(previous_in_use_value IS DISTINCT FROM in_use_value)
        AND (configured_changed OR in_use_changed))
);
CREATE INDEX ix_server_configuration_change_target_time
    ON events.server_configuration_change
    (instance_id,detected_at DESC,run_id,configuration_id);
COMMENT ON TABLE events.server_configuration_change IS
    'Observed difference between adjacent complete scans; detected_at is repository detection time, not the SQL change time.';

ALTER TABLE events.server_configuration_change ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.server_configuration_change FORCE ROW LEVEL SECURITY;
CREATE POLICY server_configuration_change_scope
    ON events.server_configuration_change
    USING (instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK (instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY server_configuration_change_migrator
    ON events.server_configuration_change FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
REVOKE ALL ON events.server_configuration_change
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER server_configuration_change_append_only
    BEFORE UPDATE OR DELETE ON events.server_configuration_change
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
