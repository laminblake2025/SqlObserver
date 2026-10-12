-- Advance catalog-v1 scheduling through missing target work before applying batch bounds.
-- Preserve deterministic job identities, UTC windows, forecast dimensions/horizons,
-- the 128-target/256-dimension caps and the legacy rollup-only return count.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.schedule_m10_derivation_jobs(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control,telemetry SET TimeZone='UTC' AS $m10_schedule_derivation$
DECLARE inserted_count integer:=0; schedule_now timestamptz:=clock_timestamp(); day_end timestamptz:=date_trunc('day',schedule_now);
BEGIN
 IF p_owner_execution_id IS NULL OR p_fencing_token<=0 THEN RAISE EXCEPTION 'analytics derivation schedule bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/derivation',p_owner_execution_id,p_fencing_token);
 WITH targets AS (
   SELECT t.instance_id,t.revision FROM control.observation_target t
   WHERE t.lifecycle_state IN ('pending_discovery','active') AND t.revision>0
     AND (
       EXISTS (
         SELECT 1 FROM (VALUES ('baseline'::text),('evidence'::text),('correlation'::text)) k(job_kind)
         WHERE NOT EXISTS (
           SELECT 1 FROM control.analytics_job j
           WHERE j.job_id=left(encode(sha256(convert_to('m10-derivation|'||t.instance_id::text||'|'||t.revision::text||'|'||k.job_kind||'|'||day_end::text,'UTF8')),'hex'),32)::uuid))
       OR EXISTS (
         SELECT 1 FROM (
           SELECT DISTINCT r.dimension_hash FROM analytics.metric_rollup_v2 r
           WHERE r.instance_id=t.instance_id AND r.target_revision=t.revision
             AND r.metric_key='host.volume.free_bytes' AND r.rollup_interval='day' AND r.visibility_state='complete'
             AND r.truncated=false AND r.value IS NOT NULL
             AND r.bucket_start >= day_end-interval '29 days' AND r.bucket_start < day_end-interval '1 day'
             AND r.bucket_start+interval '1 day' <= day_end-interval '1 day'
           ORDER BY r.dimension_hash LIMIT 256
         ) d WHERE NOT EXISTS (
           SELECT 1 FROM control.analytics_job j
           WHERE j.job_id=left(encode(sha256(convert_to('m10-derivation|'||t.instance_id::text||'|'||t.revision::text||'|forecast|'||encode(d.dimension_hash,'hex')||'|'||day_end::text,'UTF8')),'hex'),32)::uuid))
     ) ORDER BY t.instance_id LIMIT 128
   ), dimension_candidates AS (
     SELECT x.instance_id,x.revision,r.dimension_hash
     FROM targets x JOIN analytics.metric_rollup_v2 r ON r.instance_id=x.instance_id AND r.target_revision=x.revision
     WHERE r.metric_key='host.volume.free_bytes' AND r.rollup_interval='day' AND r.visibility_state='complete'
       AND r.truncated=false AND r.value IS NOT NULL
       AND r.bucket_start >= day_end-interval '29 days' AND r.bucket_start < day_end-interval '1 day'
       AND r.bucket_start+interval '1 day' <= day_end-interval '1 day'
     GROUP BY x.instance_id,x.revision,r.dimension_hash
   ), dimensions AS (
     SELECT instance_id,revision,dimension_hash
     FROM (SELECT c.*,row_number() OVER (PARTITION BY c.instance_id,c.revision ORDER BY c.dimension_hash) AS dimension_ordinal FROM dimension_candidates c) bounded
     WHERE dimension_ordinal<=256
  ), due AS (
    SELECT left(encode(sha256(convert_to('m10-derivation|'||x.instance_id::text||'|'||x.revision::text||'|'||k.job_kind||'|'||day_end::text,'UTF8')),'hex'),32)::uuid job_id,
            x.instance_id,x.revision,k.job_kind,
            CASE WHEN k.job_kind='evidence' THEN day_end-interval '1 day' ELSE day_end-interval '29 days' END from_utc,
            CASE WHEN k.job_kind='evidence' THEN day_end ELSE day_end-interval '1 day' END to_utc,
            CASE WHEN k.job_kind='evidence' THEN day_end ELSE day_end-interval '1 day' END source_cutoff_utc,
           k.metric_key,NULL::bytea AS dimensions_hash
    FROM targets x CROSS JOIN (VALUES
       ('baseline'::text,'host.cpu.percent'::text),
       ('evidence'::text,NULL::text),
       ('correlation'::text,NULL::text)) k(job_kind,metric_key)
    UNION ALL
    SELECT left(encode(sha256(convert_to('m10-derivation|'||d.instance_id::text||'|'||d.revision::text||'|forecast|'||encode(d.dimension_hash,'hex')||'|'||day_end::text,'UTF8')),'hex'),32)::uuid,
           d.instance_id,d.revision,'forecast',day_end-interval '29 days',day_end-interval '1 day',day_end-interval '1 day','host.volume.free_bytes',d.dimension_hash
    FROM dimensions d
  )
   INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,dimensions_hash,horizon_seconds)
   SELECT d.job_id,d.job_kind,d.instance_id,d.revision,d.from_utc,d.to_utc,d.metric_key,d.source_cutoff_utc,1,'queued','analytics/derivation',d.dimensions_hash,CASE WHEN d.job_kind='forecast' THEN 2592000::double precision ELSE NULL END FROM due d
  ON CONFLICT(job_id) DO NOTHING;
  -- Live rollups use the same derivation lane as baselines/forecasts so the
  -- existing worker can claim all three UTC granularities in one bounded pass.
  WITH targets AS (
    SELECT t.instance_id,t.revision FROM control.observation_target t
    WHERE t.lifecycle_state IN ('pending_discovery','active') AND t.revision>0
      AND EXISTS (
        SELECT 1 FROM analytics.metric_catalog c CROSS JOIN control.m10_rollup_due_buckets(schedule_now) b
        WHERE c.enabled AND NOT EXISTS (
          SELECT 1 FROM control.analytics_job j
          WHERE j.job_id=left(encode(sha256(convert_to('m10-rollup|'||t.instance_id::text||'|'||t.revision::text||'|'||c.metric_key||'|'||b.interval_name||'|'||b.bucket_start::text,'UTF8')),'hex'),32)::uuid)
      ) ORDER BY t.instance_id LIMIT 128
  ), buckets AS (
    SELECT b.interval_name,b.bucket_start,b.width
    FROM control.m10_rollup_due_buckets(schedule_now) b
  )
  INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,rollup_interval)
  SELECT left(encode(sha256(convert_to('m10-rollup|'||t.instance_id::text||'|'||t.revision::text||'|'||c.metric_key||'|'||b.interval_name||'|'||b.bucket_start::text,'UTF8')),'hex'),32)::uuid,'rollup',t.instance_id,t.revision,b.bucket_start,b.bucket_start+b.width,c.metric_key,b.bucket_start+b.width,1,'queued','analytics/derivation',b.interval_name
  FROM targets t CROSS JOIN analytics.metric_catalog c CROSS JOIN buckets b WHERE c.enabled
  ON CONFLICT(job_id) DO NOTHING;
  GET DIAGNOSTICS inserted_count=ROW_COUNT;
 RETURN inserted_count;
END $m10_schedule_derivation$;

REVOKE ALL ON FUNCTION control.schedule_m10_derivation_jobs(uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.schedule_m10_derivation_jobs(uuid,bigint) TO sqlobserver_collector;
