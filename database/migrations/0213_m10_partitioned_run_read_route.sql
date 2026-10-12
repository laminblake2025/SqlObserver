-- Keep M10 derivation reads on original runs until a fenced fleet cutover.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_m10_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_m10_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_m10_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

ALTER FUNCTION reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)
 RENAME TO read_m10_backfill_page_legacy;
REVOKE ALL ON FUNCTION reporting.read_m10_backfill_page_legacy(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.read_m10_backfill_page(
 p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,
 p_to_utc timestamptz,p_metric_key text,p_cursor text,p_max_rows integer,
 p_max_bytes integer,p_job_id uuid,p_source_catalog_version integer)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,
 dimensions jsonb,next_cursor text,has_more boolean,safe_cursor text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_m10_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.read_m10_backfill_page_v2_shadow(
   p_instance_id,p_target_revision,p_from_utc,p_to_utc,p_metric_key,p_cursor,
   p_max_rows,p_max_bytes,p_job_id,p_source_catalog_version);
 ELSE
  RETURN QUERY SELECT * FROM reporting.read_m10_backfill_page_legacy(
   p_instance_id,p_target_revision,p_from_utc,p_to_utc,p_metric_key,p_cursor,
   p_max_rows,p_max_bytes,p_job_id,p_source_catalog_version);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)
 TO sqlobserver_collector;

ALTER FUNCTION reporting.read_m10_rollup_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)
 RENAME TO read_m10_rollup_derivation_inputs_legacy;
REVOKE ALL ON FUNCTION reporting.read_m10_rollup_derivation_inputs_legacy(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.read_m10_rollup_derivation_inputs(
 p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,
 p_to_utc timestamptz,p_metric_key text,p_limit integer,
 p_snapshot_utc timestamptz,p_rollup_interval text,p_dimension_hash bytea)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,
 dimensions jsonb,reset boolean,complete boolean)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_m10_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.read_m10_rollup_derivation_inputs_v2_shadow(
   p_instance_id,p_target_revision,p_from_utc,p_to_utc,p_metric_key,p_limit,
   p_snapshot_utc,p_rollup_interval,p_dimension_hash);
 ELSE
  RETURN QUERY SELECT * FROM reporting.read_m10_rollup_derivation_inputs_legacy(
   p_instance_id,p_target_revision,p_from_utc,p_to_utc,p_metric_key,p_limit,
   p_snapshot_utc,p_rollup_interval,p_dimension_hash);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.read_m10_rollup_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_m10_rollup_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)
 TO sqlobserver_collector;

-- The verifier retains the writer fence until this route update commits.
CREATE FUNCTION control.set_collection_run_v2_m10_read_route(
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
  RAISE EXCEPTION 'M10 read route bounds are invalid' USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'M10 read route requires migrator' USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_m10_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_m10_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'M10 read route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_m10_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_m10_read_route(boolean,integer)
 TO sqlobserver_migrator;
