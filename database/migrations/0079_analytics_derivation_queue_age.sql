-- Add queue timing without changing the established claim return type or lease contract.
-- Existing collectors can continue calling the original projection during a rolling upgrade.
SET LOCAL ROLE sqlobserver_migrator;

CREATE FUNCTION control.claim_m10_derivation_jobs_with_queue_age(
    p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint, p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,
    from_utc timestamptz,to_utc timestamptz,source_cutoff_utc timestamptz,generation bigint,
    metric_key text,horizon_seconds double precision,dimensions_hash bytea,rollup_interval text,
    requested_at timestamptz)
LANGUAGE sql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $$
    SELECT claimed.*,job.requested_at
    FROM control.claim_m10_derivation_jobs(p_work_key,p_owner_execution_id,p_fencing_token,p_limit) claimed
    JOIN control.analytics_job job ON job.job_id=claimed.job_id;
$$;
REVOKE ALL ON FUNCTION control.claim_m10_derivation_jobs_with_queue_age(text,uuid,bigint,integer)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.claim_m10_derivation_jobs_with_queue_age(text,uuid,bigint,integer)
    TO sqlobserver_collector;
