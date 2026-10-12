-- Let a migration administrator advance historical observation identity across
-- the fleet without holding one long transaction or losing target scope.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE FUNCTION control.backfill_query_observation_identity_fleet(
    p_max_targets integer, p_rows_per_target integer)
RETURNS TABLE(processed_targets integer, updated_rows integer, complete boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, control, system
SET row_security = on AS $fn$
DECLARE
  target_row record;
  prior_scope text := pg_catalog.current_setting('sqlobserver.target_scope', true);
  target_rows integer;
  target_complete boolean;
  visited integer := 0;
  changed integer := 0;
BEGIN
  IF p_max_targets IS NULL OR p_max_targets NOT BETWEEN 1 AND 10 OR
     p_rows_per_target IS NULL OR p_rows_per_target NOT BETWEEN 1 AND 1000 THEN
    RAISE EXCEPTION 'query observation fleet backfill bounds invalid' USING ERRCODE = '22023';
  END IF;
  IF NOT pg_catalog.pg_has_role(session_user,'sqlobserver_migrator','member') THEN
    RAISE EXCEPTION 'query observation fleet backfill requires migrator login'
      USING ERRCODE = '42501';
  END IF;

  FOR target_row IN
    SELECT target.instance_id
    FROM control.observation_target AS target
    LEFT JOIN system.query_observation_identity_backfill AS state
      ON state.instance_id = target.instance_id
    WHERE state.completed_at IS NULL
    ORDER BY target.instance_id
    LIMIT p_max_targets
  LOOP
    PERFORM pg_catalog.set_config(
      'sqlobserver.target_scope', target_row.instance_id::text, true);
    SELECT result.updated_rows, result.complete
      INTO target_rows, target_complete
    FROM control.backfill_query_observation_identity(
      target_row.instance_id, p_rows_per_target) AS result;
    visited := visited + 1;
    changed := changed + target_rows;
  END LOOP;

  PERFORM pg_catalog.set_config('sqlobserver.target_scope',
    coalesce(prior_scope, ''), true);
  RETURN QUERY
    SELECT visited, changed, NOT EXISTS (
      SELECT 1
      FROM control.observation_target AS target
      LEFT JOIN system.query_observation_identity_backfill AS state
        ON state.instance_id = target.instance_id
      WHERE state.completed_at IS NULL);
END;
$fn$;
REVOKE ALL ON FUNCTION control.backfill_query_observation_identity_fleet(integer,integer)
  FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.backfill_query_observation_identity_fleet(integer,integer)
  TO sqlobserver_migrator;
