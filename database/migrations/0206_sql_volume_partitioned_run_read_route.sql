-- Keep SQL volume reads on original runs until an operator proves fleet parity.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_sql_volume_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_sql_volume_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_sql_volume_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

ALTER FUNCTION reporting.list_sql_volume_snapshot(uuid,uuid,bigint,bytea,integer)
 RENAME TO list_sql_volume_snapshot_legacy;
REVOKE ALL ON FUNCTION reporting.list_sql_volume_snapshot_legacy(uuid,uuid,bigint,bytea,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.list_sql_volume_snapshot(
 p_instance_id uuid,p_snapshot_run_id uuid,p_snapshot_target_revision bigint,
 p_after_volume_key bytea,p_max_results integer)
RETURNS TABLE(
 target_revision bigint,snapshot_run_id uuid,evidence_state text,evidence_reason text,
 completed_at timestamptz,loss_kind text,minimum_lost_items bigint,
 volume_key text,identity_kind text,mapped_file_count integer,
 total_bytes bigint,available_bytes bigint,observed_at timestamptz,
 has_more boolean,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_sql_volume_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.list_sql_volume_snapshot_v2_shadow(
   p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_volume_key,p_max_results);
 ELSE
  RETURN QUERY SELECT * FROM reporting.list_sql_volume_snapshot_legacy(
   p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_volume_key,p_max_results);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.list_sql_volume_snapshot(uuid,uuid,bigint,bytea,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_sql_volume_snapshot(uuid,uuid,bigint,bytea,integer)
 TO sqlobserver_server;

-- The verifier retains the writer fence until this route update commits.
CREATE FUNCTION control.set_collection_run_v2_sql_volume_read_route(
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
  RAISE EXCEPTION 'SQL volume read route bounds are invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'SQL volume read route requires migrator' USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_sql_volume_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_sql_volume_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'SQL volume read route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_sql_volume_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_sql_volume_read_route(boolean,integer)
 TO sqlobserver_migrator;
