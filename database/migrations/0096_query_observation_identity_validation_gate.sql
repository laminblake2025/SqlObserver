-- Enforce target identity for all new observation writes while historical rows
-- are backfilled. Validation is a separate, explicit post-backfill operation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE events.query_performance_observation
  ADD CONSTRAINT ck_query_observation_instance_present
  CHECK (instance_id IS NOT NULL) NOT VALID;
