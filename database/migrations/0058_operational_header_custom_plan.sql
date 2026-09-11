-- Preserve latest-observation semantics while avoiding a history-dependent generic
-- plan. Values remain bound parameters; the SQL text and function privileges stay
-- fixed. No collector schedule, target permissions or evidence rows are changed.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION reporting.get_latest_m9_run(p_instance_id uuid,p_collector_id text)
RETURNS TABLE(run_id uuid,target_revision bigint,observed_at timestamptz,state text)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=reporting,telemetry,control,pg_catalog AS $function$
BEGIN
    -- EXECUTE plans for this target and collector on each call. A cached generic
    -- join over all historical runs can exceed the bounded read deadline.
    RETURN QUERY EXECUTE $query$
SELECT r.run_id,r.target_revision,COALESCE(o.completed_at,r.started_at),CASE WHEN replay.m9_observation_state=1 THEN 'Complete' WHEN replay.m9_observation_state=2 THEN 'Partial' WHEN replay.m9_observation_state=3 THEN 'Degraded' WHEN replay.m9_observation_state=4 THEN 'Unsupported' WHEN replay.m9_observation_state=5 THEN 'PermissionDenied' WHEN replay.m9_observation_state=6 THEN 'NoData' WHEN replay.m9_observation_state IS NULL THEN CASE WHEN o.outcome='partial' OR o.truncated THEN 'Partial' WHEN o.outcome='permission_denied' THEN 'PermissionDenied' WHEN o.outcome='unsupported' THEN 'Unsupported' WHEN o.outcome IS NULL THEN 'NoData' ELSE 'Degraded' END ELSE 'Degraded' END FROM telemetry.collection_run r JOIN control.observation_target t ON t.instance_id=r.instance_id LEFT JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id LEFT JOIN telemetry.m9_commit_replay replay ON replay.run_id=r.run_id WHERE $2 IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health') AND $1::text=current_setting('sqlobserver.target_scope',true) AND r.instance_id=$1 AND r.target_revision=t.revision AND r.collector_id=$2 ORDER BY COALESCE(o.completed_at,r.started_at) DESC,r.run_id DESC LIMIT 1
    $query$ USING p_instance_id,p_collector_id;
END;
$function$;
