-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $sqlobserver$
BEGIN
    IF current_setting('server_version_num')::integer < 180000
       OR current_setting('server_version_num')::integer >= 190000 THEN
        RAISE EXCEPTION 'SqlObserver requires PostgreSQL 18.x; connected server is %',
            current_setting('server_version');
    END IF;
END
$sqlobserver$;

DO $sqlobserver$
DECLARE
    role_name name;
BEGIN
    FOREACH role_name IN ARRAY ARRAY[
        'sqlobserver_migrator'::name,
        'sqlobserver_server'::name,
        'sqlobserver_collector'::name,
        'sqlobserver_auditor'::name
    ]
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles AS r WHERE r.rolname = role_name) THEN
            EXECUTE format(
                'CREATE ROLE %I WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS',
                role_name
            );
        END IF;

        IF EXISTS
        (
            SELECT 1
            FROM pg_catalog.pg_roles AS existing_role
            WHERE existing_role.rolname = role_name
              AND (
                  existing_role.rolcanlogin
                  OR existing_role.rolsuper
                  OR existing_role.rolcreatedb
                  OR existing_role.rolcreaterole
                  OR existing_role.rolinherit
                  OR existing_role.rolreplication
                  OR existing_role.rolbypassrls
              )
        ) THEN
            RAISE EXCEPTION 'pre-existing SqlObserver role % has unsafe attributes', role_name
                USING ERRCODE = '55000';
        END IF;

        IF EXISTS
        (
            SELECT 1
            FROM pg_catalog.pg_auth_members AS membership
            INNER JOIN pg_catalog.pg_roles AS member_role
                ON member_role.oid = membership.member
            WHERE member_role.rolname = role_name
        ) THEN
            RAISE EXCEPTION 'pre-existing SqlObserver role % is a member of another role', role_name
                USING ERRCODE = '55000';
        END IF;
    END LOOP;
END
$sqlobserver$;

COMMENT ON ROLE sqlobserver_migrator IS
    'NOLOGIN group role that owns SqlObserver repository objects and applies reviewed migrations.';
COMMENT ON ROLE sqlobserver_server IS
    'NOLOGIN group role for bounded SqlObserver server repository reads and append-only audit writes.';
COMMENT ON ROLE sqlobserver_collector IS
    'NOLOGIN group role for SqlObserver ingestion and allowlisted lease/partition functions.';
COMMENT ON ROLE sqlobserver_auditor IS
    'NOLOGIN group role for read-only access to SqlObserver activity audit records.';

GRANT sqlobserver_migrator TO CURRENT_USER;

DO $sqlobserver$
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), current_user);
    EXECUTE format(
        'GRANT CONNECT ON DATABASE %I TO sqlobserver_migrator, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor',
        current_database()
    );
    EXECUTE format(
        'GRANT TEMPORARY ON DATABASE %I TO sqlobserver_collector',
        current_database()
    );
END
$sqlobserver$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA control AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA security AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA telemetry AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA events AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA analytics AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA alerting AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA reporting AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA audit AUTHORIZATION sqlobserver_migrator;
CREATE SCHEMA system AUTHORIZATION sqlobserver_migrator;

SET LOCAL ROLE sqlobserver_migrator;

DO $sqlobserver$
DECLARE
    schema_name name;
BEGIN
    FOREACH schema_name IN ARRAY ARRAY[
        'control'::name,
        'security'::name,
        'telemetry'::name,
        'events'::name,
        'analytics'::name,
        'alerting'::name,
        'reporting'::name,
        'audit'::name,
        'system'::name
    ]
    LOOP
        EXECUTE format('REVOKE ALL ON SCHEMA %I FROM PUBLIC', schema_name);
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE sqlobserver_migrator IN SCHEMA %I REVOKE ALL ON TABLES FROM PUBLIC',
            schema_name
        );
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE sqlobserver_migrator IN SCHEMA %I REVOKE ALL ON SEQUENCES FROM PUBLIC',
            schema_name
        );
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE sqlobserver_migrator IN SCHEMA %I REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC',
            schema_name
        );
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE sqlobserver_migrator IN SCHEMA %I REVOKE USAGE ON TYPES FROM PUBLIC',
            schema_name
        );
    END LOOP;
END
$sqlobserver$;

CREATE TABLE system.schema_migration
(
    migration_number integer PRIMARY KEY,
    migration_name text NOT NULL UNIQUE,
    sha256 text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_by text NOT NULL DEFAULT session_user,
    CONSTRAINT ck_schema_migration_number_positive CHECK (migration_number > 0),
    CONSTRAINT ck_schema_migration_name_safe CHECK (
        migration_name ~ '^[0-9]{4}_[a-z0-9_]+[.]sql$'
    ),
    CONSTRAINT ck_schema_migration_sha256 CHECK (sha256 ~ '^[0-9a-f]{64}$')
);

COMMENT ON TABLE system.schema_migration IS
    'Migration-runner history. The runner inserts only after the matching immutable migration commits.';
COMMENT ON COLUMN system.schema_migration.sha256 IS
    'Lowercase SHA-256 of the exact LF-normalized migration bytes verified before execution.';

REVOKE ALL ON TABLE system.schema_migration FROM PUBLIC;
