-- Summaries use keyset paging across historical target revisions. A publication
-- fence rejects continuation after a committed change, including backdated saves.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE INDEX saved_incident_target_saved_idx
    ON analytics.saved_incident(instance_id,saved_at DESC,thread_id DESC);

CREATE TABLE control.saved_incident_publication
(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
    revision bigint NOT NULL CHECK(revision>=0)
);
ALTER TABLE control.saved_incident_publication ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.saved_incident_publication FORCE ROW LEVEL SECURITY;
CREATE POLICY saved_incident_publication_owner ON control.saved_incident_publication
    TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON control.saved_incident_publication
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
INSERT INTO control.saved_incident_publication(instance_id,revision)
    SELECT t.instance_id,CASE WHEN EXISTS(SELECT FROM analytics.saved_incident s WHERE s.instance_id=t.instance_id) THEN 1 ELSE 0 END
    FROM control.observation_target t;

CREATE FUNCTION control.initialize_saved_incident_publication()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,control AS $initialize$
BEGIN
    INSERT INTO control.saved_incident_publication(instance_id,revision) VALUES(NEW.instance_id,0);
    RETURN NEW;
END
$initialize$;
CREATE TRIGGER saved_incident_target_publication AFTER INSERT ON control.observation_target
    FOR EACH ROW EXECUTE FUNCTION control.initialize_saved_incident_publication();
REVOKE ALL ON FUNCTION control.initialize_saved_incident_publication()
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.bump_saved_incident_publication()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,control AS $bump$
BEGIN
    INSERT INTO control.saved_incident_publication AS existing(instance_id,revision)
        VALUES(NEW.instance_id,1) ON CONFLICT(instance_id) DO UPDATE SET revision=existing.revision+1;
    RETURN NEW;
END
$bump$;
CREATE TRIGGER saved_incident_publication_insert AFTER INSERT ON analytics.saved_incident
    FOR EACH ROW EXECUTE FUNCTION control.bump_saved_incident_publication();
REVOKE ALL ON FUNCTION control.bump_saved_incident_publication()
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.list_saved_incidents(p_instance_id uuid,p_limit integer,
    p_revision bigint,p_snapshot timestamptz,p_saved_at timestamptz,p_thread_id uuid)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,control,analytics SET TimeZone='UTC' AS $list$
DECLARE
    v_revision bigint;
    v_snapshot timestamptz := coalesce(p_snapshot,statement_timestamp());
    v_result jsonb;
BEGIN
    PERFORM control.resolve_m10_target_revision(p_instance_id,NULL);
    IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 OR v_snapshot>statement_timestamp()
       OR p_revision IS NULL AND (p_snapshot IS NOT NULL OR p_saved_at IS NOT NULL OR p_thread_id IS NOT NULL)
       OR p_revision IS NOT NULL AND (p_revision<1 OR p_snapshot IS NULL OR p_saved_at IS NULL OR p_thread_id IS NULL
             OR p_saved_at>p_snapshot OR p_thread_id='00000000-0000-0000-0000-000000000000'::uuid) THEN
        RAISE EXCEPTION 'Invalid saved incident page bounds' USING ERRCODE='22023';
    END IF;
    -- Lock the fence for this transaction. Concurrent saves may finish after
    -- this page, but they cannot silently alter an accepted continuation.
    SELECT revision INTO v_revision FROM control.saved_incident_publication
        WHERE instance_id=p_instance_id FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Saved incident publication is missing' USING ERRCODE='XX000';
    END IF;
    IF p_revision IS NOT NULL AND p_revision<>v_revision THEN
        RAISE EXCEPTION 'Saved investigations changed; refresh the list' USING ERRCODE='40001';
    END IF;
    WITH candidates AS MATERIALIZED (
        SELECT s.* FROM analytics.saved_incident s
        WHERE s.instance_id=p_instance_id AND s.saved_at<=v_snapshot
          AND (p_saved_at IS NULL OR (s.saved_at,s.thread_id)<(p_saved_at,p_thread_id))
        ORDER BY s.saved_at DESC,s.thread_id DESC LIMIT p_limit+1
    ), items AS (
        SELECT * FROM candidates ORDER BY saved_at DESC,thread_id DESC LIMIT p_limit
    ) SELECT jsonb_build_object('targetId',p_instance_id,'publicationRevision',v_revision,'snapshotUtc',v_snapshot,
        'hasMore',(SELECT count(*)>p_limit FROM candidates),
        'items',coalesce(jsonb_agg(jsonb_build_object(
            'targetId',s.instance_id,'targetRevision',s.target_revision,'threadId',s.thread_id,
            'fromUtc',s.from_utc,'toUtc',s.to_utc,'savedAtUtc',s.saved_at,'title',s.title,
            'evidenceCount',(SELECT count(*) FROM analytics.saved_incident_evidence e WHERE e.thread_id=s.thread_id),
            'evidenceTruncated',s.evidence_truncated) ORDER BY s.saved_at DESC,s.thread_id DESC),'[]'::jsonb))
        INTO v_result FROM items s;
    RETURN v_result;
END
$list$;
REVOKE ALL ON FUNCTION reporting.list_saved_incidents(uuid,integer,bigint,timestamptz,timestamptz,uuid)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_saved_incidents(uuid,integer,bigint,timestamptz,timestamptz,uuid)
    TO sqlobserver_server;
