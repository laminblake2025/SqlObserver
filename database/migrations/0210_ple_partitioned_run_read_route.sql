-- Route the PLE resource panel together; installation retains original reads.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_ple_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_ple_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_ple_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

ALTER FUNCTION reporting.list_buffer_node_ple_nodes(uuid,timestamptz,timestamptz)
 RENAME TO list_buffer_node_ple_nodes_legacy;
REVOKE ALL ON FUNCTION reporting.list_buffer_node_ple_nodes_legacy(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.list_buffer_node_ple_nodes(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(node_id integer,sample_count bigint,last_observed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_ple_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.list_buffer_node_ple_nodes_v2_shadow(
   p_instance_id,p_from_utc,p_to_utc);
 ELSE
  RETURN QUERY SELECT * FROM reporting.list_buffer_node_ple_nodes_legacy(
   p_instance_id,p_from_utc,p_to_utc);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.list_buffer_node_ple_nodes(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_buffer_node_ple_nodes(uuid,timestamptz,timestamptz)
 TO sqlobserver_server;

ALTER FUNCTION reporting.get_buffer_node_ple_trend(uuid,integer,timestamptz,timestamptz)
 RENAME TO get_buffer_node_ple_trend_legacy;
REVOKE ALL ON FUNCTION reporting.get_buffer_node_ple_trend_legacy(uuid,integer,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.get_buffer_node_ple_trend(
 p_instance_id uuid,p_node_id integer,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(bucket_start_utc timestamptz,sample_count bigint,
 minimum_seconds double precision,average_seconds double precision,
 maximum_seconds double precision)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_ple_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.get_buffer_node_ple_trend_v2_shadow(
   p_instance_id,p_node_id,p_from_utc,p_to_utc);
 ELSE
  RETURN QUERY SELECT * FROM reporting.get_buffer_node_ple_trend_legacy(
   p_instance_id,p_node_id,p_from_utc,p_to_utc);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.get_buffer_node_ple_trend(uuid,integer,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_buffer_node_ple_trend(uuid,integer,timestamptz,timestamptz)
 TO sqlobserver_server;

ALTER FUNCTION reporting.get_buffer_manager_ple_trend(uuid,timestamptz,timestamptz)
 RENAME TO get_buffer_manager_ple_trend_legacy;
REVOKE ALL ON FUNCTION reporting.get_buffer_manager_ple_trend_legacy(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.get_buffer_manager_ple_trend(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(bucket_start_utc timestamptz,sample_count bigint,
 minimum_seconds double precision,average_seconds double precision,
 maximum_seconds double precision)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_ple_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.get_buffer_manager_ple_trend_v2_shadow(
   p_instance_id,p_from_utc,p_to_utc);
 ELSE
  RETURN QUERY SELECT * FROM reporting.get_buffer_manager_ple_trend_legacy(
   p_instance_id,p_from_utc,p_to_utc);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.get_buffer_manager_ple_trend(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_buffer_manager_ple_trend(uuid,timestamptz,timestamptz)
 TO sqlobserver_server;

ALTER FUNCTION reporting.count_buffer_node_ple_gaps(uuid,timestamptz,timestamptz)
 RENAME TO count_buffer_node_ple_gaps_legacy;
REVOKE ALL ON FUNCTION reporting.count_buffer_node_ple_gaps_legacy(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.count_buffer_node_ple_gaps(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS bigint
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_ple_read_route AS route
     WHERE route.singleton) THEN
  RETURN reporting.count_buffer_node_ple_gaps_v2_shadow(
   p_instance_id,p_from_utc,p_to_utc);
 END IF;
 RETURN reporting.count_buffer_node_ple_gaps_legacy(
  p_instance_id,p_from_utc,p_to_utc);
END $route$;
REVOKE ALL ON FUNCTION reporting.count_buffer_node_ple_gaps(uuid,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.count_buffer_node_ple_gaps(uuid,timestamptz,timestamptz)
 TO sqlobserver_server;

-- The verifier retains the writer fence until this route update commits.
CREATE FUNCTION control.set_collection_run_v2_ple_read_route(
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
  RAISE EXCEPTION 'PLE read route bounds are invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'PLE read route requires migrator' USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_ple_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_ple_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'PLE read route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_ple_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_ple_read_route(boolean,integer)
 TO sqlobserver_migrator;
