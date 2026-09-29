-- Expose bounded, target-scoped rule configuration to the server admin UI.
-- The evaluator's enabled-only collector read remains unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.list_rules_for_admin(
    p_target_id uuid, p_listing_started_at timestamptz, p_after_rule_id uuid, p_limit integer)
RETURNS TABLE(
    rule_id uuid, name text, kind integer, metric_id text, comparison integer,
    threshold double precision, hysteresis double precision, confirmation_count integer,
    confirmation_seconds integer, evaluation_seconds integer, clear_confirmation_count integer,
    enabled boolean, revision bigint, updated_at timestamptz)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, alerting SET row_security = on SET TimeZone = 'UTC'
AS $read$
BEGIN
  IF p_target_id IS NULL OR p_listing_started_at IS NULL OR p_limit IS NULL OR
     p_limit NOT BETWEEN 1 AND 101 THEN
    RAISE EXCEPTION 'invalid alert rule page request' USING ERRCODE = '22023';
  END IF;
  IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_target_id::text THEN
    RAISE EXCEPTION 'alert rule target scope mismatch' USING ERRCODE = '42501';
  END IF;
  RETURN QUERY
    SELECT r.rule_id, r.name, r.kind::integer, r.metric_id, r.comparison::integer,
           r.threshold, r.hysteresis, r.confirmation_count,
           EXTRACT(EPOCH FROM r.confirmation_window)::integer,
           EXTRACT(EPOCH FROM r.evaluation_interval)::integer,
           r.clear_confirmation_count, r.enabled, r.revision, r.updated_at
    FROM alerting.rule AS r
    WHERE r.instance_id = p_target_id AND r.created_at <= p_listing_started_at
      AND (p_after_rule_id IS NULL OR r.rule_id > p_after_rule_id)
    ORDER BY r.rule_id
    LIMIT p_limit;
END $read$;

REVOKE ALL ON FUNCTION alerting.list_rules_for_admin(uuid,timestamptz,uuid,integer)
  FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.list_rules_for_admin(uuid,timestamptz,uuid,integer)
  TO sqlobserver_server;
