-- Route the three production M7 projections through one repository switch.
-- Existing writes remain canonical on the original relation and mirror into
-- v3 atomically; no M7 retention is enabled by this migration.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.query_observation_v3_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
 use_v3 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK (revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.query_observation_v3_read_route(singleton) VALUES(true);
REVOKE ALL ON system.query_observation_v3_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

-- Preserve the proven legacy implementations for immediate rollback. Revoke
-- their old server grants so the public function names are the only route.
ALTER FUNCTION control.get_top_queries_projection(
 uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,bytea,
 bytea,uuid,bigint,bytea,timestamptz)
 RENAME TO get_top_queries_projection_legacy;
ALTER FUNCTION control.get_query_history_projection(
 uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,
 uuid,bytea,bytea,timestamptz)
 RENAME TO get_query_history_projection_legacy;
ALTER FUNCTION control.get_query_plan_metadata(uuid,integer,bytea,bytea)
 RENAME TO get_query_plan_metadata_legacy;
REVOKE ALL ON FUNCTION control.get_top_queries_projection_legacy(
 uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,bytea,
 bytea,uuid,bigint,bytea,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
REVOKE ALL ON FUNCTION control.get_query_history_projection_legacy(
 uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,
 uuid,bytea,bytea,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
REVOKE ALL ON FUNCTION control.get_query_plan_metadata_legacy(
 uuid,integer,bytea,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.get_top_queries_projection(
 p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
 p_metric text,p_limit integer,p_after_database_id integer,
 p_after_interval_end timestamptz,p_after_query bytea,p_after_plan bytea,
 p_after_run_id uuid,p_after_metric bigint,p_after_observation_key bytea,
 p_snapshot_utc timestamptz)
RETURNS TABLE(database_id integer,query_fingerprint bytea,
 plan_fingerprint bytea,interval_start timestamptz,interval_end timestamptz,
 observed_at timestamptz,semantics text,cpu_ms bigint,duration_ms bigint,
 execution_count bigint,logical_reads bigint,writes bigint,rows_processed bigint,
 source events.query_performance_source,source_state text,coverage text,
 freshness boolean,truncated boolean,committed_at timestamptz,
 collection_run_id uuid,observation_key bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control,system
AS $route_top$
BEGIN
 IF (SELECT route.use_v3 FROM system.query_observation_v3_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM control.get_top_queries_projection_v3(
   p_instance_id,p_from_utc,p_to_utc,p_metric,p_limit,p_after_database_id,
   p_after_interval_end,p_after_query,p_after_plan,p_after_run_id,
   p_after_metric,p_after_observation_key,p_snapshot_utc);
 ELSE
  RETURN QUERY SELECT * FROM control.get_top_queries_projection_legacy(
   p_instance_id,p_from_utc,p_to_utc,p_metric,p_limit,p_after_database_id,
   p_after_interval_end,p_after_query,p_after_plan,p_after_run_id,
   p_after_metric,p_after_observation_key,p_snapshot_utc);
 END IF;
END $route_top$;
REVOKE ALL ON FUNCTION control.get_top_queries_projection(
 uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,bytea,
 bytea,uuid,bigint,bytea,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_top_queries_projection(
 uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,bytea,
 bytea,uuid,bigint,bytea,timestamptz) TO sqlobserver_server;

CREATE FUNCTION control.get_query_history_projection(
 p_instance_id uuid,p_database_id integer,p_query_fingerprint bytea,
 p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,
 p_after_interval_end timestamptz,p_after_run_id uuid,p_after_plan bytea,
 p_after_observation_key bytea,p_snapshot_utc timestamptz)
RETURNS TABLE(database_id integer,query_fingerprint bytea,
 plan_fingerprint bytea,interval_start timestamptz,interval_end timestamptz,
 observed_at timestamptz,semantics text,cpu_ms bigint,duration_ms bigint,
 execution_count bigint,logical_reads bigint,writes bigint,rows_processed bigint,
 source events.query_performance_source,source_state text,coverage text,
 freshness boolean,truncated boolean,committed_at timestamptz,
 collection_run_id uuid,observation_key bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control,system
AS $route_history$
BEGIN
 IF (SELECT route.use_v3 FROM system.query_observation_v3_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM control.get_query_history_projection_v3(
   p_instance_id,p_database_id,p_query_fingerprint,p_from_utc,p_to_utc,
   p_limit,p_after_interval_end,p_after_run_id,p_after_plan,
   p_after_observation_key,p_snapshot_utc);
 ELSE
  RETURN QUERY SELECT * FROM control.get_query_history_projection_legacy(
   p_instance_id,p_database_id,p_query_fingerprint,p_from_utc,p_to_utc,
   p_limit,p_after_interval_end,p_after_run_id,p_after_plan,
   p_after_observation_key,p_snapshot_utc);
 END IF;
END $route_history$;
REVOKE ALL ON FUNCTION control.get_query_history_projection(
 uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,
 uuid,bytea,bytea,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_query_history_projection(
 uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,
 uuid,bytea,bytea,timestamptz) TO sqlobserver_server;

CREATE FUNCTION control.get_query_plan_metadata(
 p_instance_id uuid,p_database_id integer,p_query_fingerprint bytea,
 p_plan_fingerprint bytea)
RETURNS TABLE(plan_fingerprint bytea,source events.query_performance_source,
 observed_at timestamptz,coverage text,content_available boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control,system
AS $route_plan$
BEGIN
 IF (SELECT route.use_v3 FROM system.query_observation_v3_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM control.get_query_plan_metadata_v3(
   p_instance_id,p_database_id,p_query_fingerprint,p_plan_fingerprint);
 ELSE
  RETURN QUERY SELECT * FROM control.get_query_plan_metadata_legacy(
   p_instance_id,p_database_id,p_query_fingerprint,p_plan_fingerprint);
 END IF;
END $route_plan$;
REVOKE ALL ON FUNCTION control.get_query_plan_metadata(
 uuid,integer,bytea,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_query_plan_metadata(
 uuid,integer,bytea,bytea) TO sqlobserver_server;

-- Activation is one transaction: block target registration and both kinds
-- of observation insert, then inspect every target under the same locks.
-- Disable remains available while the original relation is fully retained.
CREATE FUNCTION control.set_query_observation_v3_read_route(p_use_v3 boolean)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,events,system SET row_security=on
SET TimeZone='UTC'
AS $cutover$
DECLARE
 v_instance_id uuid;
 v_covered boolean;
 v_prior_scope text:=current_setting('sqlobserver.target_scope',true);
 v_revision bigint;
BEGIN
 IF p_use_v3 IS NULL THEN
  RAISE EXCEPTION 'query observation read route value is required'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'query observation read route requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE control.observation_target IN SHARE MODE;
 LOCK TABLE events.query_performance_observation IN SHARE MODE;
 LOCK TABLE events.query_performance_observation_v3 IN SHARE MODE;
 LOCK TABLE system.query_observation_v3_parity_window IN SHARE MODE;
 IF p_use_v3 THEN
  IF EXISTS (
   SELECT 1 FROM (VALUES
    ('events.query_performance_observation'::regclass,
     'query_observation_target_identity'),
    ('events.query_performance_observation'::regclass,
     'query_performance_observation_append_only'),
    ('events.query_performance_observation'::regclass,
     'query_observation_v3_mirror'),
    ('events.query_performance_observation_v3'::regclass,
     'query_observation_v3_target_identity'),
    ('events.query_performance_observation_v3'::regclass,
     'query_observation_v3_append_only'),
    ('events.query_performance_observation_v3'::regclass,
     'query_observation_v3_generation')
   ) AS required(relation_id,trigger_name)
   LEFT JOIN pg_trigger AS trigger_row
    ON trigger_row.tgrelid=required.relation_id
   AND trigger_row.tgname=required.trigger_name
   AND trigger_row.tgenabled IN ('O','A')
   WHERE trigger_row.oid IS NULL
  ) OR NOT EXISTS (
   SELECT 1 FROM pg_class AS table_row
   WHERE table_row.oid='events.query_performance_observation_v3'::regclass
     AND table_row.relrowsecurity AND table_row.relforcerowsecurity) THEN
   RAISE EXCEPTION 'query observation identity, append-only, mirror, or scope gate is disabled'
    USING ERRCODE='55000';
  END IF;
  FOR v_instance_id IN
   SELECT target.instance_id FROM control.observation_target AS target
   ORDER BY target.instance_id
  LOOP
   PERFORM set_config('sqlobserver.target_scope',v_instance_id::text,true);
   SELECT coverage.all_covered INTO v_covered
   FROM control.query_observation_v3_parity_coverage(v_instance_id)
    AS coverage;
   IF NOT coalesce(v_covered,false) THEN
    RAISE EXCEPTION 'query observation parity incomplete for target %',
     v_instance_id USING ERRCODE='55000';
   END IF;
  END LOOP;
 END IF;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_prior_scope,''),true);
 UPDATE system.query_observation_v3_read_route AS route
 SET use_v3=p_use_v3,
     revision=route.revision+CASE WHEN route.use_v3<>p_use_v3 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v3<>p_use_v3 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v3<>p_use_v3 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'query observation read route is missing'
   USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_query_observation_v3_read_route(boolean)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_query_observation_v3_read_route(boolean)
 TO sqlobserver_migrator;
