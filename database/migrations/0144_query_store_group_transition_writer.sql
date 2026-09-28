-- Private transition writer. A later canonical wrapper must fence and commit
-- the run, this batch, and schedule advancement in one transaction.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE events.query_store_group_delta
    DROP CONSTRAINT query_store_group_delta_wait_transition_check;
ALTER TABLE events.query_store_group_delta
    ADD CONSTRAINT query_store_group_delta_wait_transition_check CHECK
      (wait_transition IN ('unavailable','baseline_unavailable','comparable','reset','stale'));

CREATE FUNCTION events.apply_query_store_group_batch(
    p_run_id uuid, p_instance_id uuid, p_target_revision bigint, p_payload jsonb)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
#variable_conflict use_variable
DECLARE
    item jsonb;
    previous events.query_store_group_watermark%ROWTYPE;
    has_previous boolean;
    emitted integer := 0;
    database_id integer;
    incarnation uuid;
    query_hash bytea;
    plan_hash bytea;
    compile_at timestamptz;
    interval_id bigint;
    interval_start timestamptz;
    interval_end timestamptz;
    execution_type smallint;
    first_at timestamptz;
    last_at timestamptz;
    observed_at timestamptz;
    wait_at timestamptz;
    prior_wait_at timestamptz;
    current_wait jsonb;
    next_wait jsonb;
    wait_delta jsonb;
    runtime_transition text;
    wait_transition text;
    wait_coverage text;
    window_start timestamptz;
    window_end timestamptz;
    cpu_ms bigint;
    duration_ms bigint;
    executions bigint;
    logical_reads bigint;
    writes bigint;
    rows_processed bigint;
BEGIN
    IF p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL
       OR p_target_revision <= 0
       OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
       OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object'
       OR octet_length(p_payload::text) > 8388608
       OR NOT (p_payload ?& ARRAY['schemaVersion','targetId','targetRevision',
           'windowStartUtc','windowEndUtc','runtimeComplete','waitCoverage','items'])
       OR EXISTS (SELECT 1 FROM jsonb_each(p_payload) AS field(key,value)
                  WHERE field.value='null'::jsonb)
       OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1
       OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
       OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
       OR jsonb_typeof(p_payload->'items') IS DISTINCT FROM 'array'
       OR jsonb_array_length(p_payload->'items') > 2000
       OR p_payload->>'runtimeComplete' IS DISTINCT FROM 'true'
       OR p_payload->>'waitCoverage' IS NULL
       OR p_payload->>'waitCoverage' NOT IN ('complete','capture_off','unavailable','truncated')
       OR COALESCE(p_payload->>'windowStartUtc','') !~ 'Z$'
       OR COALESCE(p_payload->>'windowEndUtc','') !~ 'Z$'
       OR EXISTS (SELECT 1 FROM jsonb_object_keys(p_payload) AS field(name)
                  WHERE field.name NOT IN ('schemaVersion','targetId','targetRevision',
                    'windowStartUtc','windowEndUtc','runtimeComplete','waitCoverage','items'))
    THEN RAISE EXCEPTION 'Query Store group batch preflight rejected' USING ERRCODE='22023'; END IF;
    window_start := (p_payload->>'windowStartUtc')::timestamptz;
    window_end := (p_payload->>'windowEndUtc')::timestamptz;
    wait_coverage := p_payload->>'waitCoverage';
    IF NOT isfinite(window_start) OR NOT isfinite(window_end)
       OR window_end <= window_start OR window_end-window_start > interval '7 days'
       OR window_end > clock_timestamp()+interval '5 minutes'
    THEN RAISE EXCEPTION 'Query Store group source window rejected' USING ERRCODE='22023'; END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_payload->'items') AS source(value)
        GROUP BY (source.value->>'databaseId')::integer,
            (source.value->>'databaseIncarnation')::uuid,
            source.value->>'queryFingerprint',source.value->>'planFingerprint',
            (source.value->>'planInitialCompileUtc')::timestamptz,
            (source.value->>'sourceIntervalId')::bigint,
            (source.value->>'intervalStartUtc')::timestamptz,
            (source.value->>'intervalEndUtc')::timestamptz,
            (source.value->>'executionType')::smallint
        HAVING count(*)>1)
    THEN RAISE EXCEPTION 'Query Store group batch has a duplicate identity' USING ERRCODE='22023'; END IF;

    FOR item IN SELECT value FROM jsonb_array_elements(p_payload->'items') AS source(value) LOOP
        IF jsonb_typeof(item) IS DISTINCT FROM 'object'
           OR NOT (item ?& ARRAY['databaseId','databaseIncarnation','queryFingerprint',
               'planFingerprint','planInitialCompileUtc','sourceIntervalId',
               'intervalStartUtc','intervalEndUtc','executionType','cpuMs','durationMs',
               'executionCount','logicalReads','writes','rowsProcessed',
               'firstExecutionUtc','lastExecutionUtc','runtimeObservedUtc'])
           OR EXISTS (SELECT 1 FROM jsonb_each(item) AS field(key,value)
                      WHERE field.value='null'::jsonb)
           OR EXISTS (SELECT 1 FROM jsonb_object_keys(item) AS field(name)
                      WHERE field.name NOT IN ('databaseId','databaseIncarnation',
                        'queryFingerprint','planFingerprint','planInitialCompileUtc',
                        'sourceIntervalId','intervalStartUtc','intervalEndUtc',
                        'executionType','cpuMs','durationMs','executionCount',
                        'logicalReads','writes','rowsProcessed','firstExecutionUtc',
                        'lastExecutionUtc','runtimeObservedUtc','waitCategories',
                        'waitObservedUtc'))
           OR (item->>'databaseId')::integer NOT BETWEEN 1 AND 32767
           OR (item->>'databaseIncarnation')::uuid IS NULL
           OR item->>'databaseIncarnation' = '00000000-0000-0000-0000-000000000000'
           OR COALESCE(item->>'queryFingerprint','') !~ '^[0-9a-f]{64}$'
           OR COALESCE(item->>'planFingerprint','') !~ '^[0-9a-f]{64}$'
           OR COALESCE(item->>'planInitialCompileUtc','') !~ 'Z$'
           OR (item->>'sourceIntervalId')::bigint <= 0
           OR COALESCE(item->>'intervalStartUtc','') !~ 'Z$'
           OR COALESCE(item->>'intervalEndUtc','') !~ 'Z$'
           OR (item->>'executionType')::integer NOT BETWEEN 0 AND 10
           OR (item->>'cpuMs')::bigint < 0
           OR (item->>'durationMs')::bigint < 0
           OR (item->>'executionCount')::bigint < 0
           OR (item->>'logicalReads')::bigint < 0
           OR (item->>'writes')::bigint < 0
           OR (item->>'rowsProcessed')::bigint < 0
           OR COALESCE(item->>'firstExecutionUtc','') !~ 'Z$'
           OR COALESCE(item->>'lastExecutionUtc','') !~ 'Z$'
           OR COALESCE(item->>'runtimeObservedUtc','') !~ 'Z$'
           OR (wait_coverage='complete') IS DISTINCT FROM
              COALESCE(jsonb_typeof(item->'waitCategories')='object'
               AND item->>'waitObservedUtc' ~ 'Z$',false)
           OR (wait_coverage<>'complete' AND
               (item ? 'waitCategories' OR item ? 'waitObservedUtc'))
        THEN RAISE EXCEPTION 'Query Store group row schema rejected' USING ERRCODE='22023'; END IF;

        database_id := (item->>'databaseId')::integer;
        incarnation := (item->>'databaseIncarnation')::uuid;
        query_hash := decode(item->>'queryFingerprint','hex');
        plan_hash := decode(item->>'planFingerprint','hex');
        compile_at := (item->>'planInitialCompileUtc')::timestamptz;
        interval_id := (item->>'sourceIntervalId')::bigint;
        interval_start := (item->>'intervalStartUtc')::timestamptz;
        interval_end := (item->>'intervalEndUtc')::timestamptz;
        execution_type := (item->>'executionType')::smallint;
        cpu_ms := (item->>'cpuMs')::bigint;
        duration_ms := (item->>'durationMs')::bigint;
        executions := (item->>'executionCount')::bigint;
        logical_reads := (item->>'logicalReads')::bigint;
        writes := (item->>'writes')::bigint;
        rows_processed := (item->>'rowsProcessed')::bigint;
        first_at := (item->>'firstExecutionUtc')::timestamptz;
        last_at := (item->>'lastExecutionUtc')::timestamptz;
        observed_at := (item->>'runtimeObservedUtc')::timestamptz;
        current_wait := CASE WHEN wait_coverage='complete' THEN item->'waitCategories' ELSE NULL END;
        wait_at := CASE WHEN wait_coverage='complete' THEN
            (item->>'waitObservedUtc')::timestamptz ELSE NULL END;
        IF NOT isfinite(compile_at) OR NOT isfinite(interval_start) OR NOT isfinite(interval_end)
           OR NOT isfinite(first_at) OR NOT isfinite(last_at) OR NOT isfinite(observed_at)
           OR interval_end <= interval_start OR interval_end-interval_start > interval '7 days'
           OR interval_start >= window_end OR interval_end <= window_start
           OR last_at < first_at OR observed_at < window_start
           OR observed_at > clock_timestamp()+interval '5 minutes'
           OR (wait_at IS NOT NULL AND
               (NOT isfinite(wait_at) OR wait_at < window_start
                OR wait_at > clock_timestamp()+interval '5 minutes'))
           OR (current_wait IS NOT NULL AND
               ((SELECT count(*) FROM jsonb_object_keys(current_wait)) > 32 OR
                octet_length(current_wait::text) > 4096 OR
                EXISTS (SELECT 1 FROM jsonb_each(current_wait) AS category(key,value)
                        WHERE category.key !~ '^([0-9]|[12][0-9]|3[01])$'
                           OR jsonb_typeof(category.value)<>'number'
                           OR category.value::text !~ '^[0-9]+$'
                           OR (category.value::text)::numeric > 9223372036854775807)))
        THEN RAISE EXCEPTION 'Query Store group row bounds rejected' USING ERRCODE='22023'; END IF;

        SELECT * INTO previous FROM events.query_store_group_watermark AS watermark
        WHERE watermark.interval_end=interval_end AND watermark.instance_id=p_instance_id
          AND watermark.target_revision=p_target_revision AND watermark.database_id=database_id
          AND watermark.database_incarnation=incarnation AND watermark.query_fingerprint=query_hash
          AND watermark.plan_fingerprint=plan_hash AND watermark.plan_initial_compile_at=compile_at
          AND watermark.source_interval_id=interval_id AND watermark.interval_start=interval_start
          AND watermark.execution_type=execution_type
        FOR UPDATE;
        has_previous := FOUND;
        IF has_previous AND observed_at <= previous.runtime_observed_at THEN CONTINUE; END IF;
        IF NOT has_previous THEN runtime_transition := 'baseline_unavailable';
        ELSIF cpu_ms < previous.cpu_ms OR duration_ms < previous.duration_ms
           OR executions < previous.execution_count OR logical_reads < previous.logical_reads
           OR writes < previous.writes OR rows_processed < previous.rows_processed
        THEN runtime_transition := 'reset';
        ELSIF first_at > previous.last_execution_at THEN runtime_transition := 'epoch_ambiguous';
        ELSE runtime_transition := 'comparable'; END IF;

        next_wait := CASE WHEN has_previous AND runtime_transition='comparable'
            THEN previous.wait_categories ELSE NULL END;
        prior_wait_at := CASE WHEN has_previous AND runtime_transition='comparable'
            THEN previous.wait_observed_at ELSE NULL END;
        wait_delta := NULL;
        wait_transition := 'unavailable';
        IF current_wait IS NOT NULL THEN
            IF next_wait IS NULL OR prior_wait_at IS NULL
            THEN wait_transition := 'baseline_unavailable';
            ELSIF wait_at <= prior_wait_at
            THEN wait_transition := 'stale';
            ELSIF EXISTS (
                SELECT 1 FROM jsonb_each_text(next_wait) AS category(key,value)
                WHERE COALESCE((current_wait->>category.key)::bigint,0) < category.value::bigint)
            THEN wait_transition := 'reset';
            ELSE
                wait_transition := 'comparable';
                SELECT COALESCE(jsonb_object_agg(category.key,
                    COALESCE((current_wait->>category.key)::bigint,0)
                    - COALESCE((next_wait->>category.key)::bigint,0)), '{}'::jsonb)
                INTO wait_delta
                FROM (SELECT jsonb_object_keys(current_wait) AS key
                      UNION SELECT jsonb_object_keys(next_wait) AS key) AS category;
            END IF;
            IF wait_at > COALESCE(prior_wait_at,'-infinity'::timestamptz) THEN
                next_wait := current_wait;
                prior_wait_at := wait_at;
            END IF;
        END IF;

        IF has_previous THEN
            UPDATE events.query_store_group_watermark AS watermark SET
                cpu_ms=cpu_ms,duration_ms=duration_ms,execution_count=executions,
                logical_reads=logical_reads,writes=writes,rows_processed=rows_processed,
                first_execution_at=first_at,last_execution_at=last_at,
                runtime_observed_at=observed_at,wait_categories=next_wait,
                wait_observed_at=prior_wait_at,last_run_id=p_run_id,updated_at=clock_timestamp()
            WHERE watermark.interval_end=interval_end AND watermark.instance_id=p_instance_id
              AND watermark.target_revision=p_target_revision AND watermark.database_id=database_id
              AND watermark.database_incarnation=incarnation AND watermark.query_fingerprint=query_hash
              AND watermark.plan_fingerprint=plan_hash AND watermark.plan_initial_compile_at=compile_at
              AND watermark.source_interval_id=interval_id AND watermark.interval_start=interval_start
              AND watermark.execution_type=execution_type;
        ELSE
            INSERT INTO events.query_store_group_watermark
                (instance_id,target_revision,database_id,database_incarnation,
                 query_fingerprint,plan_fingerprint,plan_initial_compile_at,
                 source_interval_id,interval_start,interval_end,execution_type,
                 cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed,
                 first_execution_at,last_execution_at,runtime_observed_at,
                 wait_categories,wait_observed_at,last_run_id)
            VALUES (p_instance_id,p_target_revision,database_id,incarnation,
                query_hash,plan_hash,compile_at,interval_id,interval_start,interval_end,
                execution_type,cpu_ms,duration_ms,executions,logical_reads,writes,
                rows_processed,first_at,last_at,observed_at,next_wait,prior_wait_at,p_run_id);
        END IF;
        INSERT INTO events.query_store_group_delta
            (observed_at,run_id,instance_id,target_revision,database_id,database_incarnation,
             query_fingerprint,plan_fingerprint,plan_initial_compile_at,source_interval_id,
             interval_start,interval_end,execution_type,source_window_start,source_window_end,
             runtime_transition,cpu_ms,duration_ms,execution_count,logical_reads,writes,
             rows_processed,wait_transition,wait_delta,wait_observed_at)
        VALUES (observed_at,p_run_id,p_instance_id,p_target_revision,database_id,incarnation,
            query_hash,plan_hash,compile_at,interval_id,interval_start,interval_end,
            execution_type,window_start,window_end,runtime_transition,
            CASE WHEN runtime_transition='comparable' THEN cpu_ms-previous.cpu_ms END,
            CASE WHEN runtime_transition='comparable' THEN duration_ms-previous.duration_ms END,
            CASE WHEN runtime_transition='comparable' THEN executions-previous.execution_count END,
            CASE WHEN runtime_transition='comparable' THEN logical_reads-previous.logical_reads END,
            CASE WHEN runtime_transition='comparable' THEN writes-previous.writes END,
            CASE WHEN runtime_transition='comparable' THEN rows_processed-previous.rows_processed END,
            wait_transition,wait_delta,CASE WHEN current_wait IS NOT NULL THEN wait_at END);
        emitted := emitted+1;
    END LOOP;
    RETURN emitted;
END;
$fn$;
REVOKE ALL ON FUNCTION events.apply_query_store_group_batch(uuid,uuid,bigint,jsonb)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
