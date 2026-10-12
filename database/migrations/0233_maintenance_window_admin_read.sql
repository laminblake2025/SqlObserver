-- Give target administrators a bounded view of configured maintenance windows.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.list_maintenance_for_admin(
    p_target_id uuid, p_listing_started_at timestamptz,
    p_after_starts_at timestamptz, p_after_window_id uuid, p_limit integer)
RETURNS TABLE(
    window_id uuid, starts_at timestamptz, ends_at timestamptz,
    reason text, revision bigint, cancelled_at timestamptz)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, alerting SET row_security = on SET TimeZone = 'UTC'
AS $read$
BEGIN
  IF p_target_id IS NULL OR p_listing_started_at IS NULL OR p_limit IS NULL OR
     p_limit NOT BETWEEN 1 AND 101 OR
     (p_after_starts_at IS NULL) <> (p_after_window_id IS NULL) THEN
    RAISE EXCEPTION 'invalid maintenance window page request' USING ERRCODE = '22023';
  END IF;
  IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_target_id::text THEN
    RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE = '42501';
  END IF;
  RETURN QUERY
    SELECT m.window_id, m.starts_at, m.ends_at, m.reason, m.revision, m.cancelled_at
    FROM alerting.maintenance_window AS m
    WHERE m.instance_id = p_target_id AND m.created_at <= p_listing_started_at
      AND (p_after_starts_at IS NULL OR
           (m.starts_at, m.window_id) < (p_after_starts_at, p_after_window_id))
    ORDER BY m.starts_at DESC, m.window_id DESC
    LIMIT p_limit;
END $read$;

REVOKE ALL ON FUNCTION alerting.list_maintenance_for_admin(uuid,timestamptz,timestamptz,uuid,integer)
  FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.list_maintenance_for_admin(uuid,timestamptz,timestamptz,uuid,integer)
  TO sqlobserver_server;
