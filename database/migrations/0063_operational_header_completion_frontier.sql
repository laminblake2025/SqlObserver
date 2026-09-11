-- Select the newest completed run before considering newer unfinished runs.
-- Bind the completion frontier as a value so the unfinished lookup uses the
-- target/collector/time index instead of sorting the entire retained history.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
CREATE OR REPLACE FUNCTION reporting.get_latest_m9_run(p_instance_id uuid,p_collector_id text)
RETURNS TABLE(run_id uuid,target_revision bigint,observed_at timestamptz,state text)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=reporting,telemetry,control,pg_catalog AS $function$
DECLARE
    completed_id uuid;
    completed_at timestamptz;
    unfinished_id uuid;
    unfinished_at timestamptz;
    selected_id uuid;
BEGIN
    IF p_instance_id IS NULL OR p_collector_id IS NULL
       OR p_collector_id NOT IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
       OR p_instance_id::text IS DISTINCT FROM current_setting('sqlobserver.target_scope',true) THEN
        RETURN;
    END IF;

    EXECUTE $query$
        SELECT r.run_id,o.completed_at
        FROM telemetry.collection_run_outcome o
        JOIN telemetry.collection_run r ON r.run_id=o.run_id
        JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
        WHERE r.instance_id=$1 AND r.collector_id=$2
        ORDER BY o.completed_at DESC,o.run_id DESC LIMIT 1
    $query$ INTO completed_id,completed_at USING p_instance_id,p_collector_id;

    IF completed_id IS NULL THEN
        -- With no completed run in this statement snapshot, every matching run
        -- is unfinished. No anti-join over historical outcomes is necessary.
        EXECUTE $query$
            SELECT r.run_id,r.started_at FROM telemetry.collection_run r
            JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
            WHERE r.instance_id=$1 AND r.collector_id=$2
            ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1
        $query$ INTO unfinished_id,unfinished_at USING p_instance_id,p_collector_id;
    ELSE
        EXECUTE $query$
            SELECT r.run_id,r.started_at FROM telemetry.collection_run r
            JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
            WHERE r.instance_id=$1 AND r.collector_id=$2 AND r.started_at >= $3
              AND NOT EXISTS (SELECT 1 FROM telemetry.collection_run_outcome o WHERE o.run_id=r.run_id)
            ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1
        $query$ INTO unfinished_id,unfinished_at USING p_instance_id,p_collector_id,completed_at;
    END IF;

    selected_id := CASE WHEN completed_id IS NULL THEN unfinished_id
        WHEN unfinished_id IS NULL THEN completed_id
        WHEN (unfinished_at,unfinished_id) > (completed_at,completed_id) THEN unfinished_id
        ELSE completed_id END;
    IF selected_id IS NULL THEN RETURN; END IF;

    RETURN QUERY
    SELECT r.run_id,r.target_revision,COALESCE(o.completed_at,r.started_at),
        CASE WHEN replay.m9_observation_state=1 THEN 'Complete'
             WHEN replay.m9_observation_state=2 THEN 'Partial'
             WHEN replay.m9_observation_state=3 THEN 'Degraded'
             WHEN replay.m9_observation_state=4 THEN 'Unsupported'
             WHEN replay.m9_observation_state=5 THEN 'PermissionDenied'
             WHEN replay.m9_observation_state=6 THEN 'NoData'
             WHEN replay.m9_observation_state IS NULL THEN
                 CASE WHEN o.outcome='partial' OR o.truncated THEN 'Partial'
                      WHEN o.outcome='permission_denied' THEN 'PermissionDenied'
                      WHEN o.outcome='unsupported' THEN 'Unsupported'
                      WHEN o.outcome IS NULL THEN 'NoData' ELSE 'Degraded' END
             ELSE 'Degraded' END
    FROM telemetry.collection_run r
    LEFT JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
    LEFT JOIN telemetry.m9_commit_replay replay ON replay.run_id=r.run_id
    WHERE r.run_id=selected_id;
END;
$function$;
