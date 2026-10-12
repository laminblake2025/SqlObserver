-- Stage the target-scoped, revision-fenced latest Overview read model.
-- No route reads this table until a materializer and parity check are deployed.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE reporting.overview_latest_status
(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    generation bigint NOT NULL CHECK (generation > 0),
    published_at timestamptz NOT NULL,
    collection_state text NOT NULL CHECK (collection_state IN
        ('pending','current','degraded','unavailable','unsupported','stale','disabled')),
    last_success_at timestamptz,
    active_alert_count integer CHECK (active_alert_count >= 0),
    active_alert_state text NOT NULL CHECK (active_alert_state IN ('current','partial','stale','unavailable')),
    blocked_session_count integer CHECK (blocked_session_count >= 0),
    blocked_session_state text NOT NULL CHECK (blocked_session_state IN ('current','partial','stale','unavailable')),
    source_states jsonb NOT NULL CHECK
        (jsonb_typeof(source_states) = 'object'
         AND source_states->>'schemaVersion' = '1'
         AND octet_length(source_states::text) <= 16384),
    issues jsonb NOT NULL CHECK
        (CASE WHEN jsonb_typeof(issues) = 'array'
              THEN jsonb_array_length(issues) <= 10 AND octet_length(issues::text) <= 32768
              ELSE false END),
    resources jsonb NOT NULL CHECK
        (CASE WHEN jsonb_typeof(resources) = 'array'
              THEN jsonb_array_length(resources) <= 32 AND octet_length(resources::text) <= 32768
              ELSE false END),
    gaps jsonb NOT NULL CHECK
        (CASE WHEN jsonb_typeof(gaps) = 'array'
              THEN jsonb_array_length(gaps) <= 32 AND octet_length(gaps::text) <= 8192
              ELSE false END)
);

ALTER TABLE reporting.overview_latest_status ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.overview_latest_status FORCE ROW LEVEL SECURITY;
CREATE POLICY overview_latest_status_owner
    ON reporting.overview_latest_status FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
REVOKE ALL ON reporting.overview_latest_status
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

CREATE FUNCTION reporting.publish_overview_latest_status(
    p_instance_id uuid, p_target_revision bigint, p_generation bigint,
    p_collection_state text, p_last_success_at timestamptz,
    p_active_alert_count integer, p_active_alert_state text,
    p_blocked_session_count integer, p_blocked_session_state text,
    p_source_states jsonb, p_issues jsonb, p_resources jsonb, p_gaps jsonb,
    p_owner_execution_id uuid, p_fencing_token bigint)
RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, reporting, control
SET row_security = on SET TimeZone = 'UTC'
AS $publish$
DECLARE applied integer;
BEGIN
    IF p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision <= 0
       OR p_generation IS NULL OR p_generation <= 0
       OR p_collection_state IS NULL OR p_active_alert_state IS NULL
       OR p_blocked_session_state IS NULL OR p_source_states IS NULL
       OR p_issues IS NULL OR p_resources IS NULL OR p_gaps IS NULL
       OR current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_instance_id::text
       OR NOT EXISTS (
           SELECT 1 FROM control.observation_target AS t
           WHERE t.instance_id = p_instance_id AND t.revision = p_target_revision
             AND t.lifecycle_state = 'active')
    THEN
        RAISE EXCEPTION 'overview latest-status target or payload is invalid'
            USING ERRCODE = '22023';
    END IF;
    IF p_collection_state NOT IN
          ('pending','current','degraded','unavailable','unsupported','stale','disabled')
       OR p_active_alert_state NOT IN ('current','partial','stale','unavailable')
       OR p_blocked_session_state NOT IN ('current','partial','stale','unavailable')
       OR p_active_alert_count < 0 OR p_blocked_session_count < 0
       OR jsonb_typeof(p_source_states) IS DISTINCT FROM 'object'
       OR p_source_states->>'schemaVersion' IS DISTINCT FROM '1'
       OR octet_length(p_source_states::text) > 16384
       OR jsonb_typeof(p_issues) IS DISTINCT FROM 'array'
       OR jsonb_typeof(p_resources) IS DISTINCT FROM 'array'
       OR jsonb_typeof(p_gaps) IS DISTINCT FROM 'array'
       OR octet_length(p_issues::text) > 32768
       OR octet_length(p_resources::text) > 32768
       OR octet_length(p_gaps::text) > 8192
       OR (CASE WHEN jsonb_typeof(p_issues) = 'array'
                THEN jsonb_array_length(p_issues) > 10 ELSE true END)
       OR (CASE WHEN jsonb_typeof(p_resources) = 'array'
                THEN jsonb_array_length(p_resources) > 32 ELSE true END)
       OR (CASE WHEN jsonb_typeof(p_gaps) = 'array'
                THEN jsonb_array_length(p_gaps) > 32 ELSE true END)
    THEN
        RAISE EXCEPTION 'overview latest-status payload exceeds its contract'
            USING ERRCODE = '22023';
    END IF;

    PERFORM control.assert_worker_lease(
        'overview/latest-status', p_owner_execution_id, p_fencing_token);
    INSERT INTO reporting.overview_latest_status AS current_status
        (instance_id, target_revision, generation, published_at,
         collection_state, last_success_at, active_alert_count, active_alert_state,
         blocked_session_count, blocked_session_state, source_states, issues, resources, gaps)
    VALUES
        (p_instance_id, p_target_revision, p_generation, clock_timestamp(),
         p_collection_state, p_last_success_at, p_active_alert_count, p_active_alert_state,
         p_blocked_session_count, p_blocked_session_state,
         p_source_states, p_issues, p_resources, p_gaps)
    ON CONFLICT (instance_id) DO UPDATE SET
        target_revision = EXCLUDED.target_revision,
        generation = EXCLUDED.generation,
        published_at = EXCLUDED.published_at,
        collection_state = EXCLUDED.collection_state,
        last_success_at = EXCLUDED.last_success_at,
        active_alert_count = EXCLUDED.active_alert_count,
        active_alert_state = EXCLUDED.active_alert_state,
        blocked_session_count = EXCLUDED.blocked_session_count,
        blocked_session_state = EXCLUDED.blocked_session_state,
        source_states = EXCLUDED.source_states,
        issues = EXCLUDED.issues,
        resources = EXCLUDED.resources,
        gaps = EXCLUDED.gaps
    WHERE (current_status.target_revision, current_status.generation)
        < (EXCLUDED.target_revision, EXCLUDED.generation);
    GET DIAGNOSTICS applied = ROW_COUNT;
    PERFORM control.assert_worker_lease(
        'overview/latest-status', p_owner_execution_id, p_fencing_token);
    RETURN applied = 1;
END $publish$;

CREATE FUNCTION reporting.get_overview_latest_status(
    p_instance_id uuid, p_target_revision bigint)
RETURNS TABLE (
    target_revision bigint, generation bigint, published_at timestamptz,
    collection_state text, last_success_at timestamptz,
    active_alert_count integer, active_alert_state text,
    blocked_session_count integer, blocked_session_state text,
    source_states jsonb, issues jsonb, resources jsonb, gaps jsonb)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, reporting, control
SET row_security = on SET TimeZone = 'UTC'
AS $read$
BEGIN
    IF p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision <= 0
       OR current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_instance_id::text
    THEN
        RAISE EXCEPTION 'overview latest-status target scope mismatch'
            USING ERRCODE = '42501';
    END IF;
    RETURN QUERY
        SELECT s.target_revision, s.generation, s.published_at,
               s.collection_state, s.last_success_at,
               s.active_alert_count, s.active_alert_state,
               s.blocked_session_count, s.blocked_session_state,
               s.source_states, s.issues, s.resources, s.gaps
        FROM reporting.overview_latest_status AS s
        JOIN control.observation_target AS t ON t.instance_id = s.instance_id
        WHERE s.instance_id = p_instance_id
          AND s.target_revision = p_target_revision
          AND t.revision = p_target_revision
          AND t.lifecycle_state = 'active';
END $read$;

REVOKE ALL ON FUNCTION reporting.publish_overview_latest_status(
    uuid,bigint,bigint,text,timestamptz,integer,text,integer,text,
    jsonb,jsonb,jsonb,jsonb,uuid,bigint)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.publish_overview_latest_status(
    uuid,bigint,bigint,text,timestamptz,integer,text,integer,text,
    jsonb,jsonb,jsonb,jsonb,uuid,bigint)
    TO sqlobserver_collector;
REVOKE ALL ON FUNCTION reporting.get_overview_latest_status(uuid,bigint)
    FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_overview_latest_status(uuid,bigint)
    TO sqlobserver_server;
