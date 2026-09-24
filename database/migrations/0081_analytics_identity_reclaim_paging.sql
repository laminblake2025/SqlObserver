-- Apply with the matching analytics repository port. The runner commits this
-- migration and its ledger entry together; any failed guard rolls back all DDL.
-- Preserve stored baseline history, including legacy unknown hour identities.
-- Recovery is a corrected forward migration; discarded historical hours require
-- a new analytics job/generation, not mutation of immutable replay history.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;

-- Lock before inspecting dependencies so the identity change is atomic with its
-- writer replacement. Do not silently remove deployment-specific dependencies.
LOCK TABLE analytics.metric_baseline IN ACCESS EXCLUSIVE MODE;
DO $baseline_identity_guard$
BEGIN
 IF NOT EXISTS (
   SELECT 1 FROM pg_constraint c
   WHERE c.conrelid='analytics.metric_baseline'::regclass
     AND c.conname='metric_baseline_pkey' AND c.contype='p'
     AND (SELECT array_agg(a.attname::text ORDER BY key_column.ordinality)
          FROM unnest(c.conkey) WITH ORDINALITY AS key_column(attnum,ordinality)
          JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=key_column.attnum)
         = ARRAY['instance_id','target_revision','metric_key','window_start','dimension_hash','generation']::text[]
 ) THEN
   RAISE EXCEPTION 'Unexpected prior baseline identity constraint' USING ERRCODE='55000';
 END IF;
 IF EXISTS (SELECT 1 FROM pg_constraint WHERE contype='f' AND confrelid='analytics.metric_baseline'::regclass)
    OR EXISTS (SELECT 1 FROM pg_class WHERE oid='analytics.metric_baseline'::regclass AND relreplident<>'d')
    OR EXISTS (SELECT 1 FROM pg_publication_rel WHERE prrelid='analytics.metric_baseline'::regclass)
    OR EXISTS (SELECT 1 FROM pg_publication_namespace WHERE pnnspid='analytics'::regnamespace)
    OR EXISTS (SELECT 1 FROM pg_publication WHERE puballtables)
 THEN
   RAISE EXCEPTION 'Unexpected baseline foreign key or publication identity dependency' USING ERRCODE='55000';
 END IF;
END $baseline_identity_guard$;

-- NULLS NOT DISTINCT retains one unknown legacy hour per original identity.
-- The prior key guarantees that existing rows cannot collide under this key.
-- No history UPDATE/DELETE is needed, so the append-only trigger stays enabled.
ALTER TABLE analytics.metric_baseline DROP CONSTRAINT metric_baseline_pkey;
ALTER TABLE analytics.metric_baseline ADD CONSTRAINT uq_metric_baseline_hour_identity
 UNIQUE NULLS NOT DISTINCT (instance_id,target_revision,metric_key,window_start,dimension_hash,generation,hour_of_week);

CREATE OR REPLACE FUNCTION analytics.commit_metric_baselines(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_rows jsonb,p_result_digest bytea) RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $m10_baseline_write$
DECLARE n integer:=0;
BEGIN
 IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_rows)<>'array' OR jsonb_array_length(p_rows)>100000 OR octet_length(p_rows::text)>8388608 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 baseline write bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='baseline' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'M10 baseline job fence conflict' USING ERRCODE='40001'; END IF;
 IF NOT control.record_m10_analytics_replay(p_operation_id,'baseline',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('rows',p_rows)) THEN RETURN 0; END IF;
 INSERT INTO analytics.metric_baseline(instance_id,target_revision,metric_key,hour_of_week,complete_days,window_start,window_end,sample_count,mean,stddev,median,mad,p10,p90,coverage,confidence,lower_bound,upper_bound,visibility_state,generation,dimensions,dimension_hash)
 SELECT p_instance_id,p_target_revision,x->>'metricKey',(x->>'hourOfWeek')::integer,(x->>'completeDays')::integer,(x->>'windowStartUtc')::timestamptz,(x->>'windowEndUtc')::timestamptz,coalesce((x->>'sampleCount')::integer,0),(x->>'mean')::double precision,(x->>'stddev')::double precision,(x->>'median')::double precision,(x->>'mad')::double precision,(x->>'p10')::double precision,(x->>'p90')::double precision,(x->>'coverage')::double precision,(x->>'confidence')::double precision,(x->>'lowerBound')::double precision,(x->>'upperBound')::double precision,coalesce(x->>'visibilityState','complete'),coalesce((x->>'generation')::bigint,1),coalesce(x->'dimensions','{}'::jsonb),coalesce(decode(nullif(x->>'dimensionsSha256',''),'hex'),sha256(convert_to(coalesce(x->'dimensions','{}'::jsonb)::text,'UTF8')))
 FROM jsonb_array_elements(p_rows) x WHERE x->>'windowStartUtc' IS NOT NULL AND x->>'windowEndUtc' IS NOT NULL ON CONFLICT (instance_id,target_revision,metric_key,window_start,dimension_hash,generation,hour_of_week) DO NOTHING;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $m10_baseline_write$;

-- A live lease owns a running job only when its owner and fence also match.
-- A replacement lease must be able to recover the previous owner's work.
CREATE OR REPLACE FUNCTION control.claim_m10_analytics_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,from_utc timestamptz,to_utc timestamptz,metric_key text,cursor text,current_day_utc timestamptz,cursor_source_kind text,cursor_observed_at timestamptz,cursor_source_id uuid,cursor_metric_key text,cursor_dimension_hash bytea,cursor_ordinal integer,cursor_target_id uuid,cursor_target_revision bigint,cursor_day_utc timestamptz,cursor_catalog_version integer)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_claim_jobs$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/backfill' OR p_owner_execution_id IS NULL OR p_fencing_token<=0 OR p_limit NOT BETWEEN 1 AND 2 THEN RAISE EXCEPTION 'analytics backfill claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
   SELECT j.job_id FROM control.analytics_job j
   JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
   WHERE j.job_kind='backfill' AND j.from_utc IS NOT NULL AND j.to_utc IS NOT NULL
     AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
          (SELECT 1 FROM control.worker_lease l
           WHERE l.work_key=j.work_key AND l.owner_execution_id=j.owner_execution_id
             AND l.fencing_token=j.fencing_token AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
   ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
   UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5
   RETURNING j.job_id
 ), claimed AS (
   UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,started_at=clock_timestamp(),attempt=j.attempt+1
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5
   RETURNING j.*
 ) SELECT c.job_id,c.instance_id,c.target_revision,c.from_utc,c.to_utc,c.metric_key,c.cursor,c.current_day_utc,c.cursor_source_kind,c.cursor_observed_at,c.cursor_source_id,c.cursor_metric_key,c.cursor_dimension_hash,c.cursor_ordinal,c.cursor_target_id,c.cursor_target_revision,c.cursor_day_utc,c.cursor_catalog_version FROM claimed c;
END $m10_claim_jobs$;

CREATE OR REPLACE FUNCTION control.claim_m10_derivation_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,from_utc timestamptz,to_utc timestamptz,source_cutoff_utc timestamptz,generation bigint,metric_key text,horizon_seconds double precision,dimensions_hash bytea,rollup_interval text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_claim_derivation_v2$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/derivation' OR p_owner_execution_id IS NULL OR p_fencing_token<1 OR p_limit NOT BETWEEN 1 AND 2 THEN RAISE EXCEPTION 'analytics derivation claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
   SELECT j.job_id FROM control.analytics_job j JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
   WHERE j.work_key='analytics/derivation' AND j.job_kind IN ('rollup','baseline','forecast','evidence','correlation')
     AND j.from_utc IS NOT NULL AND j.to_utc IS NOT NULL
     AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
       (SELECT 1 FROM control.worker_lease l WHERE l.work_key=j.work_key AND l.owner_execution_id=j.owner_execution_id
         AND l.fencing_token=j.fencing_token AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
   ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
   UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5 RETURNING j.job_id
 ), claimed AS (
   UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,started_at=clock_timestamp(),attempt=j.attempt+1
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5 RETURNING j.*
 ) SELECT c.job_id,c.instance_id,c.target_revision,c.job_kind,c.from_utc,c.to_utc,coalesce(c.source_cutoff_utc,c.to_utc),c.generation,c.metric_key,c.horizon_seconds,c.dimensions_hash,c.rollup_interval FROM claimed c;
END $m10_claim_derivation_v2$;

-- The public page remains capped at 100000. One additional internal row is
-- required to determine whether a maximum-size page has a continuation.
CREATE OR REPLACE FUNCTION analytics.list_metric_rollups_scoped
 (p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz,p_rollup_interval text,
  p_after_bucket timestamptz DEFAULT NULL,p_after_metric text DEFAULT NULL,p_after_dimensions bytea DEFAULT NULL,p_after_generation bigint DEFAULT NULL,p_dimension_hash bytea DEFAULT NULL)
RETURNS SETOF analytics.metric_rollup_v2 LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $$
 SELECT r.* FROM analytics.metric_rollup_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.metric_key=p_metric_key AND r.rollup_interval=p_rollup_interval
   AND p_rollup_interval IN ('5m','hour','day') AND r.bucket_start>=p_from_utc AND r.bucket_start<p_to_utc
   AND r.computed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 100001
   AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days'
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND (p_dimension_hash IS NULL OR r.dimension_hash=p_dimension_hash)
   AND ((p_after_bucket IS NULL AND p_after_metric IS NULL AND p_after_dimensions IS NULL AND p_after_generation IS NULL)
        OR (p_after_bucket IS NOT NULL AND p_after_metric IS NOT NULL AND octet_length(p_after_dimensions)=32 AND p_after_generation>0))
   AND (p_after_bucket IS NULL OR (r.bucket_start,r.metric_key,r.dimension_hash,r.generation) >
        (p_after_bucket,coalesce(p_after_metric,''),coalesce(p_after_dimensions,decode('', 'hex')),coalesce(p_after_generation,0)))
 ORDER BY r.bucket_start,r.metric_key,r.dimension_hash,r.generation LIMIT p_limit;
$$;

-- CREATE OR REPLACE preserves existing owners and ACLs. The unused separate
-- rollup compatibility functions retain their existing runtime-role denial.
