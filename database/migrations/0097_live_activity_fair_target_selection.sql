-- Keep the ten-target batch bounded while rotating uncaptured and oldest targets
-- ahead of servers that were sampled more recently.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE OR REPLACE FUNCTION live_activity.targets() RETURNS SETOF control.observation_target
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
 SELECT t.*
 FROM control.observation_target t
 LEFT JOIN LATERAL (
  SELECT s.observed_at
  FROM live_activity.snapshot s
  WHERE s.target_id=t.instance_id AND s.revision=t.revision
  ORDER BY s.observed_at DESC
  LIMIT 1
 ) latest ON true
 WHERE t.lifecycle_state='active' AND t.host_name IS NOT NULL
 ORDER BY latest.observed_at NULLS FIRST, t.instance_id
 LIMIT 10;
$$;
