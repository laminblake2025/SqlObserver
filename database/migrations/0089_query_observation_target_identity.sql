-- Add a target key to new query observations before moving read paths or RLS.
-- Historical rows remain nullable until a separately bounded backfill is complete.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE events.query_performance_observation ADD COLUMN instance_id uuid;

CREATE FUNCTION events.set_query_observation_target_identity() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog, events AS $fn$
DECLARE owning_instance_id uuid;
BEGIN
  SELECT q.instance_id INTO owning_instance_id
  FROM events.query_performance_query AS q
  WHERE q.collection_run_id = NEW.collection_run_id
    AND q.database_id = NEW.database_id
    AND q.query_fingerprint = NEW.query_fingerprint;

  IF owning_instance_id IS NULL OR
     (NEW.instance_id IS NOT NULL AND NEW.instance_id <> owning_instance_id) THEN
    RAISE EXCEPTION 'query observation target identity mismatch' USING ERRCODE = '23514';
  END IF;
  NEW.instance_id := owning_instance_id;
  RETURN NEW;
END;
$fn$;
REVOKE ALL ON FUNCTION events.set_query_observation_target_identity() FROM PUBLIC;

CREATE TRIGGER query_observation_target_identity
BEFORE INSERT ON events.query_performance_observation
FOR EACH ROW EXECUTE FUNCTION events.set_query_observation_target_identity();
