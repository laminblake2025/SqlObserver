-- A missing target-scope setting must fail closed for every caller of the
-- shared claim helper, including SECURITY DEFINER functions using IF NOT.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.query_performance_target_claim(p_instance_id uuid)
RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $fn$
SELECT coalesce(
    p_instance_id::text = pg_catalog.current_setting('sqlobserver.target_scope', true)
    AND (pg_catalog.pg_has_role(current_user,'sqlobserver_collector','member')
      OR pg_catalog.pg_has_role(current_user,'sqlobserver_server','member')
      OR pg_catalog.pg_has_role(current_user,'sqlobserver_migrator','member')),
    false);
$fn$;
