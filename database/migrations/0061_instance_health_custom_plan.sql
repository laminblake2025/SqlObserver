-- Keep the existing health evidence and authorization contract. A generic plan
-- can scan historical metrics instead of the selected latest run on a large VM.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
ALTER FUNCTION reporting.get_instance_health(uuid)
    SET plan_cache_mode = force_custom_plan;
