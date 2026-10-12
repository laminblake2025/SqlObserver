-- Build a bounded current-evidence row while leaving windowed Overview reads untouched.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE reporting.overview_refresh_attempt
(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    last_attempt_at timestamptz NOT NULL
);
ALTER TABLE reporting.overview_refresh_attempt ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.overview_refresh_attempt FORCE ROW LEVEL SECURITY;
CREATE POLICY overview_refresh_attempt_owner
    ON reporting.overview_refresh_attempt FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
REVOKE ALL ON reporting.overview_refresh_attempt
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

CREATE FUNCTION reporting.list_due_overview_latest_status(
    p_owner_execution_id uuid, p_fencing_token bigint, p_limit integer)
RETURNS TABLE(instance_id uuid, target_revision bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, reporting, control
SET row_security = on SET TimeZone = 'UTC'
AS $due$
BEGIN
    IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 32 THEN
        RAISE EXCEPTION 'overview refresh limit must be between 1 and 32'
            USING ERRCODE = '22023';
    END IF;
    PERFORM control.assert_worker_lease(
        'overview/latest-status', p_owner_execution_id, p_fencing_token);
    RETURN QUERY
        WITH selected AS MATERIALIZED
        (
            SELECT t.instance_id, t.revision
            FROM control.observation_target AS t
            LEFT JOIN reporting.overview_latest_status AS s
              ON s.instance_id = t.instance_id
            LEFT JOIN reporting.overview_refresh_attempt AS a
              ON a.instance_id = t.instance_id
            WHERE t.lifecycle_state = 'active' AND t.host_name IS NOT NULL
              AND (s.instance_id IS NULL OR s.target_revision <> t.revision
                   OR s.published_at < clock_timestamp() - interval '30 seconds')
            ORDER BY a.last_attempt_at NULLS FIRST, s.published_at NULLS FIRST,
                     t.instance_id
            LIMIT p_limit
        ), marked AS
        (
            INSERT INTO reporting.overview_refresh_attempt
                (instance_id, target_revision, last_attempt_at)
            SELECT selected.instance_id, selected.revision, clock_timestamp()
            FROM selected
            WHERE true
            ON CONFLICT ON CONSTRAINT overview_refresh_attempt_pkey DO UPDATE SET
                target_revision = EXCLUDED.target_revision,
                last_attempt_at = EXCLUDED.last_attempt_at
            RETURNING overview_refresh_attempt.instance_id
        )
        SELECT selected.instance_id, selected.revision
        FROM selected JOIN marked ON marked.instance_id = selected.instance_id;
    PERFORM control.assert_worker_lease(
        'overview/latest-status', p_owner_execution_id, p_fencing_token);
END $due$;

CREATE FUNCTION reporting.refresh_overview_latest_core(
    p_instance_id uuid, p_target_revision bigint,
    p_owner_execution_id uuid, p_fencing_token bigint)
RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, reporting, control, alerting, telemetry, events
SET row_security = on SET TimeZone = 'UTC'
AS $refresh$
DECLARE
    target_name text;
    health_state text := 'unavailable';
    health_at timestamptz;
    health_success timestamptz;
    health_run uuid;
    rule_count bigint;
    evaluated_count bigint;
    alert_at timestamptz;
    alert_count integer;
    alert_state text;
    alert_issues jsonb := '[]'::jsonb;
    blocking_run uuid;
    blocking_at timestamptz;
    blocking_outcome text;
    blocking_loss text;
    blocked_count integer;
    blocking_state text;
    blocking_issue jsonb := '[]'::jsonb;
    core_resources jsonb := '[]'::jsonb;
    source_states jsonb;
    gaps jsonb := '[]'::jsonb;
    next_generation bigint;
BEGIN
    IF p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision <= 0
       OR current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_instance_id::text
    THEN
        RAISE EXCEPTION 'overview refresh target scope mismatch'
            USING ERRCODE = '42501';
    END IF;
    PERFORM control.assert_worker_lease(
        'overview/latest-status', p_owner_execution_id, p_fencing_token);
    SELECT t.display_name INTO target_name
    FROM control.observation_target AS t
    WHERE t.instance_id = p_instance_id AND t.revision = p_target_revision
      AND t.lifecycle_state = 'active' AND t.host_name IS NOT NULL;
    IF target_name IS NULL THEN
        RAISE EXCEPTION 'overview refresh target revision changed'
            USING ERRCODE = '40001';
    END IF;

    SELECT h.health_state, h.last_completed_at, h.last_succeeded_at,
           h.latest_data_run_id
      INTO health_state, health_at, health_success, health_run
    FROM reporting.collector_health_projection AS h
    WHERE h.instance_id = p_instance_id AND h.collector_id = 'engine.core'
      AND h.schedule_target_revision = p_target_revision;
    health_state := coalesce(health_state, 'unavailable');
    IF health_state <> 'current' THEN
        gaps := gaps || jsonb_build_array('SQL collection ' || health_state);
    END IF;
    IF health_run IS NOT NULL THEN
        SELECT coalesce(jsonb_agg(jsonb_build_object(
            'targetId', p_instance_id, 'server', target_name,
            'label', metric.label, 'value', metric.value,
            'unit', metric.unit, 'state', health_state,
            'observedAtUtc', metric.observed_at) ORDER BY metric.label), '[]'::jsonb)
          INTO core_resources
        FROM (
            SELECT DISTINCT ON (sample.metric_key)
                   sample.observed_at,
                   CASE sample.metric_key
                     WHEN 'engine.user_connections' THEN 'Connections'
                     WHEN 'engine.process_physical_memory_bytes' THEN 'SQL physical memory'
                     WHEN 'engine.os_available_memory_bytes' THEN 'OS available memory via SQL'
                     WHEN 'engine.memory_grants_pending' THEN 'SQL memory grants pending'
                     ELSE 'SQL runnable tasks' END AS label,
                   CASE WHEN sample.metric_key IN
                       ('engine.process_physical_memory_bytes','engine.os_available_memory_bytes')
                     THEN sample.metric_value / 1073741824.0
                     ELSE sample.metric_value END AS value,
                   CASE WHEN sample.metric_key IN
                       ('engine.process_physical_memory_bytes','engine.os_available_memory_bytes')
                     THEN 'GiB'
                     WHEN sample.metric_key = 'engine.user_connections' THEN 'connections'
                     WHEN sample.metric_key = 'engine.memory_grants_pending' THEN 'grants'
                     ELSE 'tasks' END AS unit
            FROM telemetry.raw_metric_sample AS sample
            WHERE sample.instance_id = p_instance_id
              AND sample.collection_run_id = health_run
              AND sample.metric_key IN
                  ('engine.user_connections','engine.process_physical_memory_bytes',
                   'engine.os_available_memory_bytes','engine.memory_grants_pending',
                   'engine.scheduler_runnable_tasks')
              AND sample.dimensions = '{}'::jsonb
            ORDER BY sample.metric_key, sample.observed_at DESC, sample.sample_id DESC
        ) AS metric;
    END IF;

    SELECT count(*) FILTER (WHERE r.enabled),
           count(s.rule_id) FILTER (WHERE r.enabled),
           max(s.last_observed_at) FILTER (WHERE r.enabled)
      INTO rule_count, evaluated_count, alert_at
    FROM alerting.rule AS r
    LEFT JOIN alerting.rule_state AS s
      ON s.instance_id = r.instance_id AND s.rule_id = r.rule_id
    WHERE r.instance_id = p_instance_id;
    SELECT count(*)::integer INTO alert_count
    FROM reporting.active_alerts AS a WHERE a.target_id = p_instance_id;
    alert_state := CASE
        WHEN rule_count = 0 THEN 'unavailable'
        WHEN evaluated_count < rule_count THEN 'partial'
        WHEN alert_at IS NULL THEN 'unavailable'
        WHEN alert_at < clock_timestamp() - interval '2 minutes' THEN 'stale'
        ELSE 'current' END;
    IF alert_state = 'unavailable' THEN
        alert_count := NULL;
        gaps := gaps || jsonb_build_array('Active alert evidence unavailable');
    ELSIF alert_state <> 'current' THEN
        gaps := gaps || jsonb_build_array('Active alert evidence ' || alert_state);
    END IF;
    SELECT coalesce(jsonb_agg(jsonb_build_object(
        'targetId', p_instance_id, 'server', target_name,
        'title', ranked.rule_name,
        'detail', CASE ranked.state WHEN 3 THEN 'Firing: '
                                      ELSE 'Acknowledged: ' END ||
                  left(coalesce(ranked.reason, 'Threshold or collector condition'), 900),
        'destination', 'alerts', 'priority', 1,
        'observedAtUtc', coalesce(ranked.fired_at, ranked.first_observed_at))
        ORDER BY ranked.fired_at DESC NULLS LAST), '[]'::jsonb)
      INTO alert_issues
    FROM (
        SELECT a.rule_name, a.state, a.reason, a.fired_at, a.first_observed_at
        FROM reporting.active_alerts AS a
        WHERE a.target_id = p_instance_id
        ORDER BY (a.state = 3) DESC, a.first_observed_at
        LIMIT 5
    ) AS ranked;

    SELECT e.run_id, e.completed_at, e.outcome, e.loss_kind
      INTO blocking_run, blocking_at, blocking_outcome, blocking_loss
    FROM reporting.activity_snapshot_evidence AS e
    WHERE e.instance_id = p_instance_id
      AND e.target_revision = p_target_revision
      AND e.collector_id = 'blocking.current'
    ORDER BY e.completed_at DESC, e.run_id DESC
    LIMIT 1;
    IF blocking_run IS NULL THEN
        blocking_state := 'unavailable';
        gaps := gaps || jsonb_build_array('No current blocking snapshot');
    ELSE
        SELECT count(DISTINCT edge.blocked_session_id)::integer
          INTO blocked_count
        FROM events.blocking_edge AS edge
        WHERE edge.instance_id = p_instance_id
          AND edge.target_revision = p_target_revision
          AND edge.collection_run_id = blocking_run;
        blocking_state := CASE
            WHEN blocking_at < clock_timestamp() - interval '2 minutes' THEN 'stale'
            WHEN blocking_outcome = 'partial' OR blocking_loss <> 'none' THEN 'partial'
            ELSE 'current' END;
        IF blocked_count > 0 AND blocking_state <> 'stale' THEN
            blocking_issue := jsonb_build_array(jsonb_build_object(
                'targetId', p_instance_id, 'server', target_name,
                'title', 'Sessions blocked',
                'detail', blocked_count::text || ' distinct blocked sessions observed',
                'destination', 'activity', 'priority', 1,
                'observedAtUtc', blocking_at));
        END IF;
    END IF;
    source_states := jsonb_build_object(
        'schemaVersion', 1,
        'health', jsonb_build_object('state', health_state, 'observedAtUtc', health_at),
        'alerts', jsonb_build_object('state', alert_state, 'observedAtUtc', alert_at),
        'blocking', jsonb_build_object('state', blocking_state, 'observedAtUtc', blocking_at),
        'waits', jsonb_build_object('state', 'unavailable'),
        'databaseStates', jsonb_build_object('state', 'unavailable'),
        'tempDb', jsonb_build_object('state', 'unavailable'),
        'backups', jsonb_build_object('state', 'unavailable'),
        'availabilityGroups', jsonb_build_object('state', 'unavailable'));
    gaps := gaps || jsonb_build_array(
        'Wait, database state, TempDB, backup, and AG latest status are not materialized');
    SELECT CASE WHEN s.target_revision = p_target_revision
                THEN s.generation + 1 ELSE 1 END
      INTO next_generation
    FROM reporting.overview_latest_status AS s
    WHERE s.instance_id = p_instance_id;
    next_generation := coalesce(next_generation, 1);
    RETURN reporting.publish_overview_latest_status(
        p_instance_id, p_target_revision, next_generation,
        health_state, health_success, alert_count, alert_state,
        blocked_count, blocking_state, source_states,
        alert_issues || blocking_issue, core_resources, gaps,
        p_owner_execution_id, p_fencing_token);
END $refresh$;

REVOKE ALL ON FUNCTION reporting.list_due_overview_latest_status(uuid,bigint,integer)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_due_overview_latest_status(uuid,bigint,integer)
    TO sqlobserver_collector;
REVOKE ALL ON FUNCTION reporting.refresh_overview_latest_core(uuid,bigint,uuid,bigint)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.refresh_overview_latest_core(uuid,bigint,uuid,bigint)
    TO sqlobserver_collector;
