-- Add bounded availability-replica evidence with explicit visibility to Overview.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION reporting.refresh_overview_latest_core(
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
    database_state text := 'unavailable';
    database_at timestamptz;
    database_run uuid;
    database_exception_rows integer := 0;
    database_issues jsonb := '[]'::jsonb;
    wait_run uuid;
    wait_at timestamptz;
    wait_outcome text;
    wait_loss text;
    wait_summary jsonb;
    wait_incomparable bigint := 0;
    wait_state text := 'unavailable';
    wait_resources jsonb := '[]'::jsonb;
    temp_run uuid;
    temp_revision bigint;
    temp_header_state text;
    temp_observed_at timestamptz;
    temp_snapshot_state smallint;
    temp_total_bytes bigint;
    temp_used_bytes bigint;
    temp_truncated boolean;
    temp_state text := 'unavailable';
    temp_resources jsonb := '[]'::jsonb;
    backup_run uuid;
    backup_revision bigint;
    backup_header_at timestamptz;
    backup_header_state text;
    backup_outcome text;
    backup_truncated boolean;
    backup_loss boolean;
    backup_at timestamptz;
    backup_count integer := 0;
    backup_unknown_time boolean := false;
    backup_damaged_rows integer := 0;
    backup_state text := 'unavailable';
    backup_issues jsonb := '[]'::jsonb;
    backup_resources jsonb := '[]'::jsonb;
    ag_run uuid;
    ag_revision bigint;
    ag_header_at timestamptz;
    ag_header_state text;
    ag_outcome text;
    ag_truncated boolean;
    ag_loss boolean;
    ag_visibility smallint;
    ag_at timestamptz;
    ag_replica_count integer := 0;
    ag_unknown_count integer := 0;
    ag_scope_mismatch boolean := false;
    ag_disconnected_rows integer := 0;
    ag_state text := 'unavailable';
    ag_issues jsonb := '[]'::jsonb;
    ag_resources jsonb := '[]'::jsonb;
    all_issues jsonb;
    bounded_issues jsonb;
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

    SELECT h.health_state, h.last_completed_at, h.latest_data_run_id
      INTO database_state, database_at, database_run
    FROM reporting.collector_health_projection AS h
    WHERE h.instance_id = p_instance_id
      AND h.collector_id = 'database.inventory'
      AND h.schedule_target_revision = p_target_revision;
    database_state := coalesce(database_state, 'unavailable');
    IF database_state = 'current' AND database_run IS NULL THEN
        database_state := 'unavailable';
    END IF;
    IF database_state = 'current' THEN
        SELECT count(*)::integer,
               coalesce(jsonb_agg(jsonb_build_object(
                   'targetId', p_instance_id, 'server', target_name,
                   'title', 'Database state exception',
                   'detail', left(exception.database_name, 256) || ': ' ||
                       CASE exception.state_code
                           WHEN 'recovery_pending' THEN 'RecoveryPending'
                           ELSE initcap(exception.state_code) END,
                   'destination', 'health', 'priority', 0,
                   'observedAtUtc', exception.observed_at)
                   ORDER BY exception.database_id)
                   FILTER (WHERE exception.issue_rank <= 5), '[]'::jsonb)
          INTO database_exception_rows, database_issues
        FROM (
            SELECT snapshot.database_id, snapshot.database_name,
                   snapshot.state_code, snapshot.observed_at,
                   row_number() OVER (ORDER BY snapshot.database_id) AS issue_rank
            FROM telemetry.database_inventory_snapshot AS snapshot
            WHERE snapshot.instance_id = p_instance_id
              AND snapshot.target_revision = p_target_revision
              AND snapshot.collection_run_id = database_run
              AND snapshot.state_code IN
                  ('suspect', 'recovery_pending', 'emergency')
            ORDER BY snapshot.database_id
            LIMIT 6
        ) AS exception;
        IF database_exception_rows > 5 THEN
            database_state := 'partial';
            gaps := gaps || jsonb_build_array('Database state issue list partial');
        END IF;
    ELSE
        gaps := gaps || jsonb_build_array('Database state evidence ' || database_state);
    END IF;

    SELECT e.run_id,
           (result.server_wait_category_summary->>'observedAtUtc')::timestamptz,
           e.outcome, e.loss_kind,
           result.server_wait_category_summary
      INTO wait_run, wait_at, wait_outcome, wait_loss, wait_summary
    FROM reporting.activity_snapshot_evidence AS e
    JOIN telemetry.collection_run_outcome AS result ON result.run_id = e.run_id
    WHERE e.instance_id = p_instance_id
      AND e.target_revision = p_target_revision
      AND e.collector_id = 'waits.server'
    ORDER BY e.completed_at DESC, e.run_id DESC
    LIMIT 1;
    IF wait_run IS NULL OR wait_summary IS NULL
       OR wait_summary->>'version' IS DISTINCT FROM '1'
       OR jsonb_typeof(wait_summary->'categories') IS DISTINCT FROM 'array'
       OR (wait_summary->>'sourceRows')::integer = 0
       OR wait_at IS NULL
       OR wait_summary->>'baselineRunId' IS NULL THEN
        gaps := gaps || jsonb_build_array('Comparable wait evidence unavailable');
    ELSE
        SELECT coalesce(sum((category.value->>'incomparableTypes')::bigint), 0)
          INTO wait_incomparable
        FROM jsonb_array_elements(wait_summary->'categories') AS category(value);
        wait_state := CASE
            WHEN wait_at < clock_timestamp() - interval '2 minutes' THEN 'stale'
            WHEN wait_outcome = 'partial' OR wait_loss <> 'none'
                 OR wait_incomparable > 0 THEN 'partial'
            ELSE 'current' END;
        SELECT coalesce(jsonb_agg(jsonb_build_object(
            'targetId', p_instance_id, 'server', target_name,
            'label', 'Wait · ' || ranked.category,
            'value', (ranked.wait_ms / 1000.0)::double precision,
            'unit', 'seconds since prior sample',
            'state', wait_state,
            'observedAtUtc', wait_at)
            ORDER BY ranked.wait_ms DESC, ranked.category), '[]'::jsonb)
          INTO wait_resources
        FROM (
            SELECT category.value->>'category' AS category,
                   (category.value->>'waitMilliseconds')::numeric AS wait_ms
            FROM jsonb_array_elements(wait_summary->'categories') AS category(value)
            WHERE category.value->>'waitMilliseconds' IS NOT NULL
              AND (category.value->>'comparableTypes')::integer > 0
            ORDER BY wait_ms DESC, category.value->>'category'
            LIMIT 5
        ) AS ranked;
        IF wait_state <> 'current' THEN
            gaps := gaps || jsonb_build_array('Comparable wait evidence ' || wait_state);
        END IF;
    END IF;

    SELECT header.run_id, header.target_revision, header.state
      INTO temp_run, temp_revision, temp_header_state
    FROM reporting.get_latest_m9_run(p_instance_id, 'tempdb.health') AS header;
    IF temp_run IS NOT NULL AND temp_revision = p_target_revision THEN
        SELECT snapshot.observed_at, snapshot.state,
               snapshot.total_bytes, snapshot.used_bytes, snapshot.truncated
          INTO temp_observed_at, temp_snapshot_state,
               temp_total_bytes, temp_used_bytes, temp_truncated
        FROM telemetry.tempdb_snapshot AS snapshot
        WHERE snapshot.instance_id = p_instance_id
          AND snapshot.target_revision = p_target_revision
          AND snapshot.run_id = temp_run
        LIMIT 1;
    END IF;
    temp_state := CASE
        WHEN temp_header_state = 'Unsupported' THEN 'unsupported'
        WHEN temp_header_state = 'PermissionDenied' THEN 'unavailable'
        WHEN temp_snapshot_state IS NULL AND
             temp_header_state IN ('Complete', 'Partial', 'Degraded') THEN 'degraded'
        WHEN temp_snapshot_state IS NULL THEN 'unavailable'
        WHEN temp_header_state = 'Complete' AND temp_snapshot_state = 1 THEN 'current'
        WHEN temp_header_state = 'Partial' AND temp_snapshot_state = 2 THEN 'partial'
        WHEN temp_header_state = 'Degraded' OR temp_snapshot_state = 3 THEN 'degraded'
        ELSE 'unavailable' END;
    IF temp_state = 'current' AND temp_truncated THEN
        temp_state := 'partial';
    END IF;
    IF temp_snapshot_state IS NOT NULL AND
       (temp_total_bytes IS NULL OR temp_total_bytes <= 0 OR
        temp_used_bytes IS NULL OR temp_used_bytes < 0 OR
        temp_used_bytes > temp_total_bytes) AND
       temp_state IN ('current', 'partial') THEN
        temp_state := 'degraded';
    END IF;
    IF temp_observed_at IS NOT NULL AND
       temp_observed_at < clock_timestamp() - interval '2 minutes' AND
       temp_state IN ('current', 'partial', 'degraded') THEN
        temp_state := 'stale';
    END IF;
    IF temp_snapshot_state IS NOT NULL THEN
        temp_resources := jsonb_build_array(jsonb_build_object(
            'targetId', p_instance_id, 'server', target_name,
            'label', 'TempDB used',
            'value', CASE WHEN temp_state IN ('current', 'partial', 'degraded', 'stale') AND
                                temp_total_bytes > 0 AND
                                temp_used_bytes BETWEEN 0 AND temp_total_bytes
                          THEN (100.0 * temp_used_bytes / temp_total_bytes)::double precision
                          ELSE NULL::double precision END,
            'unit', '%', 'state', temp_state,
            'observedAtUtc', temp_observed_at));
    END IF;
    IF temp_state <> 'current' THEN
        gaps := gaps || jsonb_build_array('TempDB evidence ' || temp_state);
    END IF;

    SELECT header.run_id, header.target_revision, header.observed_at,
           header.state
      INTO backup_run, backup_revision, backup_header_at, backup_header_state
    FROM reporting.get_latest_m9_run(p_instance_id, 'backups.status') AS header;
    IF backup_run IS NOT NULL AND backup_revision = p_target_revision THEN
        SELECT result.outcome, result.truncated, result.loss_detected
          INTO backup_outcome, backup_truncated, backup_loss
        FROM telemetry.collection_run_outcome AS result
        WHERE result.run_id = backup_run;
        SELECT count(*)::integer, max(snapshot.observed_at),
               coalesce(bool_or(snapshot.source_time_unknown), false)
          INTO backup_count, backup_at, backup_unknown_time
        FROM telemetry.backup_status_snapshot AS snapshot
        WHERE snapshot.instance_id = p_instance_id
          AND snapshot.target_revision = p_target_revision
          AND snapshot.run_id = backup_run;
    END IF;
    backup_at := coalesce(backup_at, backup_header_at);
    backup_state := CASE
        WHEN backup_header_state = 'Unsupported' THEN 'unsupported'
        WHEN backup_header_state = 'PermissionDenied' THEN 'unavailable'
        WHEN backup_header_state = 'Degraded' THEN 'degraded'
        WHEN backup_run IS NULL OR backup_revision IS DISTINCT FROM p_target_revision
             OR backup_outcome IS NULL OR backup_count = 0 THEN 'unavailable'
        WHEN backup_header_state = 'Partial' OR backup_outcome = 'partial'
             OR backup_truncated OR backup_loss THEN 'partial'
        WHEN backup_header_state = 'Complete' AND backup_outcome = 'succeeded'
             THEN 'current'
        ELSE 'degraded' END;
    IF backup_at IS NOT NULL AND
       backup_at < clock_timestamp() - interval '2 minutes' AND
       backup_state IN ('current', 'partial', 'degraded') THEN
        backup_state := 'stale';
    END IF;
    IF backup_count > 0 AND backup_state IN
       ('current', 'partial', 'degraded', 'stale') THEN
        backup_resources := jsonb_build_array(jsonb_build_object(
            'targetId', p_instance_id, 'server', target_name,
            'label', 'Backup evidence', 'value', backup_count,
            'unit', 'records', 'state', backup_state,
            'observedAtUtc', backup_at));
    END IF;
    IF backup_state IN ('current', 'partial') THEN
        SELECT count(*)::integer,
               coalesce(jsonb_agg(jsonb_build_object(
                   'targetId', p_instance_id, 'server', target_name,
                   'title', 'Damaged backup',
                   'detail', 'Database ' || encode(damaged.database_fingerprint, 'hex') ||
                       ' (' || CASE damaged.backup_kind
                           WHEN 1 THEN 'full' WHEN 2 THEN 'differential'
                           ELSE 'log' END || ')',
                   'destination', 'operations', 'priority', 0,
                   'observedAtUtc', damaged.observed_at)
                   ORDER BY damaged.database_fingerprint, damaged.backup_kind)
                   FILTER (WHERE damaged.issue_rank <= 5), '[]'::jsonb)
          INTO backup_damaged_rows, backup_issues
        FROM (
            SELECT snapshot.database_fingerprint, snapshot.backup_kind,
                   snapshot.observed_at,
                   row_number() OVER (ORDER BY snapshot.database_fingerprint,
                                               snapshot.backup_kind) AS issue_rank
            FROM telemetry.backup_status_snapshot AS snapshot
            WHERE snapshot.instance_id = p_instance_id
              AND snapshot.target_revision = p_target_revision
              AND snapshot.run_id = backup_run
              AND snapshot.is_damaged = true
            ORDER BY snapshot.database_fingerprint, snapshot.backup_kind
            LIMIT 6
        ) AS damaged;
        IF backup_damaged_rows > 5 THEN
            backup_state := 'partial';
            gaps := gaps || jsonb_build_array('Damaged backup issue list partial');
            backup_resources := jsonb_build_array(jsonb_build_object(
                'targetId', p_instance_id, 'server', target_name,
                'label', 'Backup evidence', 'value', backup_count,
                'unit', 'records', 'state', backup_state,
                'observedAtUtc', backup_at));
        END IF;
    END IF;
    IF backup_unknown_time THEN
        gaps := gaps || jsonb_build_array(
            'Backup age unavailable: source timezone unknown');
    END IF;
    IF backup_state <> 'current' THEN
        gaps := gaps || jsonb_build_array('Backup evidence ' || backup_state);
    END IF;

    SELECT header.run_id, header.target_revision, header.observed_at,
           header.state
      INTO ag_run, ag_revision, ag_header_at, ag_header_state
    FROM reporting.get_latest_m9_run(
        p_instance_id, 'availability-groups.health') AS header;
    IF ag_run IS NOT NULL AND ag_revision = p_target_revision THEN
        SELECT result.outcome, result.truncated, result.loss_detected,
               replay.m9_visibility_scope
          INTO ag_outcome, ag_truncated, ag_loss, ag_visibility
        FROM telemetry.collection_run_outcome AS result
        LEFT JOIN telemetry.m9_commit_replay AS replay
          ON replay.run_id = result.run_id
        WHERE result.run_id = ag_run;
        SELECT count(*)::integer, max(replica.observed_at),
               count(*) FILTER (WHERE NOT replica.state_available)::integer,
               coalesce(bool_or(replica.visibility_scope IS DISTINCT FROM
                                ag_visibility), false)
          INTO ag_replica_count, ag_at, ag_unknown_count, ag_scope_mismatch
        FROM telemetry.availability_group_replica_snapshot AS replica
        WHERE replica.instance_id = p_instance_id
          AND replica.target_revision = p_target_revision
          AND replica.run_id = ag_run;
    END IF;
    ag_at := coalesce(ag_at, ag_header_at);
    ag_state := CASE
        WHEN ag_header_state = 'Unsupported' THEN 'unsupported'
        WHEN ag_header_state = 'PermissionDenied' THEN 'unavailable'
        WHEN ag_header_state = 'NoData' THEN 'unavailable'
        WHEN ag_run IS NULL OR ag_revision IS DISTINCT FROM p_target_revision
             OR ag_outcome IS NULL THEN 'unavailable'
        WHEN ag_visibility IS NULL OR ag_header_state = 'Degraded'
             THEN 'degraded'
        WHEN ag_header_state = 'Partial' OR ag_outcome = 'partial'
             OR ag_truncated OR ag_loss OR ag_visibility <> 1
             OR ag_unknown_count > 0 OR ag_scope_mismatch THEN 'partial'
        WHEN ag_header_state = 'Complete' AND ag_outcome = 'succeeded'
             THEN 'current'
        ELSE 'degraded' END;
    IF ag_at IS NOT NULL AND
       ag_at < clock_timestamp() - interval '2 minutes' AND
       ag_state IN ('current', 'partial', 'degraded') THEN
        ag_state := 'stale';
    END IF;
    IF (ag_replica_count > 0 AND ag_state IN
        ('current', 'partial', 'degraded', 'stale')) OR
       (ag_state = 'current' AND ag_visibility = 1) THEN
        ag_resources := jsonb_build_array(jsonb_build_object(
            'targetId', p_instance_id, 'server', target_name,
            'label', 'Availability replicas', 'value', ag_replica_count,
            'unit', 'observed replicas', 'state', ag_state,
            'observedAtUtc', ag_at));
    END IF;
    IF ag_state IN ('current', 'partial') THEN
        SELECT count(*)::integer,
               coalesce(jsonb_agg(jsonb_build_object(
                   'targetId', p_instance_id, 'server', target_name,
                   'title', 'Availability replica disconnected',
                   'detail', encode(disconnected.replica_fingerprint, 'hex'),
                   'destination', 'operations', 'priority', 1,
                   'observedAtUtc', disconnected.observed_at)
                   ORDER BY disconnected.group_fingerprint,
                            disconnected.replica_fingerprint)
                   FILTER (WHERE disconnected.issue_rank <= 5), '[]'::jsonb)
          INTO ag_disconnected_rows, ag_issues
        FROM (
            SELECT replica.group_fingerprint, replica.replica_fingerprint,
                   replica.observed_at,
                   row_number() OVER (ORDER BY replica.group_fingerprint,
                                               replica.replica_fingerprint) AS issue_rank
            FROM telemetry.availability_group_replica_snapshot AS replica
            WHERE replica.instance_id = p_instance_id
              AND replica.target_revision = p_target_revision
              AND replica.run_id = ag_run
              AND replica.state_available
              AND upper(replica.connected_state) = 'DISCONNECTED'
            ORDER BY replica.group_fingerprint, replica.replica_fingerprint
            LIMIT 6
        ) AS disconnected;
        IF ag_disconnected_rows > 5 THEN
            ag_state := 'partial';
            gaps := gaps || jsonb_build_array('Disconnected AG issue list partial');
            ag_resources := jsonb_build_array(jsonb_build_object(
                'targetId', p_instance_id, 'server', target_name,
                'label', 'Availability replicas', 'value', ag_replica_count,
                'unit', 'observed replicas', 'state', ag_state,
                'observedAtUtc', ag_at));
        END IF;
    END IF;
    IF ag_visibility IN (2, 3) THEN
        gaps := gaps || jsonb_build_array('AG visibility is local-only');
    END IF;
    IF ag_unknown_count > 0 OR ag_scope_mismatch THEN
        gaps := gaps || jsonb_build_array('AG replica state incomplete');
    END IF;
    IF ag_state <> 'current' THEN
        gaps := gaps || jsonb_build_array('AG evidence ' || ag_state);
    END IF;
    source_states := jsonb_build_object(
        'schemaVersion', 1,
        'health', jsonb_build_object('state', health_state, 'observedAtUtc', health_at),
        'alerts', jsonb_build_object('state', alert_state, 'observedAtUtc', alert_at),
        'blocking', jsonb_build_object('state', blocking_state, 'observedAtUtc', blocking_at),
        'waits', jsonb_build_object('state', wait_state, 'observedAtUtc', wait_at),
        'databaseStates', jsonb_build_object(
            'state', database_state, 'observedAtUtc', database_at),
        'tempDb', jsonb_build_object('state', temp_state,
                                    'observedAtUtc', temp_observed_at),
        'backups', jsonb_build_object('state', backup_state,
                                     'observedAtUtc', backup_at),
        'availabilityGroups', jsonb_build_object(
            'state', ag_state, 'observedAtUtc', ag_at));
    all_issues := database_issues || backup_issues || alert_issues ||
                  ag_issues || blocking_issue;
    SELECT coalesce(jsonb_agg(bounded.issue ORDER BY bounded.ordinality), '[]'::jsonb)
      INTO bounded_issues
    FROM (
        SELECT candidate.issue, candidate.ordinality
        FROM jsonb_array_elements(all_issues)
             WITH ORDINALITY AS candidate(issue, ordinality)
        ORDER BY candidate.ordinality
        LIMIT 10
    ) AS bounded;
    IF jsonb_array_length(all_issues) > 10 THEN
        gaps := gaps || jsonb_build_array('Overview issue list partial');
    END IF;
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
        bounded_issues,
        core_resources || wait_resources || temp_resources ||
        backup_resources || ag_resources,
        gaps,
        p_owner_execution_id, p_fencing_token);
END $refresh$;
