-- Stage a partitioned-run query database catalog behind a default-original route.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.get_query_performance_database_catalog_v2_shadow(p_instance_id uuid)
RETURNS TABLE(database_id integer,database_name text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control,telemetry
AS $shadow$
WITH current_target AS
(
 SELECT target.revision
 FROM control.observation_target AS target
 WHERE target.instance_id=p_instance_id AND target.lifecycle_state='active'
), latest_inventory_run AS
(
 SELECT run.run_id
 FROM telemetry.collection_run_v2 AS run
 INNER JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
 INNER JOIN current_target AS target ON target.revision=run.target_revision
 WHERE run.instance_id=p_instance_id AND run.collector_id='database.inventory'
   AND outcome.outcome IN ('succeeded','partial')
 ORDER BY outcome.completed_at DESC,run.run_id DESC
 LIMIT 1
)
SELECT snapshot.database_id,snapshot.database_name
FROM telemetry.database_inventory_snapshot AS snapshot
INNER JOIN latest_inventory_run AS latest ON latest.run_id=snapshot.collection_run_id
WHERE snapshot.instance_id=p_instance_id AND snapshot.database_id>4
  AND snapshot.state_code='online' AND snapshot.user_access='multi_user'
  AND control.query_performance_target_claim(p_instance_id)
ORDER BY snapshot.database_id
LIMIT 256;
$shadow$;
REVOKE ALL ON FUNCTION control.get_query_performance_database_catalog_v2_shadow(uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_query_performance_database_catalog_v2_shadow(uuid)
 TO sqlobserver_migrator;

CREATE TABLE system.collection_run_v2_query_catalog_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_query_catalog_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_query_catalog_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

ALTER FUNCTION control.get_query_performance_database_catalog(uuid)
 RENAME TO get_query_performance_database_catalog_legacy;
REVOKE ALL ON FUNCTION control.get_query_performance_database_catalog_legacy(uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.get_query_performance_database_catalog(p_instance_id uuid)
RETURNS TABLE(database_id integer,database_name text)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_query_catalog_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM control.get_query_performance_database_catalog_v2_shadow(p_instance_id);
 ELSE
  RETURN QUERY SELECT * FROM control.get_query_performance_database_catalog_legacy(p_instance_id);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION control.get_query_performance_database_catalog(uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_query_performance_database_catalog(uuid)
 TO sqlobserver_server;

-- The verifier retains the writer fence until this route update commits.
CREATE FUNCTION control.set_collection_run_v2_query_catalog_read_route(
 p_use_v2 boolean,p_max_pairs integer DEFAULT 10000)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,system
SET TimeZone='UTC'
AS $cutover$
DECLARE
 v_revision bigint;
BEGIN
 IF p_use_v2 IS NULL OR p_max_pairs IS NULL
    OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'query database catalog route bounds are invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'query database catalog route requires migrator' USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_query_catalog_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_query_catalog_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'query database catalog route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_query_catalog_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_query_catalog_read_route(boolean,integer)
 TO sqlobserver_migrator;
