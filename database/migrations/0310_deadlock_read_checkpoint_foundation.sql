-- Private incremental XE state and a fenced read. No writable Collector
-- checkpoint API, collector activation or alert source switch is installed.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.validate_deadlock_read_window(p_window jsonb)
RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $validate$
DECLARE v_read timestamptz; v_from timestamptz; v_gap timestamptz; v_count integer; v_bad boolean;
BEGIN
    IF jsonb_typeof(p_window) IS DISTINCT FROM 'object' OR octet_length(p_window::text)>65536
        OR (SELECT count(*) FROM jsonb_object_keys(p_window))<>9
        OR NOT p_window ?& ARRAY['schemaVersion','targetId','targetRevision','sourceEpoch',
            'readAtUtc','coveredFromUtc','gapThroughUtc','reason','events']
        OR p_window->'schemaVersion' IS DISTINCT FROM '1'::jsonb
        OR jsonb_typeof(p_window->'targetId') IS DISTINCT FROM 'string'
        OR (p_window->>'targetId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
        OR p_window->>'targetId' IS DISTINCT FROM ((p_window->>'targetId')::uuid)::text
        OR jsonb_typeof(p_window->'targetRevision') IS DISTINCT FROM 'number'
        OR (p_window->>'targetRevision')::bigint<=0
        OR jsonb_typeof(p_window->'sourceEpoch') IS DISTINCT FROM 'string'
        OR p_window->>'sourceEpoch' !~ '^[0-9a-f]{64}$'
        OR jsonb_typeof(p_window->'readAtUtc') IS DISTINCT FROM 'string'
        OR jsonb_typeof(p_window->'coveredFromUtc') IS DISTINCT FROM 'string'
        OR p_window->>'readAtUtc' !~ 'Z$' OR p_window->>'coveredFromUtc' !~ 'Z$'
        OR jsonb_typeof(p_window->'gapThroughUtc') NOT IN ('null','string')
        OR (jsonb_typeof(p_window->'gapThroughUtc')='string' AND p_window->>'gapThroughUtc' !~ 'Z$')
        OR jsonb_typeof(p_window->'reason') IS DISTINCT FROM 'string'
        OR p_window->>'reason' NOT IN ('current','catching_up','source_incomplete','coverage_gap','window_overflow')
        OR jsonb_typeof(p_window->'events') IS DISTINCT FROM 'array'
    THEN RAISE EXCEPTION 'Deadlock read window shape is invalid' USING ERRCODE='22023'; END IF;
    v_read:=(p_window->>'readAtUtc')::timestamptz;
    v_from:=(p_window->>'coveredFromUtc')::timestamptz;
    v_gap:=(p_window->>'gapThroughUtc')::timestamptz;
    v_count:=jsonb_array_length(p_window->'events');
    IF NOT isfinite(v_read) OR NOT isfinite(v_from) OR (v_gap IS NOT NULL AND NOT isfinite(v_gap))
        OR v_read<'0001-01-01 00:05:00Z'::timestamptz OR v_read>='10000-01-01Z'::timestamptz
        OR v_from<'0001-01-01Z'::timestamptz OR v_from>v_read
        OR (v_gap IS NOT NULL AND (v_gap<'0001-01-01Z'::timestamptz OR v_gap>v_read))
        OR v_count>256
    THEN RAISE EXCEPTION 'Deadlock read window time or count is invalid' USING ERRCODE='22023'; END IF;
    IF EXISTS(SELECT 1 FROM jsonb_array_elements(p_window->'events') e
        WHERE jsonb_typeof(e) IS DISTINCT FROM 'object'
            OR (SELECT count(*) FROM jsonb_object_keys(e))<>3
            OR NOT e ?& ARRAY['eventId','occurredAtUtc','parseTruncated']
            OR jsonb_typeof(e->'eventId') IS DISTINCT FROM 'string'
            OR (e->>'eventId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
            OR e->>'eventId' IS DISTINCT FROM ((e->>'eventId')::uuid)::text
            OR jsonb_typeof(e->'occurredAtUtc') IS DISTINCT FROM 'string' OR e->>'occurredAtUtc' !~ 'Z$'
            OR NOT isfinite((e->>'occurredAtUtc')::timestamptz)
            OR (e->>'occurredAtUtc')::timestamptz NOT BETWEEN v_read-interval '5 minutes' AND v_read
            OR jsonb_typeof(e->'parseTruncated') IS DISTINCT FROM 'boolean')
        OR (SELECT count(DISTINCT e->>'eventId') FROM jsonb_array_elements(p_window->'events') e)<>v_count
    THEN RAISE EXCEPTION 'Deadlock read window events are invalid' USING ERRCODE='22023'; END IF;
    v_bad:=v_from>v_read-interval '5 minutes' OR coalesce(v_gap>=v_read-interval '5 minutes',false);
    IF (p_window->>'reason'='current' AND (v_bad OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_window->'events') e
            WHERE (e->>'parseTruncated')::boolean)))
        OR (p_window->>'reason'='coverage_gap' AND NOT v_bad)
        OR (p_window->>'reason' IN ('source_incomplete','window_overflow') AND v_gap IS DISTINCT FROM v_read)
        OR (p_window->>'reason'='window_overflow' AND v_count<>256)
    THEN RAISE EXCEPTION 'Deadlock read window coverage is contradictory' USING ERRCODE='22023'; END IF;
    RETURN true;
END $validate$;
REVOKE ALL ON FUNCTION control.validate_deadlock_read_window(jsonb)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.merge_deadlock_read_window(
    p_previous jsonb,p_instance uuid,p_revision bigint,p_epoch bytea,p_read_at timestamptz,
    p_retained_from timestamptz,p_event_ids uuid[],p_occurred_ats timestamptz[],
    p_truncated boolean[],p_reached_end boolean,p_source_loss boolean)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $merge$
DECLARE v_previous jsonb:=p_previous; v_from timestamptz; v_gap timestamptz;
    v_start timestamptz; v_n integer; v_loss boolean:=p_source_loss; v_overflow boolean;
    v_reason text; v_events jsonb; v_result jsonb;
BEGIN
    IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'::uuid
        OR p_revision IS NULL OR p_revision<=0 OR octet_length(p_epoch) IS DISTINCT FROM 32
        OR p_read_at IS NULL OR NOT isfinite(p_read_at) OR p_read_at<'0001-01-01 00:05:00Z'::timestamptz
        OR p_read_at>='10000-01-01Z'::timestamptz OR p_reached_end IS NULL OR p_source_loss IS NULL
        OR cardinality(p_event_ids) IS NULL OR cardinality(p_event_ids)>256
        OR cardinality(p_occurred_ats) IS DISTINCT FROM cardinality(p_event_ids)
        OR cardinality(p_truncated) IS DISTINCT FROM cardinality(p_event_ids)
        OR coalesce(array_ndims(p_event_ids),1)>1 OR coalesce(array_ndims(p_occurred_ats),1)>1
        OR coalesce(array_ndims(p_truncated),1)>1
        OR EXISTS(SELECT 1 FROM unnest(p_event_ids,p_occurred_ats,p_truncated) e(id,at,truncated)
            WHERE e.id IS NULL OR e.id='00000000-0000-0000-0000-000000000000'::uuid OR e.at IS NULL
                OR NOT isfinite(e.at) OR e.at<'0001-01-01Z'::timestamptz OR e.at>p_read_at OR e.truncated IS NULL)
    THEN RAISE EXCEPTION 'Deadlock read merge input is invalid' USING ERRCODE='22023'; END IF;
    IF v_previous IS NOT NULL THEN
        PERFORM control.validate_deadlock_read_window(v_previous);
        IF v_previous->>'targetId'<>p_instance::text OR p_read_at<=(v_previous->>'readAtUtc')::timestamptz
        THEN RAISE EXCEPTION 'Deadlock read merge target or time does not advance' USING ERRCODE='22023'; END IF;
        IF (v_previous->>'targetRevision')::bigint<>p_revision OR v_previous->>'sourceEpoch'<>encode(p_epoch,'hex')
        THEN v_previous:=NULL; END IF;
    END IF;
    IF v_previous IS NULL AND (p_retained_from IS NULL OR NOT isfinite(p_retained_from)
        OR p_retained_from<'0001-01-01Z'::timestamptz OR p_retained_from>p_read_at)
    THEN RAISE EXCEPTION 'A cold deadlock read needs its proven retention boundary' USING ERRCODE='22023'; END IF;
    v_from:=coalesce((v_previous->>'coveredFromUtc')::timestamptz,p_retained_from);
    v_gap:=(v_previous->>'gapThroughUtc')::timestamptz;
    v_start:=p_read_at-interval '5 minutes';
    IF EXISTS(SELECT 1 FROM unnest(p_truncated) t(value) WHERE t.value) THEN v_loss:=true; END IF;
    IF EXISTS
    (
        WITH retained AS
        (
            SELECT (e->>'eventId')::uuid AS id,(e->>'occurredAtUtc')::timestamptz AS at,
                (e->>'parseTruncated')::boolean AS truncated FROM jsonb_array_elements(v_previous->'events') e
            WHERE (e->>'occurredAtUtc')::timestamptz>=v_start
        ), incoming AS
        (
            SELECT * FROM unnest(p_event_ids,p_occurred_ats,p_truncated) WITH ORDINALITY e(id,at,truncated,ordinal)
        )
        SELECT 1 FROM retained r JOIN incoming n ON n.id=r.id WHERE (r.at,r.truncated) IS DISTINCT FROM (n.at,n.truncated)
        UNION ALL SELECT 1 FROM incoming a JOIN incoming b ON a.id=b.id AND a.ordinal<b.ordinal
            WHERE a.at>=v_start AND (a.at,a.truncated) IS DISTINCT FROM (b.at,b.truncated)
    ) THEN RAISE EXCEPTION 'Deadlock event identity has conflicting evidence' USING ERRCODE='22023'; END IF;
    WITH retained AS
    (
        SELECT (e->>'eventId')::uuid AS id,(e->>'occurredAtUtc')::timestamptz AS at,
            (e->>'parseTruncated')::boolean AS truncated FROM jsonb_array_elements(v_previous->'events') e
        WHERE (e->>'occurredAtUtc')::timestamptz>=v_start
        UNION SELECT e.id,e.at,e.truncated FROM unnest(p_event_ids,p_occurred_ats,p_truncated) e(id,at,truncated)
            WHERE e.at>=v_start
    ) SELECT count(*)::integer INTO v_n FROM retained;
    v_overflow:=v_n>256;
    IF v_loss OR v_overflow THEN v_gap:=p_read_at; END IF;
    WITH retained AS
    (
        SELECT (e->>'eventId')::uuid AS id,(e->>'occurredAtUtc')::timestamptz AS at,
            (e->>'parseTruncated')::boolean AS truncated FROM jsonb_array_elements(v_previous->'events') e
        WHERE (e->>'occurredAtUtc')::timestamptz>=v_start
        UNION SELECT e.id,e.at,e.truncated FROM unnest(p_event_ids,p_occurred_ats,p_truncated) e(id,at,truncated)
            WHERE e.at>=v_start
    ), bounded AS (SELECT * FROM retained ORDER BY at DESC,id LIMIT 256)
    SELECT coalesce(jsonb_agg(jsonb_build_object('eventId',id,'occurredAtUtc',
        to_char(at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'parseTruncated',truncated)
        ORDER BY at DESC,id),'[]'::jsonb) INTO v_events FROM bounded;
    v_reason:=CASE WHEN v_overflow THEN 'window_overflow' WHEN v_loss THEN 'source_incomplete'
        WHEN NOT p_reached_end THEN 'catching_up'
        WHEN v_from>v_start OR v_gap>=v_start THEN 'coverage_gap' ELSE 'current' END;
    v_result:=jsonb_build_object('schemaVersion',1,'targetId',p_instance,'targetRevision',p_revision,
        'sourceEpoch',encode(p_epoch,'hex'),'readAtUtc',to_char(p_read_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
        'coveredFromUtc',to_char(v_from AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
        'gapThroughUtc',CASE WHEN v_gap IS NOT NULL THEN to_char(v_gap AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') END,
        'reason',v_reason,'events',v_events);
    PERFORM control.validate_deadlock_read_window(v_result);
    RETURN v_result;
END $merge$;
REVOKE ALL ON FUNCTION control.merge_deadlock_read_window(jsonb,uuid,bigint,bytea,timestamptz,timestamptz,uuid[],timestamptz[],boolean[],boolean,boolean)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.deadlock_read_checkpoint_body(
    p_instance uuid,p_revision bigint,p_schedule bigint,p_run uuid,p_completed timestamptz,p_completion bytea,
    p_source_state text,p_epoch bytea,p_generation bigint,p_index integer,p_offset bigint,p_window jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE PARALLEL SAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $body$
    SELECT jsonb_build_object('schemaVersion',1,'targetId',p_instance,'targetRevision',p_revision,
        'scheduleRevision',p_schedule,'runId',p_run,'completedAtUtc',
        to_char(p_completed AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
        'completionDigest',encode(p_completion,'hex'),'sourceState',p_source_state,'sourceEpoch',encode(p_epoch,'hex'),
        'bookmark',CASE WHEN p_generation IS NOT NULL THEN jsonb_build_object('generation',p_generation,
            'rolloverIndex',p_index,'bufferOffset',p_offset) END,'window',p_window);
$body$;
REVOKE ALL ON FUNCTION control.deadlock_read_checkpoint_body(uuid,bigint,bigint,uuid,timestamptz,bytea,text,bytea,bigint,integer,bigint,jsonb)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.deadlock_read_checkpoint_digest(
    p_instance uuid,p_revision bigint,p_schedule bigint,p_run uuid,p_completed timestamptz,p_completion bytea,
    p_source_state text,p_epoch bytea,p_generation bigint,p_index integer,p_offset bigint,p_window jsonb)
RETURNS bytea LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $digest$
BEGIN
    RETURN sha256(convert_to(control.deadlock_read_checkpoint_body(p_instance,p_revision,p_schedule,p_run,
        p_completed,p_completion,p_source_state,p_epoch,p_generation,p_index,p_offset,p_window)::text,'UTF8'));
END $digest$;
REVOKE ALL ON FUNCTION control.deadlock_read_checkpoint_digest(uuid,bigint,bigint,uuid,timestamptz,bytea,text,bytea,bigint,integer,bigint,jsonb)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE TABLE control.deadlock_read_checkpoint
(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id) ON DELETE CASCADE,
    target_revision bigint NOT NULL CHECK(target_revision>0),
    schedule_revision bigint NOT NULL CHECK(schedule_revision>0),
    run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id) ON DELETE CASCADE,
    completed_at timestamptz NOT NULL CHECK(isfinite(completed_at)),
    completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32),
    source_state text NOT NULL CHECK(source_state IN ('ready','missing','config_invalid','reset_required','failed')),
    source_epoch bytea NULL CHECK(source_epoch IS NULL OR octet_length(source_epoch)=32),
    file_generation bigint NULL,rollover_index integer NULL,buffer_offset bigint NULL,
    window_state jsonb NULL,
    checkpoint_digest bytea GENERATED ALWAYS AS
        (control.deadlock_read_checkpoint_digest(instance_id,target_revision,schedule_revision,run_id,
            completed_at,completion_digest,source_state,source_epoch,file_generation,rollover_index,buffer_offset,window_state)) STORED,
    CHECK((file_generation IS NULL AND rollover_index IS NULL AND buffer_offset IS NULL)
        OR (source_epoch IS NOT NULL AND file_generation>0 AND rollover_index>=0 AND buffer_offset BETWEEN 0 AND 105906176
            AND file_generation IS NOT NULL AND rollover_index IS NOT NULL AND buffer_offset IS NOT NULL)),
    CHECK(source_state='ready' OR window_state IS NULL OR window_state->>'reason'='source_incomplete'),
    CHECK(source_state<>'reset_required' OR file_generation IS NULL),
    CHECK((source_epoch IS NULL AND window_state IS NULL AND file_generation IS NULL AND source_state<>'ready')
        OR (source_epoch IS NOT NULL AND window_state IS NOT NULL AND control.validate_deadlock_read_window(window_state)
            AND window_state->>'targetId'=instance_id::text AND (window_state->>'targetRevision')::bigint=target_revision
            AND window_state->>'sourceEpoch'=encode(source_epoch,'hex')
            AND (window_state->>'readAtUtc')::timestamptz=completed_at))
);
ALTER TABLE control.deadlock_read_checkpoint ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.deadlock_read_checkpoint FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlock_read_checkpoint_owner ON control.deadlock_read_checkpoint
    FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON control.deadlock_read_checkpoint FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.read_deadlock_read_checkpoint(
    p_instance uuid,p_revision bigint,p_schedule bigint,p_run uuid,p_owner uuid,p_fencing bigint)
RETURNS TABLE(read_at timestamptz,payload jsonb,source_digest text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $read$
DECLARE v_key text; v_run record; v_prior record; v_saved record; v_schedule record; v_outcome record; v_scan record; v_body jsonb; v_checkpoint jsonb;
    v_digest text; v_resume boolean:=false; v_now timestamptz;
BEGIN
    IF p_instance IS NULL OR p_revision IS NULL OR p_revision<=0 OR p_schedule IS NULL OR p_schedule<=0
        OR p_run IS NULL OR p_owner IS NULL OR p_fencing IS NULL OR p_fencing<=0
        OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
    THEN RAISE EXCEPTION 'Deadlock checkpoint read scope is invalid' USING ERRCODE='42501'; END IF;
    v_key:='collector/run/deadlocks.system-health/'||replace(p_instance::text,'-','');
    PERFORM control.assert_worker_lease(v_key,p_owner,p_fencing);
    PERFORM 1 FROM control.observation_target t WHERE t.instance_id=p_instance AND t.revision=p_revision
        AND t.lifecycle_state='active' FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock checkpoint current target is required' USING ERRCODE='42501'; END IF;
    SELECT s.* INTO v_schedule FROM control.collector_schedule s WHERE s.instance_id=p_instance AND s.collector_id='deadlocks.system-health'
        AND s.target_revision=p_revision AND s.schedule_revision=p_schedule AND s.collector_version=2
        AND s.enabled AND s.active_run_id=p_run
        AND EXISTS(SELECT 1 FROM control.collector_contract c WHERE c.collector_id=s.collector_id
            AND c.collector_version=2 AND c.output_schema_version=2) FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Deadlock checkpoint exact active v2 schedule is required' USING ERRCODE='42501'; END IF;
    SELECT r.* INTO v_run FROM telemetry.lookup_collection_run_runtime(p_run) r;
    IF NOT FOUND OR v_run.instance_id<>p_instance OR v_run.target_revision<>p_revision
        OR v_run.schedule_revision<>p_schedule OR v_run.collector_id<>'deadlocks.system-health'
        OR v_run.collector_version<>2 OR v_run.output_schema_version<>2 OR v_run.work_key<>v_key
        OR v_run.owner_execution_id<>p_owner OR v_run.fencing_token<>p_fencing
        OR v_run.scheduled_for IS DISTINCT FROM v_schedule.next_due_at OR octet_length(v_run.request_digest) IS DISTINCT FROM 32
        OR EXISTS(SELECT 1 FROM telemetry.collection_run_outcome o WHERE o.run_id=p_run)
    THEN RAISE EXCEPTION 'Deadlock checkpoint active run identity is invalid' USING ERRCODE='42501'; END IF;
    v_now:=clock_timestamp();
    SELECT c.* INTO v_saved FROM control.deadlock_read_checkpoint c WHERE c.instance_id=p_instance FOR SHARE;
    IF FOUND THEN
        SELECT r.* INTO v_prior FROM telemetry.lookup_collection_run_runtime(v_saved.run_id) r;
        IF NOT FOUND OR v_prior.instance_id<>p_instance OR v_prior.target_revision<>v_saved.target_revision
            OR v_prior.schedule_revision<>v_saved.schedule_revision OR v_prior.collector_id<>'deadlocks.system-health'
            OR v_prior.collector_version<>2 OR v_prior.output_schema_version<>2
        THEN RAISE EXCEPTION 'Deadlock checkpoint canonical outcome binding is invalid' USING ERRCODE='55000'; END IF;
        SELECT o.* INTO v_outcome FROM telemetry.collection_run_outcome o WHERE o.run_id=v_saved.run_id;
        IF NOT FOUND OR v_outcome.completed_at<>v_saved.completed_at OR v_outcome.completion_digest<>v_saved.completion_digest
            OR (v_saved.source_state='ready' AND v_outcome.outcome NOT IN ('succeeded','partial'))
            OR (v_saved.window_state->>'reason'='current' AND (v_outcome.outcome<>'succeeded' OR v_outcome.loss_detected
                OR v_outcome.truncated OR v_outcome.loss_kind<>'none' OR NOT v_outcome.loss_count_exact
                OR v_outcome.rejected_item_count<>0 OR v_outcome.lost_row_count<>0 OR v_outcome.lost_byte_count<>0))
        THEN RAISE EXCEPTION 'Deadlock checkpoint canonical coverage binding is invalid' USING ERRCODE='55000'; END IF;
        IF v_saved.target_revision=p_revision AND v_saved.schedule_revision=p_schedule
            AND v_schedule.last_completed_at IS DISTINCT FROM v_saved.completed_at
        THEN RAISE EXCEPTION 'Latest canonical deadlock completion has no matching checkpoint' USING ERRCODE='55000'; END IF;
        -- Only evidence that could survive the next merge needs retained event
        -- rows. Expired cached events may already have been removed by retention.
        IF v_saved.target_revision=p_revision AND v_saved.schedule_revision=p_schedule
            AND EXISTS(SELECT 1 FROM jsonb_array_elements(v_saved.window_state->'events') e
                WHERE (e->>'occurredAtUtc')::timestamptz>=v_now-interval '5 minutes'
                    AND NOT EXISTS(SELECT 1 FROM events.deadlock_summary d WHERE d.instance_id=p_instance
                        AND d.event_id=(e->>'eventId')::uuid AND d.occurred_at=(e->>'occurredAtUtc')::timestamptz
                        AND d.parse_truncated=(e->>'parseTruncated')::boolean))
        THEN RAISE EXCEPTION 'Deadlock checkpoint retained evidence is not canonical' USING ERRCODE='55000'; END IF;
        IF v_saved.window_state->>'reason'='current' AND v_saved.target_revision=p_revision AND v_saved.schedule_revision=p_schedule THEN
            SELECT s.* INTO v_scan FROM alerting.deadlock_source_snapshot s WHERE s.instance_id=p_instance;
            IF NOT FOUND OR v_scan.run_id<>v_saved.run_id OR v_scan.target_revision<>v_saved.target_revision
                OR v_scan.schedule_revision<>v_saved.schedule_revision OR v_scan.completed_at<>v_saved.completed_at
                OR v_scan.completion_digest<>v_saved.completion_digest
                OR EXISTS(SELECT 1 FROM unnest(v_scan.event_ids,v_scan.occurred_ats,v_scan.parse_truncated) e(id,at,truncated)
                    WHERE e.at>=greatest(v_saved.completed_at-interval '5 minutes',v_now-interval '5 minutes')
                        AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_saved.window_state->'events') retained
                            WHERE (retained->>'eventId')::uuid=e.id AND (retained->>'occurredAtUtc')::timestamptz=e.at
                                AND (retained->>'parseTruncated')::boolean=e.truncated))
            THEN RAISE EXCEPTION 'Current deadlock checkpoint does not match canonical event evidence' USING ERRCODE='55000'; END IF;
        END IF;
        v_checkpoint:=control.deadlock_read_checkpoint_body(p_instance,v_saved.target_revision,v_saved.schedule_revision,
            v_saved.run_id,v_saved.completed_at,v_saved.completion_digest,v_saved.source_state,v_saved.source_epoch,
            v_saved.file_generation,v_saved.rollover_index,v_saved.buffer_offset,v_saved.window_state);
        v_digest:=encode(v_saved.checkpoint_digest,'hex');
        v_resume:=v_saved.target_revision=p_revision AND v_saved.schedule_revision=p_schedule AND v_saved.source_epoch IS NOT NULL;
    END IF;
    v_now:=clock_timestamp();
    v_body:=jsonb_build_object('schemaVersion',1,'targetId',p_instance,'targetRevision',p_revision,
        'scheduleRevision',p_schedule,'runId',p_run,'checkpoint',v_checkpoint,'checkpointDigest',v_digest,'resumeAllowed',v_resume);
    PERFORM control.assert_worker_lease(v_key,p_owner,p_fencing);
    RETURN QUERY SELECT v_now,v_body,encode(sha256(convert_to(v_body::text,'UTF8')),'hex');
END $read$;
REVOKE ALL ON FUNCTION control.read_deadlock_read_checkpoint(uuid,bigint,bigint,uuid,uuid,bigint)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.read_deadlock_read_checkpoint(uuid,bigint,bigint,uuid,uuid,bigint) TO sqlobserver_collector;
