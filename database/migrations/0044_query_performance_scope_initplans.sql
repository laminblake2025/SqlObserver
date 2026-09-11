-- Keep the same target claim and per-query ownership checks, but evaluate the
-- request-wide claim once as an initplan instead of invoking its SECURITY DEFINER
-- function for every historical row. Empty/unset scope still admits no evidence.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
DO $migration$
DECLARE role_suffix text; role_name text; predicate text;
BEGIN
  FOREACH role_suffix IN ARRAY ARRAY['collector','server','migrator'] LOOP
    role_name := 'sqlobserver_' || role_suffix;
    predicate := $predicate$instance_id = nullif(current_setting('sqlobserver.target_scope',true),'')::uuid
      AND (SELECT control.query_performance_target_claim(nullif(current_setting('sqlobserver.target_scope',true),'')::uuid))$predicate$;
    EXECUTE format('ALTER POLICY %I ON events.query_performance_run USING (%s)',
      CASE WHEN role_suffix='server' THEN 'query_performance_server' ELSE 'query_performance_run_' || role_suffix END, predicate);
    EXECUTE format('ALTER POLICY %I ON events.query_performance_query USING (%s)', 'query_performance_query_' || role_suffix, predicate);
    IF role_suffix <> 'server' THEN
      EXECUTE format('ALTER POLICY %I ON events.query_performance_run WITH CHECK (%s)', 'query_performance_run_' || role_suffix, predicate);
      EXECUTE format('ALTER POLICY %I ON events.query_performance_query WITH CHECK (%s)', 'query_performance_query_' || role_suffix, predicate);
    END IF;
    predicate := $predicate$(SELECT control.query_performance_target_claim(nullif(current_setting('sqlobserver.target_scope',true),'')::uuid))
      AND EXISTS (SELECT 1 FROM events.query_performance_query q
        WHERE q.collection_run_id=query_performance_observation.collection_run_id
          AND q.database_id=query_performance_observation.database_id
          AND q.query_fingerprint=query_performance_observation.query_fingerprint
          AND q.instance_id=nullif(current_setting('sqlobserver.target_scope',true),'')::uuid)$predicate$;
    EXECUTE format('ALTER POLICY %I ON events.query_performance_observation USING (%s)', 'query_performance_observation_' || role_suffix, predicate);
    IF role_suffix <> 'server' THEN
      EXECUTE format('ALTER POLICY %I ON events.query_performance_observation WITH CHECK (%s)', 'query_performance_observation_' || role_suffix, predicate);
    END IF;
  END LOOP;
END;
$migration$;
