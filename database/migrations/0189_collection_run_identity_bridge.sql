-- Preserve global run-ID uniqueness while collection-run history moves to a
-- partitioned parent. Existing child FKs still target the original run table.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.collection_run_identity (
 run_id uuid PRIMARY KEY,
 started_at timestamptz NOT NULL,
 CONSTRAINT fk_collection_run_identity_v2
  FOREIGN KEY(run_id,started_at)
  REFERENCES telemetry.collection_run_v2(run_id,started_at)
);
CREATE INDEX ix_collection_run_identity_time
 ON telemetry.collection_run_identity(started_at,run_id);
ALTER TABLE telemetry.collection_run_identity ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.collection_run_identity FORCE ROW LEVEL SECURITY;
CREATE POLICY collection_run_identity_migrator
 ON telemetry.collection_run_identity FOR ALL TO sqlobserver_migrator
 USING(true) WITH CHECK(true);
REVOKE ALL ON telemetry.collection_run_identity
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_identity_append_only
 BEFORE UPDATE OR DELETE ON telemetry.collection_run_identity
 FOR EACH STATEMENT
 EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION telemetry.mirror_collection_run_identity()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,telemetry SET TimeZone='UTC'
AS $mirror$
DECLARE
 v_inserted integer;
 v_expected integer;
BEGIN
 -- Ordered key acquisition makes multi-row successor inserts deterministic.
 -- The primary key rejects a reused run ID at a different started_at.
 INSERT INTO telemetry.collection_run_identity(run_id,started_at)
 SELECT row.run_id,row.started_at FROM inserted_rows AS row
 ORDER BY row.run_id;
 GET DIAGNOSTICS v_inserted=ROW_COUNT;
 SELECT count(*) INTO v_expected FROM inserted_rows;
 IF v_inserted<>v_expected THEN
  RAISE EXCEPTION 'collection run identity mirror count differs'
   USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $mirror$;
REVOKE ALL ON FUNCTION telemetry.mirror_collection_run_identity()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER collection_run_identity_mirror
 AFTER INSERT ON telemetry.collection_run_v2
 REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
 EXECUTE FUNCTION telemetry.mirror_collection_run_identity();
