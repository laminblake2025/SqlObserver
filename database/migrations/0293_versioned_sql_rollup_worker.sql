-- Activate SQL rollups without changing catalog-v1 job/cursor interpretation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.analytics_job ADD COLUMN source_catalog_version integer NOT NULL DEFAULT 1;
ALTER TABLE control.analytics_job ADD COLUMN source_cadence_seconds double precision;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_sql_rollup_job_version CHECK
 (source_catalog_version=1 OR source_catalog_version=2 AND coalesce(
   job_kind='rollup' AND work_key='analytics/derivation' AND source_cadence_seconds BETWEEN 1 AND 86400
   AND source_cadence_seconds NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)
   AND metric_key IN ('engine.user_connections','engine.process_physical_memory_bytes','engine.os_available_memory_bytes',
    'engine.memory_grants_pending','engine.scheduler_runnable_tasks','engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')
   AND dimensions_hash=sha256(convert_to('{}','UTF8')) AND from_utc IS NOT NULL AND to_utc IS NOT NULL
   AND to_utc>from_utc AND to_utc-from_utc<=interval '1 day',false));

ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN catalog_checksum text;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN weight_sum double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN weighted_value_sum double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN minimum_value double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN maximum_value double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN sample_value_sum double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD CONSTRAINT ck_sql_rollup_finite_statistics CHECK
 ((weighted_value_sum IS NULL OR weighted_value_sum>=0 AND weighted_value_sum NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
  AND (minimum_value IS NULL OR minimum_value>=0 AND minimum_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
  AND (maximum_value IS NULL OR maximum_value>=0 AND maximum_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
  AND (sample_value_sum IS NULL OR sample_value_sum>=0 AND sample_value_sum NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
  AND (minimum_value IS NULL OR maximum_value IS NULL OR minimum_value<=maximum_value));
ALTER TABLE analytics.metric_rollup_v2 ADD CONSTRAINT ck_sql_rollup_metadata CHECK
 (catalog_version<>2 OR coalesce(algorithm_version='sql-rollup-v2'
  AND catalog_checksum='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
  AND weight_sum>=0 AND weight_sum NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)
  AND (sample_count=0 AND weight_sum=0 AND value IS NULL AND weighted_value_sum IS NULL
   OR sample_count>0 AND weight_sum>0 AND weighted_value_sum IS NOT NULL AND value=weighted_value_sum/weight_sum),false));

CREATE FUNCTION control.claim_analytics_derivation_catalogs(
 p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer,p_allow_sql boolean)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,from_utc timestamptz,to_utc timestamptz,
 source_cutoff_utc timestamptz,generation bigint,metric_key text,horizon_seconds double precision,
 dimensions_hash bytea,rollup_interval text,source_catalog_version integer,source_cadence_seconds double precision)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/derivation' OR p_owner_execution_id IS NULL OR p_fencing_token<1
 OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 2 OR p_allow_sql IS NULL
 THEN RAISE EXCEPTION 'analytics derivation claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
  SELECT j.job_id FROM control.analytics_job j JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
  WHERE j.work_key='analytics/derivation' AND j.job_kind IN ('rollup','baseline','forecast','evidence','correlation')
   AND (j.source_catalog_version=1 OR p_allow_sql AND j.source_catalog_version=2)
   AND j.from_utc IS NOT NULL AND j.to_utc IS NOT NULL
   AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
    (SELECT 1 FROM control.worker_lease l WHERE l.work_key=j.work_key AND l.owner_execution_id=j.owner_execution_id
     AND l.fencing_token=j.fencing_token AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
  ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
  UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
  FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5 RETURNING j.job_id
 ), claimed AS (
  UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,
   started_at=clock_timestamp(),attempt=j.attempt+1
  FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5 RETURNING j.*
 ) SELECT c.job_id,c.instance_id,c.target_revision,c.job_kind,c.from_utc,c.to_utc,coalesce(c.source_cutoff_utc,c.to_utc),
  c.generation,c.metric_key,c.horizon_seconds,c.dimensions_hash,c.rollup_interval,c.source_catalog_version,c.source_cadence_seconds FROM claimed c;
END $$;
REVOKE ALL ON FUNCTION control.claim_analytics_derivation_catalogs(text,uuid,bigint,integer,boolean)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION control.claim_m10_derivation_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,from_utc timestamptz,to_utc timestamptz,
 source_cutoff_utc timestamptz,generation bigint,metric_key text,horizon_seconds double precision,dimensions_hash bytea,rollup_interval text)
LANGUAGE sql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT j.job_id,j.instance_id,j.target_revision,j.job_kind,j.from_utc,j.to_utc,j.source_cutoff_utc,
  j.generation,j.metric_key,j.horizon_seconds,j.dimensions_hash,j.rollup_interval
 FROM control.claim_analytics_derivation_catalogs(p_work_key,p_owner_execution_id,p_fencing_token,p_limit,false) j;
$$;

CREATE FUNCTION control.claim_m10_derivation_jobs_versioned(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,from_utc timestamptz,to_utc timestamptz,
 source_cutoff_utc timestamptz,generation bigint,metric_key text,horizon_seconds double precision,
 dimensions_hash bytea,rollup_interval text,source_catalog_version integer,source_cadence_seconds double precision)
LANGUAGE sql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT * FROM control.claim_analytics_derivation_catalogs(p_work_key,p_owner_execution_id,p_fencing_token,p_limit,true);
$$;
REVOKE ALL ON FUNCTION control.claim_m10_derivation_jobs_versioned(text,uuid,bigint,integer)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.claim_m10_derivation_jobs_versioned(text,uuid,bigint,integer) TO sqlobserver_collector;

CREATE FUNCTION control.schedule_sql_metric_rollup_jobs(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE schedule_now timestamptz:=clock_timestamp(); n integer;
BEGIN
 PERFORM control.assert_worker_lease('analytics/derivation',p_owner_execution_id,p_fencing_token);
 WITH targets AS (
  SELECT t.instance_id,t.revision,extract(epoch FROM s.collection_interval)::double precision AS cadence
  FROM control.observation_target t JOIN control.collector_schedule s ON s.instance_id=t.instance_id
  WHERE t.lifecycle_state='active' AND t.revision>0 AND s.collector_id='engine.core' AND s.enabled
 ), metrics AS (
  SELECT metric_key FROM unnest(ARRAY['engine.user_connections','engine.process_physical_memory_bytes',
   'engine.os_available_memory_bytes','engine.memory_grants_pending','engine.scheduler_runnable_tasks',
   'engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent']) AS metric_key
 ), due AS (
  SELECT left(encode(sha256(convert_to('sql-rollup-v2|'||t.instance_id::text||'|'||t.revision::text||'|'||m.metric_key||'|'||b.interval_name||'|'||b.bucket_start::text,'UTF8')),'hex'),32)::uuid AS id,
   t.instance_id,t.revision,t.cadence,m.metric_key,b.interval_name,b.bucket_start,b.width
  FROM targets t CROSS JOIN metrics m CROSS JOIN control.m10_rollup_due_buckets(schedule_now) b
 ), bounded AS (
  SELECT d.* FROM due d WHERE NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=d.id)
  ORDER BY d.bucket_start,d.instance_id,d.metric_key,d.interval_name LIMIT 2688
 )
 INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,
  source_cutoff_utc,generation,status,work_key,rollup_interval,dimensions_hash,source_catalog_version,source_cadence_seconds)
 SELECT d.id,'rollup',d.instance_id,d.revision,d.bucket_start,d.bucket_start+d.width,d.metric_key,schedule_now,1,'queued',
  'analytics/derivation',d.interval_name,sha256(convert_to('{}','UTF8')),2,d.cadence
 FROM bounded d
 ON CONFLICT(job_id) DO NOTHING;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $$;
REVOKE ALL ON FUNCTION control.schedule_sql_metric_rollup_jobs(uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.schedule_sql_metric_rollup_jobs(uuid,bigint) TO sqlobserver_collector;

-- The old writer remains v1-only, including calls from older worker binaries.
ALTER FUNCTION analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) RENAME TO commit_metric_rollups_catalog_v1;
REVOKE ALL ON FUNCTION analytics.commit_metric_rollups_catalog_v1(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION analytics.commit_metric_rollups
 (p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,
  p_fencing_token bigint,p_request_digest bytea,p_rows jsonb,p_result_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.source_catalog_version=1)
 THEN RAISE EXCEPTION 'catalog-v1 rollup job required' USING ERRCODE='40001'; END IF;
 RETURN QUERY SELECT * FROM analytics.commit_metric_rollups_catalog_v1(p_operation_id,p_job_id,p_instance_id,p_target_revision,
  p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_rows,p_result_digest);
END $$;
REVOKE ALL ON FUNCTION analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;

CREATE FUNCTION analytics.commit_sql_metric_rollup(
 p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,
 p_fencing_token bigint,p_request_digest bytea,p_row jsonb,p_result_digest bytea)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE job control.analytics_job%ROWTYPE; existing analytics.metric_rollup_v2%ROWTYPE; n integer; partition_name text; partition_day date;
BEGIN
 IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<1
 OR p_work_key IS DISTINCT FROM 'analytics/derivation' OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token<1
 OR p_request_digest IS NULL OR octet_length(p_request_digest)<>32 OR p_result_digest IS NULL OR octet_length(p_result_digest)<>32
 OR p_row IS NULL OR jsonb_typeof(p_row)<>'object' OR octet_length(p_row::text)>8388608
 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'SQL rollup write bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 PERFORM control.resolve_m10_target_revision(p_instance_id,p_target_revision);
 SELECT j.* INTO job FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.instance_id=p_instance_id
  AND j.target_revision=p_target_revision AND j.job_kind='rollup' AND j.source_catalog_version=2
  AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'SQL rollup job fence conflict' USING ERRCODE='40001'; END IF;
 IF NOT coalesce((p_row->>'catalogVersion')::integer=2
  AND p_row->>'catalogChecksum'='702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338'
  AND p_row->>'algorithmVersion'='sql-rollup-v2' AND p_row->>'metricKey'=job.metric_key
  AND p_row->>'interval'=job.rollup_interval AND p_row->>'aggregation'='avg'
  AND (p_row->>'bucketStartUtc')::timestamptz=job.from_utc
  AND job.from_utc=date_bin(CASE job.rollup_interval WHEN '5m' THEN interval '5 minutes' WHEN 'hour' THEN interval '1 hour' ELSE interval '1 day' END,job.from_utc,timestamptz '2000-01-01 00:00:00Z')
  AND job.to_utc=job.from_utc+CASE job.rollup_interval WHEN '5m' THEN interval '5 minutes' WHEN 'hour' THEN interval '1 hour' ELSE interval '1 day' END
  AND (p_row->>'sourceCutoffUtc')::timestamptz=job.source_cutoff_utc
  AND (p_row->>'generation')::bigint=job.generation AND p_row->'dimensions'='{}'::jsonb
  AND decode(p_row->>'dimensionHash','hex')=sha256(convert_to('{}','UTF8'))
  AND (p_row->>'expectedCount')::integer=ceil(extract(epoch FROM job.to_utc-job.from_utc)/job.source_cadence_seconds)::integer
  AND (p_row->>'sampleCount')::integer>=0 AND (p_row->>'gapCount')::integer>=0 AND (p_row->>'resetCount')::integer=0
  AND (p_row->>'truncated')::boolean=false,false)
 THEN RAISE EXCEPTION 'SQL rollup immutable metadata rejected' USING ERRCODE='22023'; END IF;
 IF NOT control.record_m10_analytics_replay(p_operation_id,'rollup',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,
  p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('row',p_row)) THEN RETURN false; END IF;
 SELECT r.* INTO existing FROM analytics.metric_rollup_v2 r WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision
  AND r.bucket_start=job.from_utc AND r.rollup_interval=job.rollup_interval AND r.metric_key=job.metric_key
  AND r.aggregation='avg' AND r.dimension_hash=sha256(convert_to('{}','UTF8')) AND r.generation=job.generation;
 IF FOUND THEN
  -- A worker may crash after persistence but before fenced completion. A
  -- replacement lease can replay identical content without rewriting history.
  IF existing.catalog_version IS DISTINCT FROM 2 OR existing.catalog_checksum IS DISTINCT FROM p_row->>'catalogChecksum'
   OR existing.algorithm_version IS DISTINCT FROM 'sql-rollup-v2'
   OR existing.sample_count IS DISTINCT FROM (p_row->>'sampleCount')::integer
   OR existing.expected_count IS DISTINCT FROM (p_row->>'expectedCount')::integer
   OR existing.gap_count IS DISTINCT FROM (p_row->>'gapCount')::integer
   OR existing.visibility_state IS DISTINCT FROM p_row->>'visibilityState'
   OR existing.source_cutoff_utc IS DISTINCT FROM job.source_cutoff_utc
   OR existing.value IS DISTINCT FROM (p_row->>'value')::double precision
   OR existing.last_value IS DISTINCT FROM (p_row->>'lastValue')::double precision
   OR existing.weight_sum IS DISTINCT FROM (p_row->>'weightSum')::double precision
   OR existing.weighted_value_sum IS DISTINCT FROM (p_row->>'weightedValueSum')::double precision
   OR existing.minimum_value IS DISTINCT FROM (p_row->>'minimumValue')::double precision
   OR existing.maximum_value IS DISTINCT FROM (p_row->>'maximumValue')::double precision
   OR existing.sample_value_sum IS DISTINCT FROM (p_row->>'sampleValueSum')::double precision
  THEN RAISE EXCEPTION 'SQL rollup identity has divergent content' USING ERRCODE='40001'; END IF;
  RETURN false;
 END IF;
 -- Create only the canonical rollup day needed by this fenced job.
 partition_day:=job.from_utc::date; partition_name:='metric_rollup_v2_p'||to_char(partition_day,'YYYYMMDD');
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:sql-rollup-day:'||partition_day::text,0));
 EXECUTE format('CREATE TABLE IF NOT EXISTS analytics.%I PARTITION OF analytics.metric_rollup_v2 FOR VALUES FROM (%L) TO (%L)',
  partition_name,partition_day::timestamptz,(partition_day+1)::timestamptz);
 INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
 VALUES('analytics','metric_rollup_v2','analytics',partition_name,'day',partition_day::timestamptz,(partition_day+1)::timestamptz)
 ON CONFLICT DO NOTHING;
 INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,
  sample_count,value,visibility_state,dimensions,dimension_hash,expected_count,reset_count,gap_count,truncated,source_cutoff_utc,
  catalog_version,algorithm_version,last_value,counter_delta,rate_per_second,generation,catalog_checksum,
  weight_sum,weighted_value_sum,minimum_value,maximum_value,sample_value_sum)
 VALUES(job.from_utc,p_instance_id,p_target_revision,job.rollup_interval,job.metric_key,'avg',
  (p_row->>'sampleCount')::integer,(p_row->>'value')::double precision,p_row->>'visibilityState','{}'::jsonb,sha256(convert_to('{}','UTF8')),
  (p_row->>'expectedCount')::integer,0,(p_row->>'gapCount')::integer,false,job.source_cutoff_utc,
  2,'sql-rollup-v2',(p_row->>'lastValue')::double precision,NULL,NULL,job.generation,p_row->>'catalogChecksum',
  (p_row->>'weightSum')::double precision,(p_row->>'weightedValueSum')::double precision,
  (p_row->>'minimumValue')::double precision,(p_row->>'maximumValue')::double precision,(p_row->>'sampleValueSum')::double precision);
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n=1;
END $$;
REVOKE ALL ON FUNCTION analytics.commit_sql_metric_rollup(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION analytics.commit_sql_metric_rollup(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;
