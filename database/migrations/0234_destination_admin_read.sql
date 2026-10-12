-- Bound destination administration reads without exposing configuration references.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE alerting.destination
  ADD COLUMN created_at timestamptz NOT NULL DEFAULT clock_timestamp();

CREATE FUNCTION alerting.list_destinations_for_admin(
    p_target_id uuid, p_listing_started_at timestamptz,
    p_after_destination_id uuid, p_limit integer)
RETURNS TABLE(
    destination_id uuid, kind text, enabled boolean, approved boolean, revision bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, alerting SET row_security = on SET TimeZone = 'UTC'
AS $read$
BEGIN
  IF p_target_id IS NULL OR p_listing_started_at IS NULL OR p_limit IS NULL OR
     p_limit NOT BETWEEN 1 AND 101 THEN
    RAISE EXCEPTION 'invalid destination page request' USING ERRCODE = '22023';
  END IF;
  IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_target_id::text THEN
    RAISE EXCEPTION 'destination target scope mismatch' USING ERRCODE = '42501';
  END IF;
  RETURN QUERY
    SELECT d.destination_id, d.kind, d.enabled, d.approved, d.revision
    FROM alerting.destination AS d
    WHERE d.instance_id = p_target_id AND d.created_at <= p_listing_started_at
      AND (p_after_destination_id IS NULL OR d.destination_id > p_after_destination_id)
    ORDER BY d.destination_id
    LIMIT p_limit;
END $read$;

REVOKE ALL ON FUNCTION alerting.list_destinations_for_admin(uuid,timestamptz,uuid,integer)
  FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.list_destinations_for_admin(uuid,timestamptz,uuid,integer)
  TO sqlobserver_server;
