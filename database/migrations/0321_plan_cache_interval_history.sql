-- Snapshot-qualified cache history. A sequence fence is allocated under the
-- same per-target transaction lock as the cache writer. Sequence values survive
-- pg_dump/restore; PostgreSQL transaction IDs do not provide that portability.
-- The staged receipt parent is rewritten once to stamp existing receipts.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE SEQUENCE events.plan_cache_runtime_repository_sequence AS bigint;
ALTER TABLE events.plan_cache_runtime_receipt
 ADD COLUMN repository_sequence bigint NOT NULL DEFAULT nextval('events.plan_cache_runtime_repository_sequence');
ALTER SEQUENCE events.plan_cache_runtime_repository_sequence OWNED BY events.plan_cache_runtime_receipt.repository_sequence;
REVOKE ALL ON SEQUENCE events.plan_cache_runtime_repository_sequence FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.list_plan_cache_interval_history(
 p_instance_id uuid,p_database_id integer,p_incarnation text,p_query text,p_plan text,
 p_from_utc timestamptz,p_to_utc timestamptz,p_max_results integer,p_cursor jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
SET plan_cache_mode=force_custom_plan
AS $history$
DECLARE v_result jsonb; v_revision bigint; v_snapshot bigint; v_snapshot_at timestamptz;
BEGIN
 IF p_instance_id IS NULL OR p_database_id IS NULL OR p_database_id NOT BETWEEN 5 AND 32766
  OR p_incarnation IS NULL OR p_incarnation !~ '^[0-9a-f]{64}$'
  OR p_query IS NULL OR p_query !~ '^[0-9a-f]{64}$' OR p_plan IS NULL OR p_plan !~ '^[0-9a-f]{64}$'
  OR p_from_utc IS NULL OR p_to_utc IS NULL OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
  OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
  OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN
  RAISE EXCEPTION 'Cache history bounds are invalid' USING ERRCODE='22023'; END IF;
 IF NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'Cache history requires exact target scope' USING ERRCODE='42501'; END IF;
 SELECT target.revision INTO v_revision FROM control.observation_target target WHERE target.instance_id=p_instance_id;
 IF v_revision IS NULL THEN RAISE EXCEPTION 'Cache history target is unavailable' USING ERRCODE='42501'; END IF;
 IF p_cursor IS NULL THEN
  -- Every cache writer holds this until commit. Earlier allocated receipts are
  -- committed before the fence; later writes must allocate a greater sequence.
  PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:cache-runtime:target:'||p_instance_id::text,0));
  SELECT target.revision INTO v_revision FROM control.observation_target target WHERE target.instance_id=p_instance_id;
  IF v_revision IS NULL THEN RAISE EXCEPTION 'Cache history target is unavailable' USING ERRCODE='42501'; END IF;
  v_snapshot:=nextval('events.plan_cache_runtime_repository_sequence'); v_snapshot_at:=clock_timestamp();
 ELSE
  IF jsonb_typeof(p_cursor)<>'object' OR (p_cursor->>'revision')::bigint IS DISTINCT FROM v_revision
   OR p_cursor->>'snapshot' IS NULL OR p_cursor->>'snapshot' !~ '^[0-9]{1,19}$'
   OR p_cursor->>'snapshotUtc' IS NULL OR p_cursor->>'totalRows' IS NULL
   OR (p_cursor->>'totalRows')::bigint<1 OR p_cursor->>'observedAtUtc' IS NULL
   OR p_cursor->>'committedAtUtc' IS NULL OR p_cursor->>'collectionRunId' IS NULL
   OR p_cursor->>'cacheEntryFingerprint' IS NULL OR p_cursor->>'cacheEntryFingerprint' !~ '^[0-9a-f]{64}$' THEN
   RAISE EXCEPTION 'Cache history continuation is invalid' USING ERRCODE='22023'; END IF;
  v_snapshot:=(p_cursor->>'snapshot')::bigint; v_snapshot_at:=(p_cursor->>'snapshotUtc')::timestamptz;
  IF v_snapshot<1 OR NOT isfinite(v_snapshot_at) OR v_snapshot_at>clock_timestamp() OR v_snapshot_at<clock_timestamp()-interval '30 minutes' THEN
   RAISE EXCEPTION 'Cache history continuation expired' USING ERRCODE='22023'; END IF;
 END IF;

 WITH source AS MATERIALIZED (
  SELECT delta.*,receipt.qualified_exhaustive,
   CASE WHEN delta.transition='comparable' AND delta.delta_from<p_from_utc THEN 'window_boundary' ELSE delta.transition END AS state
  FROM events.plan_cache_runtime_delta delta JOIN events.plan_cache_runtime_receipt receipt
   ON receipt.instance_id=delta.instance_id AND receipt.run_id=delta.run_id AND receipt.committed_at=delta.committed_at
  WHERE delta.instance_id=p_instance_id AND delta.target_revision=v_revision AND receipt.target_revision=v_revision
   AND delta.database_id=p_database_id AND delta.database_incarnation=decode(p_incarnation,'hex')
   AND delta.query_fingerprint=decode(p_query,'hex') AND delta.plan_fingerprint=decode(p_plan,'hex')
   AND delta.source_read_completed_at>=p_from_utc AND delta.source_read_completed_at<p_to_utc
   AND delta.committed_at>=p_from_utc-interval '5 minutes' AND delta.committed_at<p_to_utc+interval '5 minutes'
   AND receipt.committed_at>=p_from_utc-interval '5 minutes' AND receipt.committed_at<p_to_utc+interval '5 minutes'
   AND receipt.repository_sequence<v_snapshot
 ), page AS MATERIALIZED (
  SELECT source.* FROM source WHERE p_cursor IS NULL OR
   (source.source_read_completed_at,source.committed_at,source.run_id,source.cache_entry_fingerprint)<
   ((p_cursor->>'observedAtUtc')::timestamptz,(p_cursor->>'committedAtUtc')::timestamptz,
    (p_cursor->>'collectionRunId')::uuid,decode(p_cursor->>'cacheEntryFingerprint','hex'))
  ORDER BY source.source_read_completed_at DESC,source.committed_at DESC,source.run_id DESC,source.cache_entry_fingerprint DESC
  LIMIT p_max_results+1
 ), numbered AS (
  SELECT page.*,row_number() OVER(ORDER BY page.source_read_completed_at DESC,page.committed_at DESC,page.run_id DESC,page.cache_entry_fingerprint DESC) AS ordinal FROM page
 )
 SELECT jsonb_build_object(
  'targetId',p_instance_id,'databaseId',p_database_id,'databaseIncarnation',p_incarnation,'queryFingerprint',p_query,'planFingerprint',p_plan,
  'fromUtc',p_from_utc,'toUtc',p_to_utc,'semantics','plan_cache_delta','coverage','observed_entries_only',
  'revision',v_revision,'snapshot',v_snapshot::text,'snapshotUtc',v_snapshot_at,'totalRows',(SELECT count(*) FROM source),
  'hasMore',EXISTS(SELECT 1 FROM numbered WHERE ordinal>p_max_results),
  'items',coalesce((SELECT jsonb_agg(jsonb_build_object(
   'collectionRunId',numbered.run_id,'cacheEntryFingerprint',encode(numbered.cache_entry_fingerprint,'hex'),
   'sourceEpoch',encode(numbered.source_epoch,'hex'),'observedAtUtc',numbered.source_read_completed_at,'committedAtUtc',numbered.committed_at,
   'captureComplete',numbered.qualified_exhaustive,'transition',numbered.state,'intervalFromUtc',numbered.delta_from,'intervalToUtc',numbered.delta_to,
   'cpuMilliseconds',CASE WHEN numbered.state='comparable' THEN (numbered.cpu_us::numeric/1000)::text END,
   'durationMilliseconds',CASE WHEN numbered.state='comparable' THEN (numbered.duration_us::numeric/1000)::text END,
   'executions',CASE WHEN numbered.state='comparable' THEN numbered.execution_count::text END,
   'logicalReads',CASE WHEN numbered.state='comparable' THEN numbered.logical_reads::text END,
   'writes',CASE WHEN numbered.state='comparable' THEN numbered.writes::text END,
   'rows',CASE WHEN numbered.state='comparable' THEN numbered.rows_processed::text END) ORDER BY numbered.ordinal)
   FROM numbered WHERE numbered.ordinal<=p_max_results),'[]'::jsonb)) INTO v_result;
 -- The sequence fence excludes later writes. Append-only rows can still disappear
 -- during retention; invalidate continuation rather than silently skipping them.
 IF p_cursor IS NOT NULL AND (v_result->>'totalRows')::bigint<>(p_cursor->>'totalRows')::bigint THEN
  RAISE EXCEPTION 'Cache history evidence was retired; restart pagination' USING ERRCODE='22023'; END IF;
 RETURN v_result;
END $history$;
REVOKE ALL ON FUNCTION reporting.list_plan_cache_interval_history(uuid,integer,text,text,text,timestamptz,timestamptz,integer,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_plan_cache_interval_history(uuid,integer,text,text,text,timestamptz,timestamptz,integer,jsonb) TO sqlobserver_server;
