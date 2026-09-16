-- Filter before ordering/limiting so old derivation jobs cannot hide backfills.
-- Preserve the existing job inventory's target, revision and snapshot fences.
CREATE FUNCTION reporting.list_m10_backfill_jobs(
    p_instance_id uuid, p_target_revision bigint, p_limit integer,
    p_cursor_at timestamptz, p_cursor_job_id uuid, p_snapshot_utc timestamptz)
RETURNS TABLE(job_id uuid,job_kind text,status text,requested_at timestamptz,
    started_at timestamptz,completed_at timestamptz,attempt integer,target_revision bigint)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
    SELECT j.job_id,j.job_kind,j.status,j.requested_at,j.started_at,j.completed_at,j.attempt,j.target_revision
    FROM control.analytics_job j
    WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
      AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision
      AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
      AND j.job_kind='backfill' AND j.requested_at<=p_snapshot_utc
      AND (p_cursor_at IS NULL OR (j.requested_at,j.job_id)>(p_cursor_at,p_cursor_job_id))
    ORDER BY j.requested_at,j.job_id LIMIT least(greatest(p_limit,1),100);
$$;
REVOKE ALL ON FUNCTION reporting.list_m10_backfill_jobs(uuid,bigint,integer,timestamptz,uuid,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_m10_backfill_jobs(uuid,bigint,integer,timestamptz,uuid,timestamptz) TO sqlobserver_server;
