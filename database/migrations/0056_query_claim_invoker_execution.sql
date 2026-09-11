-- Keep target scope and runtime-role checks while avoiding a SECURITY DEFINER
-- function/configuration switch for each correlated query-ownership check.
-- This helper reads no privileged relation; callers need no elevated identity.
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION control.query_performance_target_claim(p_instance_id uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER AS $$
SELECT p_instance_id::text = pg_catalog.current_setting('sqlobserver.target_scope', true)
 AND (pg_catalog.pg_has_role(current_user,'sqlobserver_collector','member')
   OR pg_catalog.pg_has_role(current_user,'sqlobserver_server','member')
   OR pg_catalog.pg_has_role(current_user,'sqlobserver_migrator','member'));
$$;
