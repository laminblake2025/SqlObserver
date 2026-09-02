-- Transactional: the migration runner executes this entire file and its ledger
-- write in one transaction. This append-only repair follows a PostgreSQL 18
-- runtime execution of the fenced report-expiry worker.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

-- SELECT ... FOR UPDATE requires UPDATE as well as SELECT on the locked table,
-- even when the statement never changes a row. The original M12 grant included
-- SELECT and DELETE only. Keep the exact table surface on the dedicated
-- NOLOGIN/BYPASSRLS SECURITY DEFINER owner and do not grant table access to the
-- runtime collector login.
REVOKE ALL ON TABLE reporting.report_run
    FROM sqlobserver_report_expirer;
GRANT SELECT, UPDATE, DELETE ON TABLE reporting.report_run
    TO sqlobserver_report_expirer;

-- Fail the migration if the repaired role boundary is broader or narrower than
-- the fixed expiry function requires.
DO $m23_verify$
BEGIN
    IF NOT has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'SELECT'
    ) OR NOT has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'UPDATE'
    ) OR NOT has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'DELETE'
    ) OR has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'INSERT'
    ) OR has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'TRUNCATE'
    ) OR has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'REFERENCES'
    ) OR has_table_privilege(
        'sqlobserver_report_expirer',
        'reporting.report_run',
        'TRIGGER'
    ) THEN
        RAISE EXCEPTION 'report expiry report-run privilege repair failed'
            USING ERRCODE = '42501';
    END IF;

    IF has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'SELECT')
       OR has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'INSERT')
       OR has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'UPDATE')
       OR has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'DELETE')
       OR has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'TRUNCATE')
       OR has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'REFERENCES')
       OR has_table_privilege('sqlobserver_collector', 'reporting.report_run', 'TRIGGER')
       OR NOT has_function_privilege(
        'sqlobserver_collector',
        'reporting.expire_report_runs(integer,uuid,bigint)',
        'EXECUTE'
    ) OR (
        SELECT pg_catalog.pg_get_userbyid(function.proowner)
        FROM pg_catalog.pg_proc function
        WHERE function.oid =
            'reporting.expire_report_runs(integer,uuid,bigint)'::regprocedure
    ) IS DISTINCT FROM 'sqlobserver_report_expirer' THEN
        RAISE EXCEPTION 'report expiry execution boundary is invalid'
            USING ERRCODE = '42501';
    END IF;
END
$m23_verify$;
