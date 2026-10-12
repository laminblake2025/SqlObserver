-- Reuse the current backup aggregate for alert evidence without walking database/sample pages.
-- The alert evaluation lease does not grant server administration or overview publication.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.read_backup_fleet_evidence(
    p_instance uuid, p_revision bigint, p_owner uuid, p_fencing bigint)
RETURNS TABLE(read_at timestamptz, payload jsonb, source_digest text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path = pg_catalog SET row_security = on SET TimeZone = 'UTC'
AS $read$
DECLARE now_utc timestamptz; body jsonb;
BEGIN
    IF p_instance IS NULL OR p_revision IS NULL OR p_revision <= 0
       OR current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_instance::text
    THEN
        RAISE EXCEPTION 'Backup alert target scope is invalid' USING ERRCODE = '42501';
    END IF;
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile', p_owner, p_fencing);
    PERFORM 1 FROM control.observation_target target
        WHERE target.instance_id = p_instance AND target.revision = p_revision
          AND target.lifecycle_state = 'active' FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Backup alert current target revision is required' USING ERRCODE = '42501';
    END IF;
    PERFORM 1 FROM control.collector_schedule schedule
        WHERE schedule.instance_id = p_instance AND schedule.collector_id = 'backups.status' FOR SHARE;
    now_utc := clock_timestamp();
    SELECT cached.payload INTO body FROM reporting.overview_backup_latest cached
        WHERE cached.instance_id = p_instance AND cached.target_revision = p_revision
          AND cached.assessed_at <= now_utc AND now_utc < cached.valid_until
          AND reporting.overview_backup_context_matches(
              p_instance, p_revision, cached.payload->'summary'->'context', now_utc);
    PERFORM control.assert_worker_lease('alerts/evaluation/reconcile', p_owner, p_fencing);
    -- A single row carries repository time even when no valid aggregate exists.
    -- Later queue insertion/commit must revalidate this digest and its live context.
    RETURN QUERY SELECT now_utc, body,
        CASE WHEN body IS NULL THEN NULL::text
             ELSE encode(sha256(convert_to(body::text, 'UTF8')), 'hex') END;
END $read$;
REVOKE ALL ON FUNCTION alerting.read_backup_fleet_evidence(uuid,bigint,uuid,bigint)
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.read_backup_fleet_evidence(uuid,bigint,uuid,bigint)
    TO sqlobserver_collector;
