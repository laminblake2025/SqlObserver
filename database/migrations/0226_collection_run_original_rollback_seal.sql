-- Once every run reader and the writer use v2, irreversibly close the
-- original-run rollback path before retiring any original rows. Installation
-- leaves the seal open and preserves the original mirror.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_original_rollback_seal (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 sealed_at timestamptz,
 sealed_by name,
 CHECK ((sealed_at IS NULL)=(sealed_by IS NULL))
);
INSERT INTO system.collection_run_original_rollback_seal(singleton) VALUES(true);
ALTER TABLE system.collection_run_original_rollback_seal ENABLE ROW LEVEL SECURITY;
ALTER TABLE system.collection_run_original_rollback_seal FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_original_rollback_seal_migrator
 ON system.collection_run_original_rollback_seal FOR ALL
 TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON system.collection_run_original_rollback_seal
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION system.keep_collection_run_original_rollback_seal()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET TimeZone='UTC'
AS $guard$
BEGIN
 IF TG_OP='DELETE' OR OLD.sealed_at IS NOT NULL THEN
  RAISE EXCEPTION 'collection run rollback seal is permanent'
   USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $guard$;
REVOKE ALL ON FUNCTION system.keep_collection_run_original_rollback_seal()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_original_rollback_seal_immutable
 BEFORE UPDATE OR DELETE ON system.collection_run_original_rollback_seal
 FOR EACH ROW EXECUTE FUNCTION system.keep_collection_run_original_rollback_seal();

CREATE FUNCTION system.reject_collection_run_original_rollback()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET TimeZone='UTC'
AS $guard$
BEGIN
 IF EXISTS (SELECT 1 FROM system.collection_run_original_rollback_seal AS seal
            WHERE seal.singleton AND seal.sealed_at IS NOT NULL) THEN
  IF TG_OP='DELETE' THEN
   RAISE EXCEPTION 'original collection run rollback is sealed'
    USING ERRCODE='55000';
  END IF;
  IF NOT NEW.use_v2 THEN
   RAISE EXCEPTION 'original collection run rollback is sealed'
    USING ERRCODE='55000';
  END IF;
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 RETURN NEW;
END $guard$;
REVOKE ALL ON FUNCTION system.reject_collection_run_original_rollback()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_activity_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_health_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_m9_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_m10_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_overview_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_ple_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_query_catalog_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_scheduler_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_sql_volume_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_wait_read_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();
CREATE TRIGGER collection_run_original_rollback_seal
 BEFORE UPDATE OR DELETE ON system.collection_run_v2_writer_route
 FOR EACH ROW EXECUTE FUNCTION system.reject_collection_run_original_rollback();

CREATE FUNCTION control.seal_collection_run_original_rollback(
 p_max_pairs integer DEFAULT 10000)
RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system
SET row_security=on SET TimeZone='UTC' SET lock_timeout='5s'
AS $seal$
DECLARE
 v_route name;
 v_row_count integer;
 v_use_v2 boolean;
 v_sealed_at timestamptz;
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run rollback seal requires migrator'
   USING ERRCODE='42501';
 END IF;
 IF p_max_pairs IS NULL OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'collection run rollback seal pair bound invalid'
   USING ERRCODE='22023';
 END IF;
 -- Each route switch locks its own table before the fleet writer fence.
 -- Acquire all route locks in this same stable order, then fence writers.
 FOR v_route IN SELECT unnest(ARRAY[
  'collection_run_v2_activity_read_route',
  'collection_run_v2_health_read_route',
  'collection_run_v2_m9_read_route',
  'collection_run_v2_m10_read_route',
  'collection_run_v2_overview_read_route',
  'collection_run_v2_ple_read_route',
  'collection_run_v2_query_catalog_read_route',
  'collection_run_v2_scheduler_read_route',
  'collection_run_v2_sql_volume_read_route',
  'collection_run_v2_wait_read_route',
  'collection_run_v2_writer_route']::name[]) LOOP
  EXECUTE format('LOCK TABLE system.%I IN SHARE ROW EXCLUSIVE MODE',v_route);
  EXECUTE format('SELECT count(*),bool_and(use_v2) FROM system.%I',v_route)
   INTO v_row_count,v_use_v2;
  IF v_row_count<>1 OR NOT coalesce(v_use_v2,false) THEN
   RAISE EXCEPTION 'collection run route % is not on v2',v_route
    USING ERRCODE='55000';
  END IF;
  IF NOT EXISTS (
   SELECT 1 FROM pg_trigger AS trigger_row
   WHERE trigger_row.tgrelid=format('system.%I',v_route)::regclass
     AND trigger_row.tgname='collection_run_original_rollback_seal'
     AND trigger_row.tgenabled IN ('O','A')
  ) THEN
   RAISE EXCEPTION 'collection run route % lacks rollback guard',v_route
    USING ERRCODE='55000';
  END IF;
 END LOOP;
 SELECT seal.sealed_at INTO v_sealed_at
 FROM system.collection_run_original_rollback_seal AS seal
 WHERE seal.singleton;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'collection run rollback seal row is missing'
   USING ERRCODE='55000';
 END IF;
 IF v_sealed_at IS NOT NULL THEN RETURN v_sealed_at; END IF;
 -- The cutover verifier locks run writers, validates every reviewed child
 -- FK against the identity bridge, and repeats fleet row parity. Its locks
 -- remain held until this transaction commits the seal.
 IF control.cutover_collection_run_identity_fks(p_max_pairs)<>0 THEN
  RAISE EXCEPTION 'collection run child FK cutover is incomplete'
   USING ERRCODE='55000';
 END IF;
 LOCK TABLE system.collection_run_original_rollback_seal
  IN SHARE ROW EXCLUSIVE MODE;
 SELECT seal.sealed_at INTO v_sealed_at
 FROM system.collection_run_original_rollback_seal AS seal
 WHERE seal.singleton FOR UPDATE;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'collection run rollback seal row is missing'
   USING ERRCODE='55000';
 END IF;
 IF v_sealed_at IS NOT NULL THEN RETURN v_sealed_at; END IF;
 UPDATE system.collection_run_original_rollback_seal AS seal
 SET sealed_at=clock_timestamp(),sealed_by=session_user
 WHERE seal.singleton RETURNING seal.sealed_at INTO v_sealed_at;
 RETURN v_sealed_at;
END $seal$;
REVOKE ALL ON FUNCTION control.seal_collection_run_original_rollback(integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.seal_collection_run_original_rollback(integer)
 TO sqlobserver_migrator;

-- After sealing, new partitioned runs no longer duplicate their row in the
-- original table. A shared row lock serializes this check with seal updates;
-- the seal function fences active writers before it takes its own lock.
CREATE OR REPLACE FUNCTION telemetry.mirror_collection_run_original()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry,system SET TimeZone='UTC'
AS $mirror$
DECLARE v_inserted integer; v_expected integer; v_sealed boolean;
BEGIN
 SELECT seal.sealed_at IS NOT NULL INTO v_sealed
 FROM system.collection_run_original_rollback_seal AS seal
 WHERE seal.singleton FOR SHARE;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'collection run rollback seal row is missing'
   USING ERRCODE='55000';
 END IF;
 IF v_sealed THEN RETURN NULL; END IF;
 IF NOT (SELECT route.use_v2 FROM system.collection_run_v2_writer_route AS route
         WHERE route.singleton) THEN RETURN NULL; END IF;
 INSERT INTO telemetry.collection_run
  (run_id,instance_id,collector_id,collector_version,output_schema_version,
   target_revision,schedule_revision,work_key,owner_execution_id,
   fencing_token,request_digest,scheduled_for,started_at)
 SELECT run_id,instance_id,collector_id,collector_version,output_schema_version,
        target_revision,schedule_revision,work_key,owner_execution_id,
        fencing_token,request_digest,scheduled_for,started_at
 FROM inserted_rows ORDER BY run_id;
 GET DIAGNOSTICS v_inserted=ROW_COUNT;
 SELECT count(*) INTO v_expected FROM inserted_rows;
 IF v_inserted<>v_expected THEN
  RAISE EXCEPTION 'collection run original mirror count differs'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $mirror$;
