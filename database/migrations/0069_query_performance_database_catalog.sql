-- Expose the target's current user-database names to query-performance readers.
-- Query-performance identities remain opaque and the catalog is display metadata
-- only; it is sourced from the same inventory snapshots used by the collector.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE OR REPLACE FUNCTION control.get_query_performance_database_catalog(p_instance_id uuid)
RETURNS TABLE(database_id integer, database_name text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, control, telemetry
AS $fn$
WITH current_target AS
(
    SELECT target.revision
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
      AND target.lifecycle_state = 'active'
), latest_inventory_run AS
(
    SELECT run.run_id
    FROM telemetry.collection_run AS run
    INNER JOIN telemetry.collection_run_outcome AS outcome
        ON outcome.run_id = run.run_id
    INNER JOIN current_target AS target
        ON target.revision = run.target_revision
    WHERE run.instance_id = p_instance_id
      AND run.collector_id = 'database.inventory'
      AND outcome.outcome IN ('succeeded', 'partial')
    ORDER BY outcome.completed_at DESC, run.run_id DESC
    LIMIT 1
)
SELECT snapshot.database_id, snapshot.database_name
FROM telemetry.database_inventory_snapshot AS snapshot
INNER JOIN latest_inventory_run AS latest
    ON latest.run_id = snapshot.collection_run_id
WHERE snapshot.instance_id = p_instance_id
  AND snapshot.database_id > 4
  AND snapshot.state_code = 'online'
  AND snapshot.user_access = 'multi_user'
  AND control.query_performance_target_claim(p_instance_id)
ORDER BY snapshot.database_id
LIMIT 256;
$fn$;

REVOKE ALL ON FUNCTION control.get_query_performance_database_catalog(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.get_query_performance_database_catalog(uuid) TO sqlobserver_server;
