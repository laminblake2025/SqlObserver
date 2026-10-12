-- Resolve replication gap identity from the guarded collection-run source.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.bind_replication_run_gap() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,telemetry AS $gap$
DECLARE revision bigint;
BEGIN
 IF NEW.collector_id='replication.health' AND NEW.topology_fingerprint IS NULL AND NEW.gap_evidence IS NULL THEN
   SELECT r.target_revision INTO STRICT revision
   FROM telemetry.lock_collection_run_runtime(NEW.run_id) AS r
   WHERE r.instance_id=NEW.instance_id AND r.collector_id=NEW.collector_id;
   NEW.topology_fingerprint:=sha256(convert_to('m10-replication-gap|'||NEW.instance_id::text||'|'||revision::text||'|'||NEW.run_id::text,'UTF8'));
   NEW.gap_evidence:=jsonb_build_object('kind','collection_gap','reason',NEW.reason_code,'targetRevision',revision,'topologyAvailable',false);
 END IF;
 RETURN NEW;
END $gap$;
