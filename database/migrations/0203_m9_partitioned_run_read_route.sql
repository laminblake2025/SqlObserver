-- Stage an operator-gated M9 header route; installation keeps original reads.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_m9_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_m9_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_m9_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

ALTER FUNCTION reporting.get_latest_m9_run(uuid,text)
 RENAME TO get_latest_m9_run_legacy;
REVOKE ALL ON FUNCTION reporting.get_latest_m9_run_legacy(uuid,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.get_latest_m9_run(
 p_instance_id uuid,p_collector_id text)
RETURNS TABLE(run_id uuid,target_revision bigint,observed_at timestamptz,state text)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_m9_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.get_latest_m9_run_v2_shadow(
   p_instance_id,p_collector_id);
 ELSE
  RETURN QUERY SELECT * FROM reporting.get_latest_m9_run_legacy(
   p_instance_id,p_collector_id);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.get_latest_m9_run(uuid,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_latest_m9_run(uuid,text)
 TO sqlobserver_server;

-- Activation repeats the fleet proof while all relevant writers are fenced.
CREATE FUNCTION control.set_collection_run_v2_m9_read_route(
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
  RAISE EXCEPTION 'M9 read route bounds are invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'M9 read route requires migrator' USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_m9_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_m9_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'M9 read route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_m9_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_m9_read_route(boolean,integer)
 TO sqlobserver_migrator;
