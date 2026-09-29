-- Private M10 backfill and derivation readers for partitioned-run parity.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION reporting.read_m10_backfill_page_v2_shadow(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_cursor text,p_max_rows integer,p_max_bytes integer,p_job_id uuid,p_source_catalog_version integer)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb,next_cursor text,has_more boolean,safe_cursor text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $m10_backfill_union$
DECLARE current_revision bigint; cursor_json jsonb; cursor_kind text; cursor_at timestamptz; cursor_id uuid; cursor_metric text; cursor_dimension_hash bytea; cursor_ordinal integer; cursor_target uuid; cursor_revision bigint; cursor_day timestamptz; cursor_catalog integer;
BEGIN
 IF p_instance_id IS NULL OR p_target_revision<1 OR p_job_id IS NULL OR p_source_catalog_version<>1 OR p_from_utc IS NULL OR p_to_utc IS NULL OR p_from_utc>=p_to_utc OR p_to_utc-p_from_utc>interval '1 day' OR p_max_rows NOT BETWEEN 1 AND 100000 OR p_max_bytes NOT BETWEEN 1 AND 8388608 OR p_metric_key IS NOT NULL AND p_metric_key !~ '^[a-z][a-z0-9._-]{0,127}$' OR p_cursor IS NOT NULL AND octet_length(p_cursor)>4096 THEN RAISE EXCEPTION 'analytics backfill page bounds rejected' USING ERRCODE='22023'; END IF;
 SELECT t.revision INTO current_revision FROM control.observation_target t WHERE t.instance_id=p_instance_id FOR SHARE;
 IF current_revision IS NULL OR current_revision<>p_target_revision OR p_instance_id::text<>current_setting('sqlobserver.target_scope',true) THEN RAISE EXCEPTION 'analytics backfill target revision conflict' USING ERRCODE='40001'; END IF;
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='backfill' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.from_utc<=p_from_utc AND j.to_utc>=p_to_utc) THEN RAISE EXCEPTION 'analytics backfill job fence conflict' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NOT NULL THEN BEGIN
   cursor_json:=convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb;
   cursor_kind:=cursor_json->>'sourceKind'; cursor_at:=(cursor_json->>'observedAtUtc')::timestamptz; cursor_id:=(cursor_json->>'sourceId')::uuid; cursor_metric:=cursor_json->>'metricKey'; cursor_dimension_hash:=decode(cursor_json->>'dimensionHash','hex'); cursor_ordinal:=(cursor_json->>'ordinal')::integer; cursor_target:=(cursor_json->>'targetId')::uuid; cursor_revision:=(cursor_json->>'targetRevision')::bigint; cursor_day:=(cursor_json->>'dayStartUtc')::timestamptz; cursor_catalog:=(cursor_json->>'catalogVersion')::integer;
   IF cursor_json->>'v' IS DISTINCT FROM 'm10.backfill.v2' OR cursor_kind NOT IN ('raw','host','replication') OR cursor_id IS NULL OR cursor_metric IS NULL OR octet_length(cursor_dimension_hash)<>32 OR cursor_ordinal<1 OR cursor_target<>p_instance_id OR cursor_revision<>p_target_revision OR cursor_day<>p_from_utc OR cursor_catalog<>p_source_catalog_version OR cursor_json->>'jobId'<>p_job_id::text THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END IF;
 EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END; END IF;
 -- A page must never turn an over-sized first row into an empty, terminal
 -- page. Check every source before the bounded union so the job fails
 -- explicitly and remains retryable instead of silently advancing.
  IF EXISTS (SELECT 1 FROM telemetry.raw_metric_sample s JOIN telemetry.collection_run_v2 cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision WHERE s.instance_id=p_instance_id AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND (p_metric_key IS NULL OR s.metric_key=p_metric_key) AND octet_length(row_to_json(s)::text)>p_max_bytes)
    OR EXISTS (SELECT 1 FROM telemetry.host_metric_snapshot_v2 h WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND (p_metric_key IS NULL OR h.metric_key=p_metric_key) AND octet_length(row_to_json(h)::text)>p_max_bytes)
    OR EXISTS (SELECT 1 FROM telemetry.replication_snapshot_v2 r WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND octet_length(row_to_json(r)::text)>p_max_bytes)
 THEN RAISE EXCEPTION 'analytics backfill row exceeds byte bound' USING ERRCODE='22023'; END IF;
 RETURN QUERY
WITH source_rows AS (
  -- Historical raw samples are admitted only when their metric and
  -- dimensions exactly match an enabled catalog-v1 entry.  Their source
  -- kind remains raw so the cursor cannot collide with v2 host/replication
  -- rows carrying the same timestamp and identifier.
   SELECT 'raw'::text source_kind,s.observed_at,s.sample_id source_id,s.metric_key,s.metric_value,s.dimensions,sha256(convert_to(s.dimensions::text,'UTF8')) dimension_hash,1::integer source_ordinal
   FROM telemetry.raw_metric_sample s
   JOIN telemetry.collection_run_v2 cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision
   JOIN analytics.metric_catalog c ON c.metric_key=s.metric_key AND c.enabled
  WHERE s.instance_id=p_instance_id AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND (p_metric_key IS NULL OR s.metric_key=p_metric_key)
    AND NOT EXISTS (SELECT 1 FROM jsonb_object_keys(s.dimensions) k WHERE NOT (c.definition->'dimensions' ? k))
  UNION ALL
  SELECT 'host'::text source_kind,h.observed_at,h.run_id source_id,h.metric_key,h.metric_value,h.dimensions,sha256(convert_to(h.dimensions::text,'UTF8')) dimension_hash,2::integer source_ordinal
   FROM telemetry.host_metric_snapshot_v2 h JOIN analytics.metric_catalog c ON c.metric_key=h.metric_key AND c.source_kind='host' AND c.enabled
   WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND (p_metric_key IS NULL OR h.metric_key=p_metric_key)
     AND NOT EXISTS (SELECT 1 FROM jsonb_object_keys(h.dimensions) k WHERE NOT (c.definition->'dimensions' ? k))
   UNION ALL SELECT 'replication',r.observed_at,r.run_id,'replication.pending_commands',r.pending_commands::double precision,'{}'::jsonb,sha256(convert_to('{}','UTF8')),3
   FROM telemetry.replication_snapshot_v2 r JOIN analytics.metric_catalog c ON c.metric_key='replication.pending_commands' AND c.source_kind='replication' AND c.enabled
   WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.pending_commands IS NOT NULL AND (p_metric_key IS NULL OR p_metric_key='replication.pending_commands')
   UNION ALL SELECT 'replication',r.observed_at,r.run_id,'replication.latency_seconds',r.latency_seconds,'{}'::jsonb,sha256(convert_to('{}','UTF8')),4
   FROM telemetry.replication_snapshot_v2 r JOIN analytics.metric_catalog c ON c.metric_key='replication.latency_seconds' AND c.source_kind='replication' AND c.enabled
   WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.latency_seconds IS NOT NULL AND (p_metric_key IS NULL OR p_metric_key='replication.latency_seconds')
 ), ordered AS (SELECT s.*,octet_length(row_to_json(s)::text)::bigint row_bytes FROM source_rows s WHERE p_cursor IS NULL OR (s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal)>(cursor_at,cursor_kind,cursor_id,cursor_metric,cursor_dimension_hash,cursor_ordinal)), bounded AS (SELECT o.*,row_number() OVER (ORDER BY o.observed_at,o.source_kind,o.source_id,o.metric_key,o.dimension_hash,o.source_ordinal) rn,sum(o.row_bytes) OVER (ORDER BY o.observed_at,o.source_kind,o.source_id,o.metric_key,o.dimension_hash,o.source_ordinal) total_bytes FROM ordered o), selected AS (SELECT * FROM bounded WHERE rn<=p_max_rows AND total_bytes<=p_max_bytes), last_row AS (SELECT * FROM selected ORDER BY observed_at DESC,source_kind DESC,source_id DESC,metric_key DESC,dimension_hash DESC,source_ordinal DESC LIMIT 1), remaining AS (SELECT EXISTS(SELECT 1 FROM ordered o,last_row l WHERE (o.observed_at,o.source_kind,o.source_id,o.metric_key,o.dimension_hash,o.source_ordinal)>(l.observed_at,l.source_kind,l.source_id,l.metric_key,l.dimension_hash,l.source_ordinal)) has_more), safe_rows AS (SELECT s.* FROM selected s CROSS JOIN last_row l CROSS JOIN remaining m WHERE (m.has_more AND (s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal)<(l.observed_at,l.source_kind,l.source_id,l.metric_key,l.dimension_hash,l.source_ordinal) AND date_trunc('hour',s.observed_at)+floor(extract(minute FROM s.observed_at)/5)*interval '5 minutes' < date_trunc('hour',l.observed_at)+floor(extract(minute FROM l.observed_at)/5)*interval '5 minutes') OR (NOT m.has_more AND date_trunc('hour',s.observed_at)+floor(extract(minute FROM s.observed_at)/5)*interval '5 minutes'+interval '5 minutes'<=p_to_utc)), safe_last AS (SELECT * FROM safe_rows ORDER BY observed_at DESC,source_kind DESC,source_id DESC,metric_key DESC,dimension_hash DESC,source_ordinal DESC LIMIT 1)
 SELECT s.observed_at,s.metric_key,s.metric_value,s.dimensions,CASE WHEN remaining.has_more THEN encode(convert_to(jsonb_build_object('v','m10.backfill.v2','sourceKind',l.source_kind,'observedAtUtc',l.observed_at,'sourceId',l.source_id,'metricKey',l.metric_key,'dimensionHash',encode(l.dimension_hash,'hex'),'ordinal',l.source_ordinal,'targetId',p_instance_id,'targetRevision',p_target_revision,'jobId',p_job_id,'dayStartUtc',p_from_utc,'catalogVersion',p_source_catalog_version)::text,'UTF8'),'base64') END,remaining.has_more,CASE WHEN sl.source_id IS NOT NULL THEN encode(convert_to(jsonb_build_object('v','m10.backfill.v2','sourceKind',sl.source_kind,'observedAtUtc',sl.observed_at,'sourceId',sl.source_id,'metricKey',sl.metric_key,'dimensionHash',encode(sl.dimension_hash,'hex'),'ordinal',sl.source_ordinal,'targetId',p_instance_id,'targetRevision',p_target_revision,'jobId',p_job_id,'dayStartUtc',p_from_utc,'catalogVersion',p_source_catalog_version)::text,'UTF8'),'base64') END FROM selected s CROSS JOIN last_row l CROSS JOIN remaining LEFT JOIN safe_last sl ON true WHERE (s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal)<=(l.observed_at,l.source_kind,l.source_id,l.metric_key,l.dimension_hash,l.source_ordinal) ORDER BY s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal;
END $m10_backfill_union$;

CREATE OR REPLACE FUNCTION reporting.read_m10_rollup_derivation_inputs_v2_shadow(
 p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,
 p_metric_key text,p_limit integer,p_snapshot_utc timestamptz,p_rollup_interval text,
 p_dimension_hash bytea)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb,reset boolean,complete boolean)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,analytics,control SET TimeZone='UTC' AS $$
 WITH source_rows AS (
 SELECT s.observed_at,s.metric_key,s.metric_value,s.dimensions,false AS reset,true AS complete
 FROM telemetry.raw_metric_sample s
 JOIN telemetry.collection_run_v2 cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND s.instance_id=p_instance_id
   AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND s.observed_at<=p_snapshot_utc
   AND (p_metric_key IS NULL OR s.metric_key=p_metric_key)
   AND (p_dimension_hash IS NULL OR sha256(convert_to(s.dimensions::text,'UTF8'))=p_dimension_hash)
 UNION ALL
 SELECT h.observed_at,h.metric_key,h.metric_value,h.dimensions,false,true
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision
   AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc
   AND (p_metric_key IS NULL OR h.metric_key=p_metric_key)
   AND (p_dimension_hash IS NULL OR sha256(convert_to(h.dimensions::text,'UTF8'))=p_dimension_hash)
 UNION ALL
 SELECT r.observed_at,'replication.pending_commands',r.pending_commands::double precision,'{}'::jsonb,false,true
 FROM telemetry.replication_snapshot_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND r.pending_commands IS NOT NULL
   AND (p_metric_key IS NULL OR p_metric_key='replication.pending_commands')
   AND (p_dimension_hash IS NULL OR p_dimension_hash=sha256(convert_to('{}','UTF8')))
 UNION ALL
 SELECT r.observed_at,'replication.latency_seconds',r.latency_seconds,'{}'::jsonb,false,true
 FROM telemetry.replication_snapshot_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND r.latency_seconds IS NOT NULL
   AND (p_metric_key IS NULL OR p_metric_key='replication.latency_seconds')
   AND (p_dimension_hash IS NULL OR p_dimension_hash=sha256(convert_to('{}','UTF8')))
 ) SELECT observed_at,metric_key,metric_value,dimensions,reset,complete FROM source_rows
 WHERE p_rollup_interval IN ('5m','hour','day') AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days' AND p_limit BETWEEN 1 AND 100000
 ORDER BY observed_at,metric_key,dimensions LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.read_m10_backfill_page_v2_shadow(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.read_m10_rollup_derivation_inputs_v2_shadow(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_m10_backfill_page_v2_shadow(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)
 TO sqlobserver_migrator;
GRANT EXECUTE ON FUNCTION reporting.read_m10_rollup_derivation_inputs_v2_shadow(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)
 TO sqlobserver_migrator;
