-- Incident discovery is metadata only. A publication revision changes with
-- every incident mutation; a continuation must restart after that change.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TimeZone = 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

-- Seed and install the triggers under the same lock so existing or concurrent
-- writers cannot publish an incident without a matching revision.
LOCK TABLE analytics.incident_thread, analytics.incident_generation IN SHARE ROW EXCLUSIVE MODE;

CREATE TABLE control.incident_publication_revision
(
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    publication_revision bigint NOT NULL CHECK (publication_revision > 0),
    PRIMARY KEY (instance_id, target_revision)
);
ALTER TABLE control.incident_publication_revision ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.incident_publication_revision FORCE ROW LEVEL SECURITY;
CREATE POLICY incident_publication_revision_scope ON control.incident_publication_revision
    USING (instance_id::text = current_setting('sqlobserver.target_scope', true))
    WITH CHECK (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY incident_publication_revision_owner ON control.incident_publication_revision
    FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
REVOKE ALL ON TABLE control.incident_publication_revision
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

INSERT INTO control.incident_publication_revision(instance_id, target_revision, publication_revision)
SELECT DISTINCT instance_id, target_revision, 1 FROM analytics.incident_thread;

CREATE FUNCTION control.bump_incident_publication_revision()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, control SET TimeZone = 'UTC' AS $bump$
DECLARE changed record;
BEGIN
    -- UPDATE can move an identity. Invalidate both scopes in a stable order.
    FOR changed IN
        SELECT DISTINCT identity.instance_id, identity.target_revision
        FROM (VALUES
            (CASE WHEN TG_OP IN ('UPDATE', 'DELETE') THEN OLD.instance_id END,
             CASE WHEN TG_OP IN ('UPDATE', 'DELETE') THEN OLD.target_revision END),
            (CASE WHEN TG_OP IN ('INSERT', 'UPDATE') THEN NEW.instance_id END,
             CASE WHEN TG_OP IN ('INSERT', 'UPDATE') THEN NEW.target_revision END)
        ) AS identity(instance_id, target_revision)
        WHERE identity.instance_id IS NOT NULL
        ORDER BY identity.instance_id, identity.target_revision
    LOOP
        INSERT INTO control.incident_publication_revision AS existing
            (instance_id, target_revision, publication_revision)
        VALUES (changed.instance_id, changed.target_revision, 1)
        ON CONFLICT (instance_id, target_revision) DO UPDATE
            SET publication_revision = existing.publication_revision + 1;
    END LOOP;
    RETURN NULL;
END
$bump$;
REVOKE ALL ON FUNCTION control.bump_incident_publication_revision()
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;

CREATE TRIGGER incident_thread_publication_revision
    AFTER INSERT OR UPDATE OR DELETE ON analytics.incident_thread
    FOR EACH ROW EXECUTE FUNCTION control.bump_incident_publication_revision();
CREATE TRIGGER incident_generation_publication_revision
    AFTER INSERT OR UPDATE OR DELETE ON analytics.incident_generation
    FOR EACH ROW EXECUTE FUNCTION control.bump_incident_publication_revision();

CREATE INDEX ix_incident_list_target_revision_time
    ON analytics.incident_thread(instance_id, target_revision, opened_at, thread_id);

CREATE FUNCTION control.read_incident_publication_revision(
    p_instance_id uuid, p_target_revision bigint, p_expected_revision bigint)
RETURNS bigint LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, control SET TimeZone = 'UTC' AS $fence$
DECLARE current_revision bigint;
BEGIN
    IF p_target_revision IS NULL OR p_target_revision < 1
       OR p_expected_revision < 0 THEN
        RAISE EXCEPTION 'Incident publication bounds rejected' USING ERRCODE = '22023';
    END IF;
    PERFORM control.resolve_m10_target_revision(p_instance_id, p_target_revision);
    SELECT publication.publication_revision INTO current_revision
    FROM control.incident_publication_revision publication
    WHERE publication.instance_id = p_instance_id
      AND publication.target_revision = p_target_revision
    FOR SHARE;
    IF NOT FOUND THEN current_revision := 0; END IF;
    IF p_expected_revision IS NOT NULL AND p_expected_revision <> current_revision THEN
        RAISE EXCEPTION 'Incident publication changed; restart incident listing'
            USING ERRCODE = '40001', CONSTRAINT = 'incident_publication_revision_changed';
    END IF;
    RETURN current_revision;
END
$fence$;

CREATE FUNCTION reporting.list_incidents_metadata(
    p_instance_id uuid, p_target_revision bigint,
    p_from_utc timestamptz, p_to_utc timestamptz, p_limit integer,
    p_snapshot_utc timestamptz, p_publication_revision bigint,
    p_cursor_at timestamptz, p_cursor_thread_id uuid)
RETURNS TABLE(thread_id uuid, opened_at timestamptz,
    latest_generation_observed_at timestamptz, generation_count bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, reporting, analytics, control SET TimeZone = 'UTC' AS $list$
DECLARE revision bigint;
BEGIN
    IF p_from_utc IS NULL OR p_to_utc IS NULL OR p_snapshot_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc) OR NOT isfinite(p_snapshot_utc)
       OR p_to_utc <= p_from_utc OR p_to_utc - p_from_utc > interval '31 days'
       OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 101
       OR p_publication_revision IS NULL OR p_publication_revision < 0
       OR (p_cursor_at IS NULL) <> (p_cursor_thread_id IS NULL)
       OR p_cursor_at IS NOT NULL AND
          (NOT isfinite(p_cursor_at) OR p_cursor_at < p_from_utc
           OR p_cursor_at >= p_to_utc OR p_cursor_at > p_snapshot_utc
           OR p_cursor_thread_id = '00000000-0000-0000-0000-000000000000'::uuid) THEN
        RAISE EXCEPTION 'Incident listing bounds rejected' USING ERRCODE = '22023';
    END IF;
    revision := control.read_incident_publication_revision(
        p_instance_id, p_target_revision, p_publication_revision);
    -- No row can be locked for an unpublished target. Return immediately,
    -- even if its first writer commits before the next SQL statement.
    IF revision = 0 THEN RETURN; END IF;

    RETURN QUERY
    WITH page AS MATERIALIZED (
        SELECT incident.thread_id, incident.opened_at
        FROM analytics.incident_thread incident
        WHERE incident.instance_id = p_instance_id
          AND incident.target_revision = p_target_revision
          AND incident.opened_at >= p_from_utc AND incident.opened_at < p_to_utc
          AND incident.opened_at <= p_snapshot_utc
          AND (p_cursor_at IS NULL
               OR (incident.opened_at, incident.thread_id) > (p_cursor_at, p_cursor_thread_id))
        ORDER BY incident.opened_at, incident.thread_id
        LIMIT p_limit
    )
    SELECT page.thread_id, page.opened_at,
           generations.latest_generation_observed_at, generations.generation_count
    FROM page
    CROSS JOIN LATERAL (
        SELECT max(generation.observed_at) AS latest_generation_observed_at,
               count(*) AS generation_count
        FROM analytics.incident_generation generation
        WHERE generation.instance_id = p_instance_id
          AND generation.target_revision = p_target_revision
          AND generation.thread_id = page.thread_id
          AND generation.observed_at <= p_snapshot_utc
    ) generations
    ORDER BY page.opened_at, page.thread_id;
END
$list$;

REVOKE ALL ON FUNCTION control.read_incident_publication_revision(uuid,bigint,bigint),
    reporting.list_incidents_metadata(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,bigint,timestamptz,uuid)
    FROM PUBLIC, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.read_incident_publication_revision(uuid,bigint,bigint),
    reporting.list_incidents_metadata(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,bigint,timestamptz,uuid)
    TO sqlobserver_server;
