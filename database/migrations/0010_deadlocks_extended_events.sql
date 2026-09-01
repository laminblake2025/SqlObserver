-- Transactional M6 migration. Passive evidence only: this migration never creates or mutates SQL Server XE state.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

INSERT INTO control.collector_contract
(
    collector_id,collector_version,execution_order,manifest_schema_version,output_schema_version,
    manifest_sha256,asset_bundle_sha256,default_interval,minimum_interval,execution_timeout,
    maximum_rows,maximum_response_bytes,estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval
)
VALUES
(
    'deadlocks.system-health',1,8,4,1,
    decode('5f3b0a9fef5a7d06f37cea5063e39a8b1b7e20dbec7c8234f84f521d41ca0a60','hex'),
    decode('72570fba287327e1dec64a56d6211b9c24a7d597b35010c9b9ac765615d2963f','hex'),
    interval '30 seconds', interval '10 seconds', interval '5 seconds',
    257,1048576,'moderate',2,3,interval '5 minutes'
);
INSERT INTO control.collector_dependency(collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES
 -- capability.connection is control-plane discovery, not a scheduled
 -- collector contract, so it remains a manifest-only prerequisite and must
 -- not enter the foreign-key-backed scheduled dependency registry.
 ('deadlocks.system-health',1,'engine.core',1),('deadlocks.system-health',1,'activity.sessions',1),
 ('deadlocks.system-health',1,'activity.requests',1),('deadlocks.system-health',1,'waits.server',1),
 ('deadlocks.system-health',1,'blocking.current',1);

CREATE TABLE events.deadlock_summary
(
    occurred_at timestamptz NOT NULL,
    event_id uuid NOT NULL,
    collection_run_id uuid NOT NULL REFERENCES telemetry.collection_run(run_id),
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    fingerprint bytea NOT NULL,
    participant_count integer NOT NULL,
    relation_count integer NOT NULL,
    parse_truncated boolean NOT NULL DEFAULT false,
    collected_at timestamptz NOT NULL,
    CONSTRAINT pk_deadlock_summary PRIMARY KEY (occurred_at, event_id),
    CONSTRAINT fk_deadlock_summary_envelope FOREIGN KEY (occurred_at, event_id) REFERENCES events.diagnostic_event(occurred_at, event_id) ON DELETE CASCADE,
    CONSTRAINT uq_deadlock_summary_fingerprint UNIQUE (instance_id, fingerprint, occurred_at),
    CONSTRAINT ck_deadlock_summary_fingerprint CHECK (octet_length(fingerprint) = 32),
    CONSTRAINT ck_deadlock_summary_counts CHECK (participant_count BETWEEN 0 AND 128 AND relation_count BETWEEN 0 AND 256)
) ;
CREATE INDEX ix_deadlock_summary_instance_time ON events.deadlock_summary(instance_id, occurred_at DESC, event_id DESC);

CREATE TABLE events.deadlock_participant
(
    occurred_at timestamptz NOT NULL,
    event_id uuid NOT NULL,
    session_id integer NOT NULL,
    is_victim boolean NOT NULL,
    PRIMARY KEY (occurred_at, event_id, session_id),
    FOREIGN KEY (occurred_at, event_id) REFERENCES events.deadlock_summary(occurred_at, event_id) ON DELETE CASCADE,
    CONSTRAINT ck_deadlock_participant_session CHECK (session_id BETWEEN 1 AND 32767)
);
CREATE TABLE events.deadlock_relation
(
    occurred_at timestamptz NOT NULL,
    event_id uuid NOT NULL,
    blocker_session_id integer NOT NULL,
    waiter_session_id integer NOT NULL,
    resource_category text NOT NULL,
    lock_mode text NOT NULL,
    PRIMARY KEY (occurred_at, event_id, blocker_session_id, waiter_session_id, resource_category, lock_mode),
    FOREIGN KEY (occurred_at, event_id) REFERENCES events.deadlock_summary(occurred_at, event_id) ON DELETE CASCADE,
    CONSTRAINT ck_deadlock_relation_sessions CHECK (blocker_session_id BETWEEN 1 AND 32767 AND waiter_session_id BETWEEN 1 AND 32767),
    CONSTRAINT ck_deadlock_relation_category CHECK (resource_category IN ('key','page','object_lock','metadata','exchange','other')),
    CONSTRAINT ck_deadlock_relation_mode CHECK (lock_mode IN ('NL','S','U','X','IS','IU','IX','SIU','SIX','UIX','SCH_S','SCH_M','BU','RANGES_S','RANGES_U','RANGEI_N','RANGEI_S','RANGEX_X','OTHER'))
);

CREATE OR REPLACE FUNCTION events.reject_deadlock_mutation() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, events AS $fn$
BEGIN
    IF TG_OP = 'DELETE'
       AND current_setting('sqlobserver.retention_context', true) = 'envelope_cascade'
       AND pg_has_role(session_user, 'sqlobserver_migrator', 'member') THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'deadlock evidence is append-only' USING ERRCODE = '55000';
END;
$fn$;
CREATE TRIGGER deadlock_summary_append_only BEFORE UPDATE OR DELETE ON events.deadlock_summary FOR EACH ROW EXECUTE FUNCTION events.reject_deadlock_mutation();
CREATE TRIGGER deadlock_participant_append_only BEFORE UPDATE OR DELETE ON events.deadlock_participant FOR EACH ROW EXECUTE FUNCTION events.reject_deadlock_mutation();
CREATE TRIGGER deadlock_relation_append_only BEFORE UPDATE OR DELETE ON events.deadlock_relation FOR EACH ROW EXECUTE FUNCTION events.reject_deadlock_mutation();
REVOKE ALL ON FUNCTION events.reject_deadlock_mutation() FROM PUBLIC;
COMMENT ON FUNCTION events.reject_deadlock_mutation() IS 'Append-only guard; only a trusted migrator retention context permits cascaded deletes initiated from the diagnostic_event envelope.';
ALTER TABLE events.deadlock_summary ENABLE ROW LEVEL SECURITY; ALTER TABLE events.deadlock_summary FORCE ROW LEVEL SECURITY;
ALTER TABLE events.deadlock_participant ENABLE ROW LEVEL SECURITY; ALTER TABLE events.deadlock_participant FORCE ROW LEVEL SECURITY;
ALTER TABLE events.deadlock_relation ENABLE ROW LEVEL SECURITY; ALTER TABLE events.deadlock_relation FORCE ROW LEVEL SECURITY;
CREATE POLICY deadlock_summary_migrator ON events.deadlock_summary FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY deadlock_participant_migrator ON events.deadlock_participant FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY deadlock_relation_migrator ON events.deadlock_relation FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);

COMMENT ON TABLE events.deadlock_summary IS 'Typed, deduplicated, privacy-minimized system_health deadlock evidence. Raw XML is intentionally not retained.';

-- M6 parser/source sentinels are a partial, explicitly lossy run. Preserve every
-- M5 outcome tuple while extending the inherited matrix for this one typed loss.
ALTER TABLE telemetry.collection_run_outcome
    DROP CONSTRAINT ck_collection_outcome_reason_matrix;
ALTER TABLE telemetry.collection_run_outcome
    ADD CONSTRAINT ck_collection_outcome_reason_matrix CHECK
    (
        (outcome = 'succeeded' AND reason_code = 'completed' AND loss_kind = 'none')
        OR
        (
            outcome = 'partial'
            AND
            (
                (reason_code = 'source_row_limit' AND loss_kind = 'source_row_limit')
                OR (reason_code = 'response_byte_limit' AND loss_kind = 'response_byte_limit')
                OR (reason_code = 'blocking_graph_limit' AND loss_kind = 'blocking_graph_limit')
                OR (reason_code = 'output_validation_failed' AND loss_kind = 'output_validation_failure')
            )
        )
        OR (outcome = 'timed_out' AND reason_code = 'deadline_exceeded' AND loss_kind = 'none')
        OR (outcome = 'transient_failure' AND reason_code = 'transient_target_failure' AND loss_kind = 'none')
        OR (outcome = 'permanent_failure' AND reason_code IN ('permanent_target_failure', 'target_revision_changed') AND loss_kind = 'none')
        OR (outcome = 'permission_denied' AND reason_code = 'required_permission_missing' AND loss_kind = 'none')
        OR
        (
            outcome = 'unsupported'
            AND reason_code IN
            (
                'target_unsupported', 'capability_profile_missing', 'capability_profile_stale',
                'capability_missing', 'target_version_unsupported', 'target_platform_unsupported',
                'target_edition_unsupported'
            )
            AND loss_kind = 'none'
        )
        OR (outcome = 'output_invalid' AND reason_code = 'output_validation_failed' AND loss_kind = 'output_validation_failure')
        OR (outcome = 'lease_lost' AND reason_code = 'lease_ownership_lost' AND loss_kind = 'none')
        OR (outcome = 'circuit_open' AND reason_code = 'circuit_currently_open' AND loss_kind = 'none')
    );

DROP FUNCTION IF EXISTS control.list_deadlocks(uuid,timestamptz,timestamptz,integer,timestamptz,uuid);
CREATE OR REPLACE FUNCTION control.list_deadlocks(
    p_instance_id uuid, p_from_utc timestamptz, p_to_utc timestamptz, p_limit integer, p_after_utc timestamptz DEFAULT NULL, p_after_event_id uuid DEFAULT NULL, p_snapshot_collected_at timestamptz DEFAULT NULL)
RETURNS TABLE(occurred_at timestamptz, event_id uuid, fingerprint bytea, participant_count integer, relation_count integer, parse_truncated boolean, collected_at timestamptz, snapshot_at timestamptz)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, events AS $fn$
WITH target_lock AS MATERIALIZED
(
    SELECT pg_advisory_xact_lock(hashtextextended(p_instance_id::text, 0)) AS acquired
), snapshot AS
(
    SELECT coalesce(p_snapshot_collected_at, clock_timestamp()) AS at FROM target_lock
), matches AS
(
    SELECT d.occurred_at,d.event_id,d.fingerprint,d.participant_count,d.relation_count,d.parse_truncated,d.collected_at,snapshot.at AS snapshot_at
    FROM events.deadlock_summary d CROSS JOIN snapshot
    WHERE d.instance_id=p_instance_id AND d.occurred_at>=p_from_utc AND d.occurred_at<p_to_utc AND d.collected_at<=snapshot.at
      AND (p_after_utc IS NULL OR (d.occurred_at,d.event_id)<(p_after_utc,p_after_event_id))
    ORDER BY d.occurred_at DESC,d.event_id DESC LIMIT LEAST(GREATEST(COALESCE(p_limit,256),1)+1,257)
)
SELECT * FROM matches
UNION ALL
SELECT NULL::timestamptz,NULL::uuid,NULL::bytea,NULL::integer,NULL::integer,NULL::boolean,NULL::timestamptz,snapshot.at
FROM snapshot WHERE NOT EXISTS (SELECT 1 FROM matches)
ORDER BY occurred_at DESC NULLS LAST, event_id DESC NULLS LAST
$fn$;
REVOKE ALL ON FUNCTION control.list_deadlocks(uuid,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz) FROM PUBLIC;
CREATE OR REPLACE FUNCTION control.get_deadlock(p_instance_id uuid, p_event_id uuid)
RETURNS TABLE(occurred_at timestamptz,event_id uuid,fingerprint bytea,participant_count integer,relation_count integer,parse_truncated boolean,collected_at timestamptz,participants jsonb,relations jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, events AS $fn$
SELECT d.occurred_at,d.event_id,d.fingerprint,d.participant_count,d.relation_count,d.parse_truncated,d.collected_at,
 COALESCE((SELECT jsonb_agg(jsonb_build_object('sessionId',bounded.session_id,'victim',bounded.is_victim) ORDER BY bounded.session_id) FROM (SELECT p.session_id,p.is_victim FROM events.deadlock_participant p WHERE p.occurred_at=d.occurred_at AND p.event_id=d.event_id ORDER BY p.session_id LIMIT 129) AS bounded),'[]'::jsonb),
 COALESCE((SELECT jsonb_agg(jsonb_build_object('blockerSessionId',bounded.blocker_session_id,'waiterSessionId',bounded.waiter_session_id,'resourceCategory',bounded.resource_category,'lockMode',bounded.lock_mode) ORDER BY bounded.blocker_session_id,bounded.waiter_session_id,bounded.resource_category,bounded.lock_mode) FROM (SELECT r.blocker_session_id,r.waiter_session_id,r.resource_category,r.lock_mode FROM events.deadlock_relation r WHERE r.occurred_at=d.occurred_at AND r.event_id=d.event_id ORDER BY r.blocker_session_id,r.waiter_session_id,r.resource_category,r.lock_mode LIMIT 257) AS bounded),'[]'::jsonb)
FROM events.deadlock_summary d WHERE d.instance_id=p_instance_id AND d.event_id=p_event_id AND d.event_id=encode(substring(sha256(uuid_send(d.instance_id) || d.fingerprint) FROM 1 FOR 16),'hex')::uuid
$fn$;
REVOKE ALL ON FUNCTION control.get_deadlock(uuid,uuid) FROM PUBLIC;
REVOKE ALL ON TABLE events.deadlock_summary, events.deadlock_participant, events.deadlock_relation FROM PUBLIC, sqlobserver_server, sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.list_deadlocks(uuid,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz), control.get_deadlock(uuid,uuid) TO sqlobserver_server;

GRANT EXECUTE ON FUNCTION control.ensure_monthly_event_partition(date) TO sqlobserver_collector;

-- The Collector reaches deadlock tables only through this fenced, replay-bound entry point.
CREATE OR REPLACE FUNCTION control.commit_deadlock_collection_run
(
    p_run_id uuid, p_instance_id uuid, p_target_revision bigint, p_collector_id text,
    p_collector_version integer, p_output_schema_version integer, p_schedule_revision bigint,
    p_scheduled_at timestamptz, p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint,
    p_request_digest bytea, p_outcome text, p_reason_code text, p_duration_ms bigint,
    p_attempt_count integer, p_source_row_count integer, p_output_item_count integer,
    p_response_bytes bigint, p_output_bytes bigint, p_loss_kind text, p_minimum_lost_items integer,
    p_loss_count_is_exact boolean, p_minimum_lost_bytes integer, p_next_circuit_state text,
    p_next_consecutive_failures integer, p_occurred_ats timestamptz[], p_event_ids uuid[],
    p_fingerprints bytea[], p_participant_counts integer[], p_relation_counts integer[],
    p_parse_truncated boolean[], p_participant_json jsonb[], p_relation_json jsonb[], p_sizes integer[]
)
RETURNS TABLE(result_status text, inserted_count integer, duplicate_count integer, rejected_count integer, persisted_bytes integer, committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_contract control.collector_contract%ROWTYPE;
    selected_schedule control.collector_schedule%ROWTYPE;
    existing_outcome telemetry.collection_run_outcome%ROWTYPE;
    item_count integer := coalesce(cardinality(p_event_ids), -1);
    inserted_count integer := 0;
    duplicate_count integer := 0;
    rejected_count integer := 0;
    persisted_bytes bigint := 0;
    captured_repository_time timestamptz;
    completion_digest bytea;
    aggregate_json_bytes bigint;
    aggregate_size_bytes bigint;
    begin_status text;
    begin_started_at timestamptz;
    event_id uuid;
    event_occurred timestamptz;
    item_index integer;
    occurrence_month date;
    next_open_until timestamptz;
BEGIN
    IF p_collector_id <> 'deadlocks.system-health'
       OR item_count < 0 OR item_count > 256
       OR cardinality(p_occurred_ats) IS DISTINCT FROM item_count
       OR cardinality(p_fingerprints) IS DISTINCT FROM item_count
       OR cardinality(p_participant_counts) IS DISTINCT FROM item_count
       OR cardinality(p_relation_counts) IS DISTINCT FROM item_count
       OR cardinality(p_parse_truncated) IS DISTINCT FROM item_count
       OR cardinality(p_participant_json) IS DISTINCT FROM item_count
       OR cardinality(p_relation_json) IS DISTINCT FROM item_count
       OR cardinality(p_sizes) IS DISTINCT FROM item_count
       OR (p_outcome <> 'output_invalid' AND p_output_item_count <> item_count)
       OR (p_outcome = 'output_invalid' AND item_count <> 0)
       OR p_output_item_count NOT BETWEEN 0 AND 256
       OR (p_outcome NOT IN ('succeeded','partial','output_invalid') AND (item_count <> 0 OR p_output_item_count <> 0))
       OR p_source_row_count NOT BETWEEN 0 AND 100000
       OR p_response_bytes NOT BETWEEN 0 AND 33554432
       OR p_output_bytes NOT BETWEEN 0 AND 33554432
       OR p_response_bytes < p_output_bytes
       OR p_attempt_count NOT BETWEEN 0 AND 2
       OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit','output_validation_failure')
       OR p_minimum_lost_items < 0 OR p_minimum_lost_bytes < 0 THEN
        RAISE EXCEPTION 'deadlock collector payload failed bounded array preflight' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(p_fingerprints,p_participant_counts,p_relation_counts,p_sizes) AS x(fingerprint,participants,relations,size)
               WHERE octet_length(x.fingerprint) <> 32 OR x.participants NOT BETWEEN 0 AND 128 OR x.relations NOT BETWEEN 0 AND 256 OR x.size < 192 OR x.size > 1048576)
       OR EXISTS (SELECT 1 FROM unnest(p_occurred_ats) AS x(value) WHERE x.value IS NULL OR NOT isfinite(x.value)) THEN
        RAISE EXCEPTION 'deadlock collector payload contains an invalid bounded value' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(p_event_ids,p_fingerprints) AS x(event_id,fingerprint)
               WHERE x.event_id <> encode(substring(sha256(uuid_send(p_instance_id) || x.fingerprint) FROM 1 FOR 16),'hex')::uuid) THEN
        RAISE EXCEPTION 'deadlock event identity is not target scoped' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM generate_subscripts(p_event_ids,1) AS s(i)
               WHERE p_participant_json[s.i] IS NULL
                  OR p_relation_json[s.i] IS NULL
                  OR jsonb_typeof(p_participant_json[s.i]) <> 'array'
                  OR jsonb_typeof(p_relation_json[s.i]) <> 'array'
                  OR jsonb_array_length(p_participant_json[s.i]) <> p_participant_counts[s.i]
                  OR jsonb_array_length(p_relation_json[s.i]) <> p_relation_counts[s.i]
                  OR octet_length(p_participant_json[s.i]::text) > 262144
                  OR octet_length(p_relation_json[s.i]::text) > 524288
                  OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_participant_json[s.i]) AS item
                             WHERE jsonb_typeof(item) <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(item)) <> 2
                                OR NOT item ? 'sessionId' OR NOT item ? 'victim'
                                OR jsonb_typeof(item->'sessionId') <> 'number' OR jsonb_typeof(item->'victim') <> 'boolean'
                                OR CASE WHEN item->>'sessionId' ~ '^[0-9]+$' AND length(item->>'sessionId') <= 5 THEN (item->>'sessionId')::integer NOT BETWEEN 1 AND 32767 ELSE true END)
                  OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_relation_json[s.i]) AS item
                             WHERE jsonb_typeof(item) <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(item)) <> 4
                                OR NOT item ? 'blockerSessionId' OR NOT item ? 'waiterSessionId' OR NOT item ? 'resourceCategory' OR NOT item ? 'lockMode'
                                OR jsonb_typeof(item->'blockerSessionId') <> 'number' OR jsonb_typeof(item->'waiterSessionId') <> 'number'
                                OR jsonb_typeof(item->'resourceCategory') <> 'string' OR jsonb_typeof(item->'lockMode') <> 'string'
                                OR CASE WHEN item->>'blockerSessionId' ~ '^[0-9]+$' AND length(item->>'blockerSessionId') <= 5 THEN (item->>'blockerSessionId')::integer NOT BETWEEN 1 AND 32767 ELSE true END
                                OR CASE WHEN item->>'waiterSessionId' ~ '^[0-9]+$' AND length(item->>'waiterSessionId') <= 5 THEN (item->>'waiterSessionId')::integer NOT BETWEEN 1 AND 32767 ELSE true END
                                OR item->>'resourceCategory' NOT IN ('key','page','object_lock','metadata','exchange','other')
                                OR item->>'lockMode' NOT IN ('NL','S','U','X','IS','IU','IX','SIU','SIX','UIX','SCH_S','SCH_M','BU','RANGES_S','RANGES_U','RANGEI_N','RANGEI_S','RANGEX_X','OTHER'))) THEN
        RAISE EXCEPTION 'deadlock collector payload contains malformed bounded JSON evidence' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1
               FROM generate_subscripts(p_event_ids,1) AS s(i)
               CROSS JOIN LATERAL
               (
                   SELECT COALESCE('[' || (SELECT string_agg(
                       '{"sessionId":' || (item.value->>'sessionId') ||
                       ',"victim":' || lower(item.value->>'victim') || '}', ',' ORDER BY (item.value->>'sessionId')::integer)
                       FROM jsonb_array_elements(p_participant_json[s.i]) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS participant_json,
                          COALESCE('[' || (SELECT string_agg(
                       '{"blockerSessionId":' || (item.value->>'blockerSessionId') ||
                       ',"waiterSessionId":' || (item.value->>'waiterSessionId') ||
                       ',"resourceCategory":"' || (item.value->>'resourceCategory') ||
                       '","lockMode":"' || (item.value->>'lockMode') || '"}', ',' ORDER BY (item.value->>'blockerSessionId')::integer, (item.value->>'waiterSessionId')::integer, CASE item.value->>'resourceCategory' WHEN 'key' THEN 1 WHEN 'page' THEN 2 WHEN 'object_lock' THEN 3 WHEN 'metadata' THEN 4 WHEN 'exchange' THEN 5 ELSE 6 END, convert_to(item.value->>'lockMode','UTF8'))
                       FROM jsonb_array_elements(p_relation_json[s.i]) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS relation_json
               ) AS canonical
               WHERE p_sizes[s.i] <> 192
                   + octet_length(convert_to(canonical.participant_json, 'UTF8'))
                   + octet_length(convert_to(canonical.relation_json, 'UTF8'))) THEN
        RAISE EXCEPTION 'deadlock collector payload contains forged per-item byte accounting' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (SELECT 1 FROM generate_subscripts(p_event_ids,1) AS s(i)
               WHERE EXISTS (SELECT 1 FROM (SELECT item->>'sessionId' AS session_id, count(*) AS c FROM jsonb_array_elements(p_participant_json[s.i]) AS item GROUP BY item->>'sessionId' HAVING count(*) > 1) AS duplicate_participants)
                  OR EXISTS (SELECT 1 FROM (SELECT item->>'blockerSessionId' AS blocker_id,item->>'waiterSessionId' AS waiter_id,item->>'resourceCategory' AS category,item->>'lockMode' AS mode,count(*) AS c FROM jsonb_array_elements(p_relation_json[s.i]) AS item GROUP BY item->>'blockerSessionId',item->>'waiterSessionId',item->>'resourceCategory',item->>'lockMode' HAVING count(*) > 1) AS duplicate_relations)) THEN
        RAISE EXCEPTION 'deadlock collector payload contains duplicate normalized evidence' USING ERRCODE = '22023';
    END IF;
    SELECT coalesce(sum(octet_length(convert_to(canonical.participant_json, 'UTF8')) + octet_length(convert_to(canonical.relation_json, 'UTF8'))), 0),
           coalesce(sum(size), 0)
    INTO aggregate_json_bytes, aggregate_size_bytes
    FROM unnest(p_participant_json, p_relation_json, p_sizes) AS bounded(participant_json, relation_json, size)
    CROSS JOIN LATERAL
    (
        SELECT COALESCE('[' || (SELECT string_agg(
                   '{"sessionId":' || (item.value->>'sessionId') ||
                   ',"victim":' || lower(item.value->>'victim') || '}', ',' ORDER BY (item.value->>'sessionId')::integer)
                   FROM jsonb_array_elements(bounded.participant_json) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS participant_json,
               COALESCE('[' || (SELECT string_agg(
                   '{"blockerSessionId":' || (item.value->>'blockerSessionId') ||
                   ',"waiterSessionId":' || (item.value->>'waiterSessionId') ||
                   ',"resourceCategory":"' || (item.value->>'resourceCategory') ||
                   '","lockMode":"' || (item.value->>'lockMode') || '"}', ',' ORDER BY (item.value->>'blockerSessionId')::integer, (item.value->>'waiterSessionId')::integer, CASE item.value->>'resourceCategory' WHEN 'key' THEN 1 WHEN 'page' THEN 2 WHEN 'object_lock' THEN 3 WHEN 'metadata' THEN 4 WHEN 'exchange' THEN 5 ELSE 6 END, convert_to(item.value->>'lockMode','UTF8'))
                   FROM jsonb_array_elements(bounded.relation_json) WITH ORDINALITY AS item(value,ordinality)) || ']', '[]') AS relation_json
    ) AS canonical;
    IF aggregate_json_bytes > 1048576
       OR (p_outcome <> 'output_invalid' AND aggregate_size_bytes <> p_output_bytes)
       OR (p_outcome = 'output_invalid' AND (aggregate_size_bytes <> 0 OR aggregate_json_bytes <> 0))
       OR aggregate_size_bytes > p_response_bytes
       OR aggregate_json_bytes > p_output_bytes THEN
        RAISE EXCEPTION 'deadlock collector payload exceeds its bounded aggregate byte accounting' USING ERRCODE = '22023';
    END IF;
    SELECT contract.* INTO selected_contract FROM control.collector_contract AS contract
    WHERE contract.collector_id = p_collector_id AND contract.collector_version = p_collector_version;
    IF NOT FOUND OR selected_contract.output_schema_version <> p_output_schema_version
       OR p_source_row_count > selected_contract.maximum_rows OR p_response_bytes > selected_contract.maximum_response_bytes
       OR p_output_bytes > selected_contract.maximum_response_bytes OR p_attempt_count > selected_contract.maximum_attempts THEN
        RAISE EXCEPTION 'deadlock collector payload does not match immutable contract' USING ERRCODE = '22023';
    END IF;
    IF p_outcome = 'succeeded' AND (p_loss_kind <> 'none' OR p_reason_code <> 'completed') THEN
        RAISE EXCEPTION 'deadlock success must be complete and loss-free' USING ERRCODE = '22023';
    END IF;
    IF p_outcome = 'partial' AND p_loss_kind = 'none' THEN
        RAISE EXCEPTION 'deadlock partial outcome requires explicit loss' USING ERRCODE = '22023';
    END IF;
    completion_digest := sha256(convert_to(jsonb_build_object(
        'contract','sqlobserver.deadlock-completion.v1','run',p_run_id,'target',p_instance_id,
        'revision',p_target_revision,'collector',p_collector_id,'collectorVersion',p_collector_version,
        'outputVersion',p_output_schema_version,'scheduleRevision',p_schedule_revision,
        'scheduledAt',p_scheduled_at,'requestDigest',encode(p_request_digest,'hex'),
        'outcome',p_outcome,'reason',p_reason_code,'sourceRows',p_source_row_count,
        'outputItems',p_output_item_count,'responseBytes',p_response_bytes,'outputBytes',p_output_bytes,
        'durationMs',p_duration_ms,'attempts',p_attempt_count,'lossExact',p_loss_count_is_exact,
        'nextCircuit',p_next_circuit_state,'nextFailures',p_next_consecutive_failures,
        'lossKind',p_loss_kind,'lostItems',p_minimum_lost_items,'lostBytes',p_minimum_lost_bytes,
        'fingerprints',p_fingerprints,'occurredAt',p_occurred_ats,'eventIds',p_event_ids,
        'participants',p_participant_counts,'relations',p_relation_counts,'parseTruncated',p_parse_truncated,
        'participantJson',p_participant_json,'relationJson',p_relation_json,'sizes',p_sizes)::text,'UTF8'));
    -- Serialize target reads with the atomic commit so a collected_at watermark
    -- cannot precede visibility of an in-flight transaction.
    PERFORM pg_advisory_xact_lock(hashtextextended(p_instance_id::text, 0));
    SELECT result.result_status,result.started_at,result.repository_time INTO begin_status,begin_started_at,captured_repository_time
    FROM control.begin_collection_run(p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,p_output_schema_version,p_schedule_revision,p_scheduled_at,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest) AS result;
    IF begin_status = 'replayed' THEN
        SELECT outcome.* INTO existing_outcome FROM telemetry.collection_run_outcome AS outcome WHERE outcome.run_id = p_run_id;
        IF existing_outcome.completion_digest <> completion_digest THEN RAISE EXCEPTION 'deadlock collector replay differs from committed payload' USING ERRCODE = '22023'; END IF;
        RETURN QUERY SELECT 'replayed',existing_outcome.inserted_item_count,existing_outcome.duplicate_item_count,existing_outcome.rejected_item_count,existing_outcome.persisted_bytes::integer,existing_outcome.completed_at; RETURN;
    ELSIF begin_status NOT IN ('started','running_replay') THEN
        RETURN QUERY SELECT begin_status,0,0,0,0,NULL::timestamptz; RETURN;
    END IF;
    captured_repository_time := clock_timestamp();
    IF EXISTS (SELECT 1 FROM unnest(p_occurred_ats) AS x(value) WHERE x.value < captured_repository_time - interval '33 days' OR x.value > captured_repository_time + interval '1 day') THEN
        RAISE EXCEPTION 'deadlock occurrence is outside the bounded repository window' USING ERRCODE = '22023';
    END IF;
    FOR occurrence_month IN SELECT DISTINCT date_trunc('month', value)::date FROM unnest(p_occurred_ats) AS x(value) LOOP
        PERFORM control.ensure_monthly_event_partition(occurrence_month);
    END LOOP;
    FOR event_occurred,event_id IN SELECT value.event_occurred,value.event_id FROM unnest(p_occurred_ats,p_event_ids) AS value(event_occurred,event_id) LOOP
        item_index := array_position(p_event_ids,event_id);
        IF NOT EXISTS (SELECT 1 FROM events.deadlock_summary AS prior WHERE prior.instance_id=p_instance_id AND prior.fingerprint=p_fingerprints[item_index]) THEN
            INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at)
            VALUES(event_occurred,event_id,p_instance_id,'deadlock.captured',0,jsonb_build_object('participantCount',p_participant_counts[item_index],'relationCount',p_relation_counts[item_index],'parseTruncated',p_parse_truncated[item_index]),captured_repository_time)
            ON CONFLICT (occurred_at,event_id) DO NOTHING;
            INSERT INTO events.deadlock_summary(occurred_at,event_id,collection_run_id,instance_id,fingerprint,participant_count,relation_count,parse_truncated,collected_at)
            VALUES(event_occurred,event_id,p_run_id,p_instance_id,p_fingerprints[item_index],p_participant_counts[item_index],p_relation_counts[item_index],p_parse_truncated[item_index],captured_repository_time);
            inserted_count := inserted_count + 1;
            persisted_bytes := persisted_bytes + p_sizes[item_index];
            INSERT INTO events.deadlock_participant(occurred_at,event_id,session_id,is_victim)
            SELECT event_occurred,event_id,(x->>'sessionId')::integer,(x->>'victim')::boolean FROM jsonb_array_elements(p_participant_json[item_index]) AS x;
            INSERT INTO events.deadlock_relation(occurred_at,event_id,blocker_session_id,waiter_session_id,resource_category,lock_mode)
            SELECT event_occurred,event_id,(x->>'blockerSessionId')::integer,(x->>'waiterSessionId')::integer,x->>'resourceCategory',x->>'lockMode' FROM jsonb_array_elements(p_relation_json[item_index]) AS x;
        ELSE duplicate_count := duplicate_count + 1; END IF;
    END LOOP;
    rejected_count := greatest(p_output_item_count - inserted_count - duplicate_count,0);
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    SELECT schedule.* INTO selected_schedule FROM control.collector_schedule AS schedule WHERE schedule.instance_id=p_instance_id AND schedule.collector_id=p_collector_id FOR UPDATE;
    IF selected_schedule.active_run_id <> p_run_id OR selected_schedule.schedule_revision <> p_schedule_revision THEN RAISE EXCEPTION 'deadlock collector schedule changed before commit' USING ERRCODE = '55000'; END IF;
    IF
    (
        p_outcome IN ('succeeded', 'partial')
        AND (p_next_circuit_state <> 'closed' OR p_next_consecutive_failures <> 0)
    )
    OR
    (
        p_outcome IN ('transient_failure', 'timed_out')
        AND
        (
            p_next_consecutive_failures <> selected_schedule.consecutive_failure_count + 1
            OR p_next_circuit_state <> CASE
                WHEN selected_schedule.circuit_state = 'half_open'
                     OR selected_schedule.consecutive_failure_count + 1 >= selected_contract.circuit_failure_threshold
                    THEN 'open'
                ELSE 'closed'
            END
        )
    )
    OR
    (
        p_outcome NOT IN ('succeeded', 'partial', 'transient_failure', 'timed_out')
        AND
        (
            p_next_circuit_state <> 'closed'
            OR p_next_consecutive_failures <> 0
        )
    ) THEN
        RAISE EXCEPTION 'deadlock collector circuit transition differs from the authoritative schedule state' USING ERRCODE = '22023';
    END IF;
    next_open_until := CASE
        WHEN p_next_circuit_state = 'open'
            THEN captured_repository_time + selected_contract.circuit_open_interval
        ELSE NULL
    END;
    INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
    VALUES(p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),p_duration_ms,p_source_row_count,p_output_item_count,inserted_count,duplicate_count,rejected_count,p_response_bytes,p_output_bytes,persisted_bytes,p_loss_kind IN ('source_row_limit','response_byte_limit'),p_loss_kind <> 'none',p_loss_kind,p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,completion_digest,captured_repository_time);
    IF p_outcome <> 'succeeded' OR p_loss_kind <> 'none' THEN
        INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at)
        VALUES(gen_random_uuid(),p_run_id,p_instance_id,p_collector_id,p_reason_code,p_scheduled_at,captured_repository_time,greatest(p_minimum_lost_items,1),p_minimum_lost_bytes,CASE WHEN p_loss_kind <> 'none' THEN p_loss_count_is_exact ELSE false END,captured_repository_time);
    END IF;
    UPDATE control.collector_schedule SET active_run_id=NULL,next_due_at=CASE WHEN p_next_circuit_state='open' THEN next_open_until ELSE captured_repository_time+collection_interval END,circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,circuit_open_until=next_open_until,last_completed_at=captured_repository_time,last_succeeded_at=CASE WHEN p_outcome IN ('succeeded','partial') THEN captured_repository_time ELSE last_succeeded_at END,last_outcome=p_outcome,updated_at=captured_repository_time WHERE instance_id=p_instance_id AND collector_id=p_collector_id;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    RETURN QUERY SELECT 'committed',inserted_count,duplicate_count,rejected_count,persisted_bytes::integer,captured_repository_time;
END
$sqlobserver$;
REVOKE ALL ON FUNCTION control.commit_deadlock_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.commit_deadlock_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[]) TO sqlobserver_collector;

CREATE OR REPLACE FUNCTION control.reconcile_collector_catalog_m6
(
    p_collector_ids text[], p_collector_versions integer[], p_manifest_sha256 bytea[], p_asset_bundle_sha256 bytea[],
    p_execution_orders integer[], p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint
)
RETURNS TABLE(inserted_count integer, updated_count integer, unchanged_count integer, repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE captured timestamptz;
BEGIN
    IF p_work_key <> 'collector/catalog/reconcile'
       OR p_collector_ids IS DISTINCT FROM ARRAY['engine.core','database.inventory','database.files','activity.sessions','activity.requests','waits.server','blocking.current','deadlocks.system-health']::text[]
       OR p_collector_versions IS DISTINCT FROM ARRAY[1,1,1,1,1,1,1,1]::integer[]
       OR p_execution_orders IS DISTINCT FROM ARRAY[1,2,3,4,5,6,7,8]::integer[]
       OR cardinality(p_manifest_sha256) <> 8 OR cardinality(p_asset_bundle_sha256) <> 8 THEN
        RAISE EXCEPTION 'collector M6 catalog must contain the exact ordered reviewed contract set' USING ERRCODE = '22023';
    END IF;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    IF EXISTS (SELECT 1 FROM unnest(p_collector_ids,p_collector_versions,p_manifest_sha256,p_asset_bundle_sha256,p_execution_orders) AS x(id,version,manifest,bundle,execution_order)
               LEFT JOIN control.collector_contract c ON c.collector_id=x.id AND c.collector_version=x.version AND c.manifest_sha256=x.manifest AND c.asset_bundle_sha256=x.bundle AND c.execution_order=x.execution_order WHERE c.collector_id IS NULL) THEN
        RAISE EXCEPTION 'collector M6 catalog differs from immutable repository contracts' USING ERRCODE = '55000';
    END IF;
    captured := clock_timestamp();
    INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
    SELECT target.instance_id,contract.collector_id,contract.collector_version,target.revision,1,true,contract.default_interval,captured,'closed',0,captured,captured
    FROM control.observation_target target CROSS JOIN control.collector_contract contract
    WHERE target.host_name IS NOT NULL AND target.lifecycle_state='active' AND contract.collector_id=ANY(p_collector_ids)
    ON CONFLICT (instance_id,collector_id) DO NOTHING;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    RETURN QUERY SELECT 0,0,cardinality(p_collector_ids),captured;
END
$sqlobserver$;
REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_m6(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.reconcile_collector_catalog_m6(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint) TO sqlobserver_collector;
