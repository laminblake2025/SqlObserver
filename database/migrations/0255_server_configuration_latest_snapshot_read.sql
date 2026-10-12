-- Read one current-revision complete configuration scan with a run-fenced cursor.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_server_configuration_snapshot(
    p_instance_id uuid,p_limit integer,p_after_configuration_id integer DEFAULT NULL,
    p_expected_revision bigint DEFAULT NULL,p_expected_run_id uuid DEFAULT NULL)
RETURNS TABLE(target_revision bigint,snapshot_run_id uuid,snapshot_observed_at timestamptz,
    server_major_version smallint,collector_enabled boolean,last_outcome text,
    has_more boolean,configuration_id integer,configuration_name text,
    source_type text,configured_value text,in_use_value text,
    is_dynamic boolean,is_advanced boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
DECLARE
    v_revision bigint;
    v_run_id uuid;
    v_observed_at timestamptz;
    v_major smallint;
    v_enabled boolean;
    v_outcome text;
BEGIN
    IF p_instance_id IS NULL OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100
       OR (p_after_configuration_id IS NULL) <> (p_expected_revision IS NULL)
       OR (p_after_configuration_id IS NULL) <> (p_expected_run_id IS NULL)
       OR (p_after_configuration_id IS NOT NULL AND
           (p_after_configuration_id NOT BETWEEN 1 AND 1000000 OR
            p_expected_revision<=0 OR p_expected_run_id='00000000-0000-0000-0000-000000000000'::uuid))
    THEN RAISE EXCEPTION 'Configuration snapshot bounds or cursor are invalid'
        USING ERRCODE='22023'; END IF;
    IF control.query_performance_target_claim(p_instance_id) IS DISTINCT FROM true
    THEN RAISE EXCEPTION 'Configuration snapshot target scope is required'
        USING ERRCODE='42501'; END IF;

    SELECT target.revision INTO v_revision
    FROM control.observation_target AS target
    WHERE target.instance_id=p_instance_id AND target.lifecycle_state='active';
    IF NOT FOUND THEN RAISE EXCEPTION 'Configuration snapshot target is inactive'
        USING ERRCODE='42501'; END IF;
    SELECT schedule.enabled,schedule.last_outcome INTO v_enabled,v_outcome
    FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id=p_instance_id
      AND schedule.collector_id='engine.configurations'
      AND schedule.target_revision=v_revision;
    v_enabled:=coalesce(v_enabled,false);

    -- Repository run order, not the observed SQL Server clock, chooses the scan.
    SELECT run.run_id INTO v_run_id
    FROM telemetry.collection_run_v2 AS run
    JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
    WHERE run.instance_id=p_instance_id AND run.target_revision=v_revision
      AND run.collector_id='engine.configurations' AND outcome.outcome='succeeded'
      AND EXISTS (
          SELECT 1 FROM telemetry.server_configuration_snapshot AS snapshot
          WHERE snapshot.instance_id=p_instance_id AND snapshot.run_id=run.run_id
            AND snapshot.target_revision=v_revision)
    ORDER BY run.started_at DESC,run.run_id DESC LIMIT 1;
    IF v_run_id IS NOT NULL THEN
        SELECT snapshot.observed_at,snapshot.server_major_version
          INTO v_observed_at,v_major
        FROM telemetry.server_configuration_snapshot AS snapshot
        WHERE snapshot.instance_id=p_instance_id AND snapshot.run_id=v_run_id
        ORDER BY snapshot.configuration_id LIMIT 1;
    END IF;
    IF p_expected_revision IS NOT NULL AND
       (p_expected_revision<>v_revision OR p_expected_run_id IS DISTINCT FROM v_run_id)
    THEN RAISE EXCEPTION 'Configuration snapshot cursor is stale'
        USING ERRCODE='40001'; END IF;

    RETURN QUERY
    WITH bounded AS MATERIALIZED (
        SELECT snapshot.configuration_id,snapshot.configuration_name,
               snapshot.source_type,snapshot.configured_value,snapshot.in_use_value,
               snapshot.is_dynamic,snapshot.is_advanced
        FROM telemetry.server_configuration_snapshot AS snapshot
        WHERE snapshot.instance_id=p_instance_id AND snapshot.run_id=v_run_id
          AND snapshot.target_revision=v_revision
          AND (p_after_configuration_id IS NULL OR
               snapshot.configuration_id>p_after_configuration_id)
        ORDER BY snapshot.configuration_id LIMIT p_limit+1
    )
    SELECT v_revision,v_run_id,v_observed_at,v_major,v_enabled,v_outcome,
           EXISTS(SELECT 1 FROM bounded OFFSET p_limit),
           item.configuration_id,item.configuration_name,item.source_type,
           item.configured_value,item.in_use_value,item.is_dynamic,item.is_advanced
    FROM (SELECT 1) AS anchor
    LEFT JOIN LATERAL (
        SELECT * FROM bounded ORDER BY bounded.configuration_id LIMIT p_limit
    ) AS item ON true;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_server_configuration_snapshot(
    uuid,integer,integer,bigint,uuid)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_configuration_snapshot(
    uuid,integer,integer,bigint,uuid) TO sqlobserver_server;
