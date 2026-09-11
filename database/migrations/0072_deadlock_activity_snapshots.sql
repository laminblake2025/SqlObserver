-- Preserve one bounded live-activity capture for each recently observed deadlock.
-- Triggered captures are additional evidence; the normal cadence remains minute-deduplicated.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;

ALTER TABLE live_activity.snapshot
    ADD COLUMN deadlock_event_id uuid,
    ADD COLUMN deadlock_occurred_at timestamptz,
    ADD CONSTRAINT ck_live_activity_snapshot_deadlock_metadata CHECK (
        (deadlock_event_id IS NULL AND deadlock_occurred_at IS NULL)
        OR (deadlock_event_id IS NOT NULL AND deadlock_occurred_at IS NOT NULL)
    );

CREATE UNIQUE INDEX uq_live_activity_snapshot_deadlock_event
    ON live_activity.snapshot(target_id, revision, deadlock_event_id)
    WHERE deadlock_event_id IS NOT NULL;
CREATE INDEX ix_live_activity_snapshot_deadlock_event
    ON live_activity.snapshot(target_id, deadlock_event_id)
    WHERE deadlock_event_id IS NOT NULL;

CREATE FUNCTION live_activity.has_deadlock_snapshot(p_target uuid, p_event uuid) RETURNS boolean
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
    SELECT EXISTS (
        SELECT 1
        FROM live_activity.snapshot s
        JOIN control.observation_target t
          ON t.instance_id = s.target_id
         AND t.revision = s.revision
         AND t.lifecycle_state = 'active'
        WHERE s.target_id = p_target
          AND s.deadlock_event_id = p_event
          AND s.observed_at > clock_timestamp() - interval '24 hours'
    );
$$;

CREATE FUNCTION live_activity.commit_triggered_capture(
    p_target uuid,
    p_revision bigint,
    p_owner uuid,
    p_fence bigint,
    p_id uuid,
    p_observed timestamptz,
    p_truncated boolean,
    p_rows jsonb,
    p_payloads jsonb,
    p_databases jsonb,
    p_deadlock_event uuid,
    p_deadlock_occurred timestamptz
) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
DECLARE
    v_previous uuid;
    v_time timestamptz;
    v_seconds numeric;
    v_inserted uuid;
BEGIN
    -- Keep payload retention from racing this additional capture, matching
    -- the shared lock used by the normal cadence commit path.
    PERFORM pg_advisory_xact_lock_shared(731940042::bigint);
    PERFORM control.assert_worker_lease(
        'collector/live-activity-deadlock/' || p_target::text,
        p_owner,
        p_fence);
    PERFORM 1
    FROM control.observation_target
    WHERE instance_id = p_target
      AND revision = p_revision
      AND lifecycle_state = 'active'
    FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target revision unavailable' USING ERRCODE = '22023';
    END IF;

    IF p_deadlock_event IS NULL
       OR p_deadlock_event = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_deadlock_occurred IS NULL
       OR p_deadlock_occurred < clock_timestamp() - interval '33 days'
       OR p_deadlock_occurred > clock_timestamp() + interval '1 day'
       OR p_observed < clock_timestamp() - interval '2 minutes'
       OR p_observed > clock_timestamp() + interval '5 seconds'
       OR COALESCE(jsonb_typeof(p_rows) <> 'array', true)
       OR jsonb_array_length(p_rows) > 512
       OR octet_length(p_rows::text) > 4194304
       OR COALESCE(jsonb_typeof(p_payloads) <> 'array', true)
       OR jsonb_array_length(p_payloads) > 512
       OR octet_length(p_payloads::text) > 1572864
       OR COALESCE(jsonb_typeof(p_databases) <> 'array', true)
       OR jsonb_array_length(p_databases) > 1024
       OR octet_length(p_databases::text) > 524288
    THEN
        RAISE EXCEPTION 'Invalid triggered capture bounds' USING ERRCODE = '22023';
    END IF;

    -- Event-triggered captures may race the cadence writer. Use the most recent
    -- observation that precedes this capture rather than rejecting the event as
    -- out of order.
    SELECT s.id, s.observed_at
    INTO v_previous, v_time
    FROM live_activity.snapshot s
    WHERE s.target_id = p_target
      AND s.revision = p_revision
      AND s.observed_at < p_observed
    ORDER BY s.observed_at DESC
    LIMIT 1;
    v_seconds = CASE WHEN v_time IS NULL THEN 0 ELSE extract(epoch FROM p_observed - v_time) END;

    INSERT INTO live_activity.snapshot(
        id,
        target_id,
        revision,
        observed_at,
        minute_at,
        truncated,
        databases,
        deadlock_event_id,
        deadlock_occurred_at)
    VALUES (
        p_id,
        p_target,
        p_revision,
        p_observed,
        NULL,
        p_truncated,
        p_databases,
        p_deadlock_event,
        p_deadlock_occurred)
    ON CONFLICT DO NOTHING
    RETURNING id INTO v_inserted;
    IF v_inserted IS NULL THEN
        RETURN;
    END IF;

    INSERT INTO live_activity.collection_state(target_id, failed)
    VALUES (p_target, false)
    ON CONFLICT(target_id) DO UPDATE SET failed = false;
    INSERT INTO live_activity.payload(target_id, id, protected)
    SELECT p_target, (j->>'id')::uuid, j->'protected'
    FROM jsonb_array_elements(p_payloads) j
    ON CONFLICT DO NOTHING;
    INSERT INTO live_activity.observation(snapshot_id, identity, data)
    SELECT p_id,
           j->>'identity',
           j || jsonb_build_object(
               'delta',
               CASE
                   WHEN v_seconds > 0
                    AND v_seconds <= 120
                    AND NOT p_truncated
                    AND v_previous IS NOT NULL
                    AND NOT (SELECT truncated FROM live_activity.snapshot WHERE id = v_previous)
                    AND (j->>'cpuMs')::bigint >= (o.data->>'cpuMs')::bigint
                    AND (j->>'reads')::bigint >= (o.data->>'reads')::bigint
                    AND (j->>'writes')::bigint >= (o.data->>'writes')::bigint
                    AND (j->>'logicalReads')::bigint >= (o.data->>'logicalReads')::bigint
                   THEN jsonb_build_object(
                       'seconds', v_seconds,
                       'cpuMs', ((j->>'cpuMs')::bigint - (o.data->>'cpuMs')::bigint)::text,
                       'reads', ((j->>'reads')::bigint - (o.data->>'reads')::bigint)::text,
                       'writes', ((j->>'writes')::bigint - (o.data->>'writes')::bigint)::text,
                       'logicalReads', ((j->>'logicalReads')::bigint - (o.data->>'logicalReads')::bigint)::text
                   )
                   ELSE NULL
               END)
    FROM jsonb_array_elements(p_rows) j
    LEFT JOIN live_activity.observation o
      ON o.snapshot_id = v_previous
     AND o.identity = j->>'identity';
END;
$$;

DROP FUNCTION live_activity.history(uuid, timestamptz, timestamptz);
CREATE FUNCTION live_activity.history(p_target uuid, p_from timestamptz, p_to timestamptz)
RETURNS TABLE(
    id uuid,
    observed_at timestamptz,
    truncated boolean,
    deadlock_event_id uuid,
    deadlock_occurred_at timestamptz)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
    SELECT s.id,
           s.observed_at,
           s.truncated,
           s.deadlock_event_id,
           s.deadlock_occurred_at
    FROM live_activity.snapshot s
    JOIN control.observation_target t
      ON t.instance_id = s.target_id
     AND t.revision = s.revision
     AND t.lifecycle_state = 'active'
    WHERE s.target_id = p_target
      AND (s.minute_at IS NOT NULL OR s.deadlock_event_id IS NOT NULL)
      AND s.observed_at > clock_timestamp() - interval '24 hours'
      AND s.observed_at >= p_from
      AND s.observed_at <= p_to
    ORDER BY s.observed_at
    LIMIT 2048;
$$;

CREATE OR REPLACE FUNCTION live_activity.cleanup() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
DECLARE
    v_created timestamptz;
    v_target uuid;
    v_payload uuid;
BEGIN
    -- One maintenance transaction across collector processes; collection uses
    -- separate target leases. Triggered snapshots remain until their 24-hour
    -- visibility boundary, while anonymous out-of-band rows retain the normal
    -- five-minute cleanup behavior.
    IF NOT pg_try_advisory_xact_lock(731940042::bigint) THEN
        RETURN;
    END IF;
    DELETE FROM live_activity.snapshot
    WHERE id IN (
        SELECT id
        FROM live_activity.snapshot
        WHERE observed_at <= clock_timestamp() - interval '24 hours'
           OR (minute_at IS NULL AND deadlock_event_id IS NULL AND received_at < clock_timestamp() - interval '5 minutes')
        ORDER BY received_at
        LIMIT 120);
    SELECT created_at, target_id, payload_id
    INTO v_created, v_target, v_payload
    FROM live_activity.maintenance_cursor
    WHERE singleton = 1;
    -- Bound candidates examined, not just rows deleted. The persisted cursor
    -- makes progress past still-referenced payloads without rescanning the
    -- entire repository.
    WITH candidates AS MATERIALIZED (
        SELECT q.created_at, q.target_id, q.id
        FROM live_activity.payload q
        WHERE (q.created_at, q.target_id, q.id) > (
                  coalesce(v_created, '-infinity'::timestamptz),
                  coalesce(v_target, '00000000-0000-0000-0000-000000000000'::uuid),
                  coalesce(v_payload, '00000000-0000-0000-0000-000000000000'::uuid))
          AND q.created_at < clock_timestamp() - interval '5 minutes'
        ORDER BY q.created_at, q.target_id, q.id
        LIMIT 8192
    ), removed AS (
        DELETE FROM live_activity.payload p
        USING candidates c
        WHERE p.target_id = c.target_id
          AND p.id = c.id
          AND NOT EXISTS (
              SELECT 1
              FROM live_activity.observation o
              JOIN live_activity.snapshot s ON s.id = o.snapshot_id
              WHERE s.target_id = p.target_id
                AND o.data->>'queryId' = p.id::text)
        RETURNING p.id
    )
    SELECT created_at, target_id, id
    INTO v_created, v_target, v_payload
    FROM candidates
    ORDER BY created_at DESC, target_id DESC, id DESC
    LIMIT 1;
    UPDATE live_activity.maintenance_cursor
    SET created_at = v_created,
        target_id = v_target,
        payload_id = v_payload
    WHERE singleton = 1;
END;
$$;

REVOKE ALL ON FUNCTION live_activity.has_deadlock_snapshot(uuid, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION live_activity.commit_triggered_capture(uuid, bigint, uuid, bigint, uuid, timestamptz, boolean, jsonb, jsonb, jsonb, uuid, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION live_activity.history(uuid, timestamptz, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION live_activity.has_deadlock_snapshot(uuid, uuid),
    live_activity.commit_triggered_capture(uuid, bigint, uuid, bigint, uuid, timestamptz, boolean, jsonb, jsonb, jsonb, uuid, timestamptz)
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION live_activity.history(uuid, timestamptz, timestamptz) TO sqlobserver_server;
