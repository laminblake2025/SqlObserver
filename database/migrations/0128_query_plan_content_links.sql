-- Bind protected plan XML to the exact plan, query, run, and target. Separate
-- links allow identical XML to be deduplicated across two plan identities.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.query_performance_plan_content_link (
    collection_run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    database_id integer NOT NULL CHECK (database_id BETWEEN 1 AND 32767),
    query_fingerprint bytea NOT NULL CHECK (octet_length(query_fingerprint) = 32),
    plan_fingerprint bytea NOT NULL CHECK (octet_length(plan_fingerprint) = 32),
    content_reference uuid NOT NULL,
    content_available boolean NOT NULL DEFAULT true,
    PRIMARY KEY (collection_run_id, database_id, query_fingerprint,
                 plan_fingerprint, content_reference),
    FOREIGN KEY (collection_run_id, database_id, query_fingerprint,
                 plan_fingerprint)
        REFERENCES events.query_performance_plan
            (collection_run_id, database_id, query_fingerprint,
             plan_fingerprint) ON DELETE CASCADE,
    FOREIGN KEY (collection_run_id, database_id, query_fingerprint, instance_id)
        REFERENCES events.query_performance_query
            (collection_run_id, database_id, query_fingerprint, instance_id),
    FOREIGN KEY (content_reference, instance_id)
        REFERENCES security.protected_diagnostic_payload (payload_id, instance_id)
);
REVOKE ALL ON events.query_performance_plan_content_link FROM PUBLIC;
ALTER TABLE events.query_performance_plan_content_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.query_performance_plan_content_link FORCE ROW LEVEL SECURITY;
CREATE POLICY query_plan_content_migrator
    ON events.query_performance_plan_content_link FOR ALL TO sqlobserver_migrator
    USING (control.query_performance_target_claim(instance_id))
    WITH CHECK (control.query_performance_target_claim(instance_id));
CREATE TRIGGER query_plan_content_append_only
    BEFORE UPDATE OR DELETE ON events.query_performance_plan_content_link
    FOR EACH ROW EXECUTE FUNCTION events.reject_query_performance_mutation();

CREATE FUNCTION control.commit_query_plan_links(
    p_run_id uuid, p_instance_id uuid, p_fence_token bigint, p_links jsonb)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path = pg_catalog, control, events, security, telemetry
AS $fn$
DECLARE
    v_work_key text;
    v_owner uuid;
    v_valid integer;
    v_persisted integer;
BEGIN
    IF p_run_id IS NULL OR p_instance_id IS NULL OR p_fence_token IS NULL
       OR p_fence_token <= 0 OR p_links IS NULL
       OR jsonb_typeof(p_links) <> 'array'
       OR jsonb_array_length(p_links) > 20000
       OR octet_length(p_links::text) > 8388608
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query plan link preflight failed' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        WHERE jsonb_typeof(item.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(item.value)) <> 5
           OR coalesce(item.value->>'database_id', '') !~ '^[0-9]{1,5}$'
           OR coalesce(item.value->>'query_fingerprint', '') !~ '^[0-9a-f]{64}$'
           OR coalesce(item.value->>'plan_fingerprint', '') !~ '^[0-9a-f]{64}$'
           OR coalesce(item.value->>'payload_id', '') !~*
              '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'
           OR coalesce(item.value->>'fingerprint', '') !~ '^[0-9a-f]{64}$'
    ) THEN
        RAISE EXCEPTION 'query plan link shape is invalid' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        WHERE (item.value->>'database_id')::integer NOT BETWEEN 1 AND 32767
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_links) AS item(value)
        GROUP BY item.value->>'database_id', item.value->>'query_fingerprint',
                 item.value->>'plan_fingerprint'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query plan links have an invalid or duplicate identity'
            USING ERRCODE = '22023';
    END IF;

    SELECT work_key, owner_execution_id INTO v_work_key, v_owner
    FROM telemetry.collection_run
    WHERE run_id = p_run_id AND instance_id = p_instance_id
      AND collector_id = 'queries.performance'
    FOR UPDATE;
    IF v_work_key IS NULL OR v_owner IS NULL THEN
        RAISE EXCEPTION 'query plan link run is missing' USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);

    WITH supplied AS (
        SELECT * FROM jsonb_to_recordset(p_links) AS item(
            database_id integer, query_fingerprint text, plan_fingerprint text,
            payload_id uuid, fingerprint text)
    )
    SELECT count(*) INTO v_valid
    FROM supplied AS item
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = p_run_id
     AND query_row.instance_id = p_instance_id
     AND query_row.database_id = item.database_id
     AND query_row.query_fingerprint = decode(item.query_fingerprint, 'hex')
    JOIN events.query_performance_plan AS plan_row
      ON plan_row.collection_run_id = query_row.collection_run_id
     AND plan_row.database_id = query_row.database_id
     AND plan_row.query_fingerprint = query_row.query_fingerprint
     AND plan_row.plan_fingerprint = decode(item.plan_fingerprint, 'hex')
    JOIN security.protected_diagnostic_payload AS payload
      ON payload.payload_id = item.payload_id
     AND payload.instance_id = p_instance_id
     AND payload.payload_kind = 'execution_plan'
     AND payload.fingerprint = decode(item.fingerprint, 'hex');
    IF v_valid <> jsonb_array_length(p_links) THEN
        RAISE EXCEPTION 'query plan link target, plan, or payload mismatch'
            USING ERRCODE = '23503';
    END IF;

    INSERT INTO events.query_performance_plan_content_link
        (collection_run_id, instance_id, database_id, query_fingerprint,
         plan_fingerprint, content_reference, content_available)
    SELECT p_run_id, p_instance_id, item.database_id,
           decode(item.query_fingerprint, 'hex'),
           decode(item.plan_fingerprint, 'hex'), item.payload_id, true
    FROM jsonb_to_recordset(p_links) AS item(
        database_id integer, query_fingerprint text, plan_fingerprint text,
        payload_id uuid, fingerprint text)
    ON CONFLICT DO NOTHING;

    SELECT count(*) INTO v_persisted
    FROM jsonb_to_recordset(p_links) AS item(
        database_id integer, query_fingerprint text, plan_fingerprint text,
        payload_id uuid, fingerprint text)
    JOIN events.query_performance_plan_content_link AS link
      ON link.collection_run_id = p_run_id
     AND link.instance_id = p_instance_id
     AND link.database_id = item.database_id
     AND link.query_fingerprint = decode(item.query_fingerprint, 'hex')
     AND link.plan_fingerprint = decode(item.plan_fingerprint, 'hex')
     AND link.content_reference = item.payload_id
     AND link.content_available;
    IF v_persisted <> v_valid THEN
        RAISE EXCEPTION 'query plan link did not persist as available'
            USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);
    RETURN v_persisted;
END;
$fn$;
REVOKE ALL ON FUNCTION control.commit_query_plan_links(uuid,uuid,bigint,jsonb)
    FROM PUBLIC, sqlobserver_collector, sqlobserver_server;

-- The existing text wrapper commits the run and text links. This outer
-- wrapper adds plan links before its transaction can be committed.
CREATE FUNCTION control.commit_query_performance_with_content(
    p_run_id uuid, p_instance_id uuid, p_target_revision bigint,
    p_collector_version integer, p_output_schema_version integer,
    p_schedule_revision bigint, p_scheduled_at timestamptz, p_work_key text,
    p_owner_execution_id uuid, p_fencing_token bigint,
    p_request_digest bytea, p_outcome text, p_reason_code text,
    p_duration_ms bigint, p_attempt_count integer, p_source_row_count integer,
    p_output_item_count integer, p_response_bytes bigint, p_output_bytes bigint,
    p_loss_kind text, p_minimum_lost_items integer, p_loss_count_is_exact boolean,
    p_minimum_lost_bytes integer, p_next_circuit_state text,
    p_next_consecutive_failures integer, p_completion_digest bytea,
    p_window_start timestamptz, p_window_end timestamptz,
    p_source events.query_performance_source, p_source_state text,
    p_coverage text, p_freshness boolean, p_truncated boolean,
    p_payload jsonb, p_text_links jsonb, p_plan_links jsonb)
RETURNS TABLE(
    result_status text, inserted_count integer, duplicate_count integer,
    rejected_count integer, persisted_bytes integer, committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path = pg_catalog, control, events, security, telemetry
AS $fn$
DECLARE v_result record;
BEGIN
    IF p_plan_links IS NULL OR jsonb_typeof(p_plan_links) <> 'array'
       OR jsonb_array_length(p_plan_links) > 20000
       OR octet_length(p_plan_links::text) > 8388608 THEN
        RAISE EXCEPTION 'query plan link envelope invalid' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO v_result
    FROM control.commit_query_performance_with_text(
        p_run_id, p_instance_id, p_target_revision, p_collector_version,
        p_output_schema_version, p_schedule_revision, p_scheduled_at,
        p_work_key, p_owner_execution_id, p_fencing_token, p_request_digest,
        p_outcome, p_reason_code, p_duration_ms, p_attempt_count,
        p_source_row_count, p_output_item_count, p_response_bytes,
        p_output_bytes, p_loss_kind, p_minimum_lost_items,
        p_loss_count_is_exact, p_minimum_lost_bytes, p_next_circuit_state,
        p_next_consecutive_failures, p_completion_digest, p_window_start,
        p_window_end, p_source, p_source_state, p_coverage, p_freshness,
        p_truncated, p_payload, p_text_links);
    IF NOT FOUND THEN
        RAISE EXCEPTION 'query performance commit returned no result'
            USING ERRCODE = '55000';
    END IF;
    IF v_result.result_status = 'committed'
       AND jsonb_array_length(p_plan_links) > 0 THEN
        PERFORM control.commit_query_plan_links(
            p_run_id, p_instance_id, p_fencing_token, p_plan_links);
    END IF;
    RETURN QUERY SELECT v_result.result_status, v_result.inserted_count,
        v_result.duplicate_count, v_result.rejected_count,
        v_result.persisted_bytes, v_result.committed_at;
END;
$fn$;
REVOKE ALL ON FUNCTION control.commit_query_performance_with_content(
    uuid,uuid,bigint,integer,integer,bigint,timestamptz,text,uuid,bigint,
    bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,
    integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,
    events.query_performance_source,text,text,boolean,boolean,jsonb,jsonb,jsonb)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.commit_query_performance_with_content(
    uuid,uuid,bigint,integer,integer,bigint,timestamptz,text,uuid,bigint,
    bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,
    integer,boolean,integer,text,integer,bytea,timestamptz,timestamptz,
    events.query_performance_source,text,text,boolean,boolean,jsonb,jsonb,jsonb)
    TO sqlobserver_collector;
