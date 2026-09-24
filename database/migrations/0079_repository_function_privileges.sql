-- Transactional forward repair: the runner owns this migration and its ledger
-- write in one transaction. Preserve explicit runtime grants and function bodies.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Migrations 0036 and 0077 did not select the repository owner. Transfer only
-- their known objects while the migration login can still act as their owner.
-- Do this before SET ROLE, as in migration 0020. Corrected installations are
-- no-ops; insufficient authority fails the transaction rather than hiding drift.
DO $repair_owners$
DECLARE
    repository_owner oid := 'sqlobserver_migrator'::regrole;
    revision_table oid := 'control.observation_target_revision_identity'::regclass;
    function_signature text;
    function_oid oid;
BEGIN
    IF (SELECT c.relowner FROM pg_catalog.pg_class c WHERE c.oid=revision_table) <> repository_owner THEN
        ALTER TABLE control.observation_target_revision_identity OWNER TO sqlobserver_migrator;
    END IF;

    FOREACH function_signature IN ARRAY ARRAY[
        'control.capture_observation_target_revision_identity()',
        'reporting.list_m10_backfill_jobs(uuid,bigint,integer,timestamptz,uuid,timestamptz)'
    ]::text[]
    LOOP
        function_oid := function_signature::regprocedure;
        IF (SELECT p.proowner FROM pg_catalog.pg_proc p WHERE p.oid=function_oid) <> repository_owner THEN
            EXECUTE format('ALTER FUNCTION %s OWNER TO sqlobserver_migrator',function_oid::regprocedure);
        END IF;
    END LOOP;
END
$repair_owners$;

SET LOCAL ROLE sqlobserver_migrator;

-- Per-schema default REVOKE cannot subtract PostgreSQL's global PUBLIC
-- EXECUTE default. Close it for future functions created by the migrator,
-- including schemas introduced after the bootstrap migration.
ALTER DEFAULT PRIVILEGES FOR ROLE sqlobserver_migrator
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- Existing functions need their own repair. Remove only PUBLIC, retaining all
-- explicitly approved server/collector/auditor grants. Effective ACL expansion
-- includes NULL proacl, which means default permissions rather than no grants.
-- Do not re-own or alter the dedicated report-expiry function; it already denies
-- PUBLIC and must retain its narrowly privileged sqlobserver_report_expirer owner.
DO $revoke_public_execute$
DECLARE
    application_schemas text[] := ARRAY[
        'control','security','telemetry','events','analytics','alerting',
        'reporting','audit','system','live_activity'
    ];
    function_identity regprocedure;
BEGIN
    FOR function_identity IN
        SELECT p.oid::regprocedure
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname=ANY(application_schemas)
          AND EXISTS (
              SELECT 1
              FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
              WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
        ORDER BY p.oid
    LOOP
        EXECUTE format('REVOKE EXECUTE ON FUNCTION %s FROM PUBLIC',function_identity);
    END LOOP;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
        CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
        WHERE n.nspname=ANY(application_schemas) AND a.grantee=0 AND a.privilege_type='EXECUTE'
    ) THEN
        RAISE EXCEPTION 'Repository routines still grant PUBLIC EXECUTE' USING ERRCODE='55000';
    END IF;
END
$revoke_public_execute$;
