-- Private staged cache comparison. The production lifecycle wrapper must fence
-- run/target/schedule/lease and publish this state with the run in one transaction.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION events.apply_plan_cache_runtime_batch(
    p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_payload jsonb)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET row_security=on
AS $batch$
DECLARE v_field record; v_item jsonb; v_digest bytea; v_previous events.plan_cache_runtime_watermark%ROWTYPE;
    v_receipt events.plan_cache_runtime_receipt%ROWTYPE; v_has_previous boolean;
    v_now timestamptz:=clock_timestamp(); v_started timestamptz; v_completed timestamptz; v_day timestamptz;
    v_before_started timestamptz; v_before_completed timestamptz; v_after_started timestamptz; v_after_completed timestamptz;
    v_epoch bytea; v_incarnation bytea; v_query bytea; v_plan bytea; v_entry bytea; v_database integer;
    v_cpu bigint; v_duration bigint; v_executions bigint; v_reads bigint; v_writes bigint; v_rows bigint;
    v_limit integer; v_candidates integer; v_unresolved integer; v_invisible integer;
    v_excluded integer; v_emitted integer; v_unqualified integer; v_items integer;
    v_exhaustive boolean; v_transition text;
BEGIN
    IF p_run_id IS NULL OR p_run_id='00000000-0000-0000-0000-000000000000'::uuid
        OR p_instance_id IS NULL OR p_instance_id='00000000-0000-0000-0000-000000000000'::uuid
        OR p_target_revision IS NULL OR p_target_revision<=0
        OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN
        RAISE EXCEPTION 'Cache batch requires exact target scope' USING ERRCODE='42501'; END IF;
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR octet_length(p_payload::text)>4194304
        OR (SELECT count(*) FROM jsonb_object_keys(p_payload))<>15
        OR NOT p_payload ?& ARRAY['schemaVersion','targetId','targetRevision','sourceEpoch',
            'readStartedUtc','readCompletedUtc','candidateLimit','candidateRows','unresolvedRows',
            'invisibleDatabaseRows','excludedRows','emittedRows','unqualifiedRows','sourceExhaustive','items']
        OR p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb
        OR jsonb_typeof(p_payload->'targetId') IS DISTINCT FROM 'string'
        OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
        OR jsonb_typeof(p_payload->'targetRevision') IS DISTINCT FROM 'number'
        OR p_payload->>'targetRevision' !~ '^[1-9][0-9]{0,18}$'
        OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
        OR jsonb_typeof(p_payload->'sourceEpoch') IS DISTINCT FROM 'string'
        OR p_payload->>'sourceEpoch' !~ '^[0-9a-f]{64}$'
        OR jsonb_typeof(p_payload->'sourceExhaustive') IS DISTINCT FROM 'boolean'
        OR jsonb_typeof(p_payload->'items') IS DISTINCT FROM 'array'
        OR jsonb_array_length(p_payload->'items')>2000 THEN
        RAISE EXCEPTION 'Cache batch frame is invalid' USING ERRCODE='22023'; END IF;
    FOR v_field IN SELECT key,value FROM jsonb_each(p_payload) WHERE key IN
        ('candidateLimit','candidateRows','unresolvedRows','invisibleDatabaseRows','excludedRows','emittedRows','unqualifiedRows') LOOP
        IF jsonb_typeof(v_field.value) IS DISTINCT FROM 'number' OR v_field.value::text !~ '^(0|[1-9][0-9]{0,9})$' THEN
            RAISE EXCEPTION 'Cache accounting value is invalid' USING ERRCODE='22023'; END IF;
    END LOOP;
    FOR v_field IN SELECT key,value FROM jsonb_each(p_payload) WHERE key IN ('readStartedUtc','readCompletedUtc') LOOP
        IF jsonb_typeof(v_field.value) IS DISTINCT FROM 'string' OR p_payload->>v_field.key !~ 'Z$' THEN
            RAISE EXCEPTION 'Cache capture timestamp is not UTC' USING ERRCODE='22023'; END IF;
    END LOOP;
    v_limit:=(p_payload->>'candidateLimit')::integer; v_candidates:=(p_payload->>'candidateRows')::integer;
    v_unresolved:=(p_payload->>'unresolvedRows')::integer; v_invisible:=(p_payload->>'invisibleDatabaseRows')::integer;
    v_excluded:=(p_payload->>'excludedRows')::integer; v_emitted:=(p_payload->>'emittedRows')::integer;
    v_unqualified:=(p_payload->>'unqualifiedRows')::integer; v_items:=jsonb_array_length(p_payload->'items');
    v_exhaustive:=(p_payload->>'sourceExhaustive')::boolean;
    v_started:=(p_payload->>'readStartedUtc')::timestamptz; v_completed:=(p_payload->>'readCompletedUtc')::timestamptz;
    v_epoch:=decode(p_payload->>'sourceEpoch','hex'); v_day:=date_trunc('day',v_completed,'UTC');
    IF v_limit NOT BETWEEN 1 AND 2000 OR v_candidates NOT BETWEEN 0 AND v_limit+1
        OR v_unresolved::bigint+v_invisible+v_excluded+v_emitted<>v_candidates
        OR v_items::bigint+v_unqualified<>least(v_emitted,v_limit)
        OR v_exhaustive IS DISTINCT FROM (v_candidates<=v_limit AND v_unresolved=0 AND v_invisible=0)
        OR NOT isfinite(v_started) OR NOT isfinite(v_completed) OR v_completed<v_started
        OR v_completed-v_started>interval '20 seconds'
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_payload->'items') AS item(value)
            GROUP BY item.value->>'cacheEntryFingerprint' HAVING count(*)>1) THEN
        RAISE EXCEPTION 'Cache accounting, capture or statement uniqueness is invalid' USING ERRCODE='22023'; END IF;

    -- Future canonical wrappers take their run/schedule locks before this lock.
    -- Different targets never serialize behind one global comparison lock.
    PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:cache-runtime:target:'||p_instance_id::text,0));
    v_digest:=sha256(convert_to(p_payload::text,'UTF8'));
    SELECT receipt.* INTO v_receipt FROM events.plan_cache_runtime_receipt AS receipt
        WHERE receipt.instance_id=p_instance_id AND receipt.run_id=p_run_id;
    IF FOUND THEN
        IF v_receipt.target_revision<>p_target_revision OR v_receipt.payload_digest<>v_digest THEN
            RAISE EXCEPTION 'Cache completion replay differs' USING ERRCODE='40001'; END IF;
        RETURN 0;
    END IF;
    IF v_started<v_now-interval '5 minutes' OR v_completed>v_now+interval '5 minutes' THEN
        RAISE EXCEPTION 'Cache capture clock differs from repository time' USING ERRCODE='22023'; END IF;
    PERFORM control.ensure_plan_cache_runtime_partitions((v_now AT TIME ZONE 'UTC')::date);

    FOR v_item IN SELECT value FROM jsonb_array_elements(p_payload->'items') LOOP
        IF jsonb_typeof(v_item) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(v_item))<>19
            OR NOT v_item ?& ARRAY['databaseId','databaseIncarnation','queryFingerprint','planFingerprint','cacheEntryFingerprint',
                'cpuUs','durationUs','executionCount','logicalReads','writes','rowsProcessed',
                'identityBeforeStartedUtc','identityBeforeCompletedUtc','identityAfterStartedUtc','identityAfterCompletedUtc',
                'identityBeforeIncarnation','identityAfterIncarnation','identityBeforeEpoch','identityAfterEpoch'] THEN
            RAISE EXCEPTION 'Cache statement frame is invalid' USING ERRCODE='22023'; END IF;
        FOR v_field IN SELECT key,value FROM jsonb_each(v_item) WHERE key IN
            ('databaseIncarnation','queryFingerprint','planFingerprint','cacheEntryFingerprint',
             'identityBeforeIncarnation','identityAfterIncarnation','identityBeforeEpoch','identityAfterEpoch') LOOP
            IF jsonb_typeof(v_field.value) IS DISTINCT FROM 'string' OR v_item->>v_field.key !~ '^[0-9a-f]{64}$' THEN
                RAISE EXCEPTION 'Cache statement digest is invalid' USING ERRCODE='22023'; END IF;
        END LOOP;
        FOR v_field IN SELECT key,value FROM jsonb_each(v_item) WHERE key IN
            ('databaseId','cpuUs','durationUs','executionCount','logicalReads','writes','rowsProcessed') LOOP
            IF jsonb_typeof(v_field.value) IS DISTINCT FROM 'number' OR v_field.value::text !~ '^(0|[1-9][0-9]{0,18})$' THEN
                RAISE EXCEPTION 'Cache statement counter is invalid' USING ERRCODE='22023'; END IF;
        END LOOP;
        FOR v_field IN SELECT key,value FROM jsonb_each(v_item) WHERE key IN
            ('identityBeforeStartedUtc','identityBeforeCompletedUtc','identityAfterStartedUtc','identityAfterCompletedUtc') LOOP
            IF jsonb_typeof(v_field.value) IS DISTINCT FROM 'string' OR v_item->>v_field.key !~ 'Z$' THEN
                RAISE EXCEPTION 'Cache identity timestamp is not UTC' USING ERRCODE='22023'; END IF;
        END LOOP;
        v_database:=(v_item->>'databaseId')::integer; v_incarnation:=decode(v_item->>'databaseIncarnation','hex');
        v_query:=decode(v_item->>'queryFingerprint','hex'); v_plan:=decode(v_item->>'planFingerprint','hex');
        v_entry:=decode(v_item->>'cacheEntryFingerprint','hex');
        v_cpu:=(v_item->>'cpuUs')::bigint; v_duration:=(v_item->>'durationUs')::bigint;
        v_executions:=(v_item->>'executionCount')::bigint; v_reads:=(v_item->>'logicalReads')::bigint;
        v_writes:=(v_item->>'writes')::bigint; v_rows:=(v_item->>'rowsProcessed')::bigint;
        v_before_started:=(v_item->>'identityBeforeStartedUtc')::timestamptz;
        v_before_completed:=(v_item->>'identityBeforeCompletedUtc')::timestamptz;
        v_after_started:=(v_item->>'identityAfterStartedUtc')::timestamptz;
        v_after_completed:=(v_item->>'identityAfterCompletedUtc')::timestamptz;
        IF v_database NOT BETWEEN 5 AND 32766
            OR v_item->>'identityBeforeIncarnation' IS DISTINCT FROM v_item->>'databaseIncarnation'
            OR v_item->>'identityAfterIncarnation' IS DISTINCT FROM v_item->>'databaseIncarnation'
            OR v_item->>'identityBeforeEpoch' IS DISTINCT FROM p_payload->>'sourceEpoch'
            OR v_item->>'identityAfterEpoch' IS DISTINCT FROM p_payload->>'sourceEpoch'
            OR NOT isfinite(v_before_started) OR NOT isfinite(v_before_completed)
            OR NOT isfinite(v_after_started) OR NOT isfinite(v_after_completed)
            OR v_before_completed<v_before_started OR v_before_completed-v_before_started>interval '5 seconds'
            OR v_before_completed>v_started OR v_after_started<v_completed
            OR v_after_completed<v_after_started OR v_after_completed-v_after_started>interval '5 seconds'
            OR v_after_completed-v_before_started>interval '30 seconds' THEN
            RAISE EXCEPTION 'Cache statement identity bracket is invalid' USING ERRCODE='22023'; END IF;

        SELECT watermark.* INTO v_previous FROM events.plan_cache_runtime_watermark AS watermark
            WHERE watermark.instance_id=p_instance_id AND watermark.target_revision=p_target_revision
                AND watermark.database_id=v_database AND watermark.query_fingerprint=v_query
                AND watermark.cache_entry_fingerprint=v_entry
                AND watermark.observed_day>=date_trunc('day',v_now,'UTC')-interval '8 days'
            ORDER BY watermark.source_read_completed_at DESC LIMIT 1 FOR UPDATE;
        v_has_previous:=FOUND;
        v_transition:=CASE
            WHEN NOT v_has_previous THEN 'baseline_unavailable'
            WHEN v_completed<=v_previous.source_read_completed_at THEN 'stale'
            WHEN v_previous.database_incarnation<>v_incarnation OR v_previous.source_epoch<>v_epoch
                OR v_previous.plan_fingerprint<>v_plan THEN 'baseline_unavailable'
            WHEN v_completed-v_previous.source_read_completed_at>interval '7 days' THEN 'comparison_window_expired'
            WHEN v_cpu<v_previous.cpu_us OR v_duration<v_previous.duration_us
                OR v_executions<v_previous.execution_count OR v_reads<v_previous.logical_reads
                OR v_writes<v_previous.writes OR v_rows<v_previous.rows_processed THEN 'reset'
            ELSE 'comparable' END;
        IF v_transition<>'stale' THEN
            INSERT INTO events.plan_cache_runtime_watermark
                (observed_day,instance_id,target_revision,database_id,database_incarnation,source_epoch,
                 query_fingerprint,plan_fingerprint,cache_entry_fingerprint,cpu_us,duration_us,execution_count,
                 logical_reads,writes,rows_processed,source_read_started_at,source_read_completed_at,
                 identity_before_started_at,identity_before_completed_at,identity_after_started_at,identity_after_completed_at,last_run_id)
            VALUES(v_day,p_instance_id,p_target_revision,v_database,v_incarnation,v_epoch,v_query,v_plan,v_entry,
                v_cpu,v_duration,v_executions,v_reads,v_writes,v_rows,v_started,v_completed,
                v_before_started,v_before_completed,v_after_started,v_after_completed,p_run_id)
            ON CONFLICT(observed_day,instance_id,target_revision,database_id,query_fingerprint,cache_entry_fingerprint)
            DO UPDATE SET database_incarnation=excluded.database_incarnation,source_epoch=excluded.source_epoch,
                plan_fingerprint=excluded.plan_fingerprint,cpu_us=excluded.cpu_us,duration_us=excluded.duration_us,
                execution_count=excluded.execution_count,logical_reads=excluded.logical_reads,writes=excluded.writes,
                rows_processed=excluded.rows_processed,source_read_started_at=excluded.source_read_started_at,
                source_read_completed_at=excluded.source_read_completed_at,identity_before_started_at=excluded.identity_before_started_at,
                identity_before_completed_at=excluded.identity_before_completed_at,identity_after_started_at=excluded.identity_after_started_at,
                identity_after_completed_at=excluded.identity_after_completed_at,last_run_id=excluded.last_run_id,updated_at=v_now;
        END IF;
        INSERT INTO events.plan_cache_runtime_delta
            (committed_at,run_id,instance_id,target_revision,database_id,database_incarnation,source_epoch,
             query_fingerprint,plan_fingerprint,cache_entry_fingerprint,source_read_started_at,source_read_completed_at,
             identity_before_started_at,identity_before_completed_at,identity_after_started_at,identity_after_completed_at,
             transition,delta_from,delta_to,cpu_us,duration_us,execution_count,logical_reads,writes,rows_processed)
        VALUES(v_now,p_run_id,p_instance_id,p_target_revision,v_database,v_incarnation,v_epoch,v_query,v_plan,v_entry,v_started,v_completed,
            v_before_started,v_before_completed,v_after_started,v_after_completed,v_transition,
            CASE WHEN v_transition='comparable' THEN v_previous.source_read_completed_at END,
            CASE WHEN v_transition='comparable' THEN v_completed END,
            CASE WHEN v_transition='comparable' THEN v_cpu-v_previous.cpu_us END,
            CASE WHEN v_transition='comparable' THEN v_duration-v_previous.duration_us END,
            CASE WHEN v_transition='comparable' THEN v_executions-v_previous.execution_count END,
            CASE WHEN v_transition='comparable' THEN v_reads-v_previous.logical_reads END,
            CASE WHEN v_transition='comparable' THEN v_writes-v_previous.writes END,
            CASE WHEN v_transition='comparable' THEN v_rows-v_previous.rows_processed END);
    END LOOP;
    INSERT INTO events.plan_cache_runtime_receipt(committed_at,run_id,instance_id,target_revision,payload_digest,source_epoch,
        source_read_started_at,source_read_completed_at,candidate_limit,candidate_rows,unresolved_rows,invisible_database_rows,
        excluded_rows,emitted_rows,unqualified_rows,statement_rows,source_exhaustive,qualified_exhaustive)
    VALUES(v_now,p_run_id,p_instance_id,p_target_revision,v_digest,v_epoch,v_started,v_completed,
        v_limit,v_candidates,v_unresolved,v_invisible,v_excluded,v_emitted,v_unqualified,v_items,v_exhaustive,v_exhaustive AND v_unqualified=0);
    RETURN v_items;
END $batch$;
REVOKE ALL ON FUNCTION events.apply_plan_cache_runtime_batch(uuid,uuid,bigint,jsonb)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
