-- Plan for the actual target and UTC window instead of a generic estimate over
-- retained history. Preserve the existing cutoff, revision and rate safeguards.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
ALTER FUNCTION reporting.overview_workload_history(uuid,bigint,timestamptz,timestamptz,timestamptz)
    SET plan_cache_mode = force_custom_plan;
