-- Query Store wait categories are bounded plan/run snapshots, not additive
-- deltas. A separate relation avoids multiplying runtime metrics by category.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.query_performance_wait_snapshot (
    collection_run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    database_id integer NOT NULL CHECK (database_id BETWEEN 1 AND 32767),
    query_fingerprint bytea NOT NULL CHECK (octet_length(query_fingerprint) = 32),
    plan_fingerprint bytea NOT NULL CHECK (octet_length(plan_fingerprint) = 32),
    categories jsonb NOT NULL CHECK (
        jsonb_typeof(categories) = 'array'
        AND jsonb_array_length(categories) <= 32
        AND octet_length(categories::text) <= 4096),
    captured_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (collection_run_id, database_id, query_fingerprint, plan_fingerprint),
    FOREIGN KEY (collection_run_id, database_id, query_fingerprint, plan_fingerprint)
        REFERENCES events.query_performance_plan
            (collection_run_id, database_id, query_fingerprint, plan_fingerprint)
        ON DELETE CASCADE,
    FOREIGN KEY (collection_run_id, database_id, query_fingerprint, instance_id)
        REFERENCES events.query_performance_query
            (collection_run_id, database_id, query_fingerprint, instance_id)
);
REVOKE ALL ON events.query_performance_wait_snapshot FROM PUBLIC;
ALTER TABLE events.query_performance_wait_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.query_performance_wait_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY query_wait_snapshot_migrator
    ON events.query_performance_wait_snapshot FOR ALL TO sqlobserver_migrator
    USING (control.query_performance_target_claim(instance_id))
    WITH CHECK (control.query_performance_target_claim(instance_id));
CREATE TRIGGER query_wait_snapshot_append_only
    BEFORE UPDATE OR DELETE ON events.query_performance_wait_snapshot
    FOR EACH ROW EXECUTE FUNCTION events.reject_query_performance_mutation();

CREATE FUNCTION control.commit_query_plan_wait_snapshots(
    p_run_id uuid, p_instance_id uuid, p_fence_token bigint, p_snapshots jsonb)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path = pg_catalog, control, events, telemetry
AS $fn$
DECLARE
    v_work_key text;
    v_owner uuid;
    v_valid integer;
    v_inserted integer;
BEGIN
    IF p_run_id IS NULL OR p_instance_id IS NULL OR p_fence_token IS NULL
       OR p_fence_token <= 0 OR p_snapshots IS NULL
       OR jsonb_typeof(p_snapshots) <> 'array'
       OR jsonb_array_length(p_snapshots) > 8
       OR octet_length(p_snapshots::text) > 8192
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query wait snapshot preflight failed' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value)
        WHERE jsonb_typeof(snapshot.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(snapshot.value)) <> 4
           OR coalesce(snapshot.value->>'database_id','') !~ '^[0-9]{1,5}$'
           OR coalesce(snapshot.value->>'query_fingerprint','') !~ '^[0-9a-f]{64}$'
           OR coalesce(snapshot.value->>'plan_fingerprint','') !~ '^[0-9a-f]{64}$'
           OR jsonb_typeof(snapshot.value->'categories') <> 'array'
    ) THEN
        RAISE EXCEPTION 'query wait snapshot shape is invalid' USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value)
        WHERE (snapshot.value->>'database_id')::integer NOT BETWEEN 1 AND 32767
           OR jsonb_array_length(snapshot.value->'categories') > 32
           OR octet_length((snapshot.value->'categories')::text) > 4096
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value)
        GROUP BY snapshot.value->>'database_id',
                 snapshot.value->>'query_fingerprint',
                 snapshot.value->>'plan_fingerprint'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query wait snapshots have duplicate or invalid plans'
            USING ERRCODE = '22023';
    END IF;
    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value),
             jsonb_array_elements(snapshot.value->'categories') AS category(value)
        WHERE jsonb_typeof(category.value) <> 'object'
           OR (SELECT count(*) FROM jsonb_object_keys(category.value)) <> 2
           OR NOT CASE WHEN coalesce(category.value->>'category','') ~ '^[0-9]{1,2}$'
                 THEN (category.value->>'category')::integer BETWEEN 0 AND 31
                 ELSE false END
           OR NOT CASE WHEN coalesce(category.value->>'waitMilliseconds','') ~ '^[0-9]{1,19}$'
                 THEN (category.value->>'waitMilliseconds')::numeric
                      BETWEEN 1 AND 9223372036854775807
                 ELSE false END
    ) OR EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_snapshots) AS snapshot(value),
             jsonb_array_elements(snapshot.value->'categories') AS category(value)
        GROUP BY snapshot.value->>'database_id',
                 snapshot.value->>'query_fingerprint',
                 snapshot.value->>'plan_fingerprint',
                 category.value->>'category'
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'query wait category shape or identity is invalid'
            USING ERRCODE = '22023';
    END IF;

    SELECT work_key, owner_execution_id INTO v_work_key, v_owner
    FROM telemetry.collection_run
    WHERE run_id = p_run_id AND instance_id = p_instance_id
      AND collector_id = 'queries.performance'
    FOR UPDATE;
    IF v_work_key IS NULL OR v_owner IS NULL THEN
        RAISE EXCEPTION 'query wait run is missing' USING ERRCODE = '55000';
    END IF;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);

    WITH supplied AS (
        SELECT * FROM jsonb_to_recordset(p_snapshots) AS item(
            database_id integer, query_fingerprint text,
            plan_fingerprint text, categories jsonb)
    )
    SELECT count(*) INTO v_valid
    FROM supplied AS item
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = p_run_id
     AND query_row.instance_id = p_instance_id
     AND query_row.database_id = item.database_id
     AND query_row.query_fingerprint = decode(item.query_fingerprint,'hex')
    JOIN events.query_performance_plan AS plan_row
      ON plan_row.collection_run_id = query_row.collection_run_id
     AND plan_row.database_id = query_row.database_id
     AND plan_row.query_fingerprint = query_row.query_fingerprint
     AND plan_row.plan_fingerprint = decode(item.plan_fingerprint,'hex');
    IF v_valid <> jsonb_array_length(p_snapshots) THEN
        RAISE EXCEPTION 'query wait target, query, or plan mismatch'
            USING ERRCODE = '23503';
    END IF;

    INSERT INTO events.query_performance_wait_snapshot
        (collection_run_id, instance_id, database_id,
         query_fingerprint, plan_fingerprint, categories)
    SELECT p_run_id, p_instance_id, item.database_id,
           decode(item.query_fingerprint,'hex'),
           decode(item.plan_fingerprint,'hex'), item.categories
    FROM jsonb_to_recordset(p_snapshots) AS item(
        database_id integer, query_fingerprint text,
        plan_fingerprint text, categories jsonb)
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    PERFORM control.assert_worker_lease(v_work_key, v_owner, p_fence_token);
    RETURN v_inserted;
END;
$fn$;
REVOKE ALL ON FUNCTION control.commit_query_plan_wait_snapshots(uuid,uuid,bigint,jsonb)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_query_plan_wait_snapshots(uuid,uuid,bigint,jsonb)
    TO sqlobserver_collector;

CREATE FUNCTION control.get_query_plan_wait_snapshot(
    p_instance_id uuid, p_run_id uuid, p_database_id integer,
    p_query_fingerprint bytea, p_plan_fingerprint bytea)
RETURNS TABLE(categories jsonb, captured_at timestamptz)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, control, events
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_run_id IS NULL
       OR p_database_id NOT BETWEEN 1 AND 32767
       OR octet_length(p_query_fingerprint) <> 32
       OR octet_length(p_plan_fingerprint) <> 32
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query wait read preflight failed' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY SELECT snapshot.categories, snapshot.captured_at
    FROM events.query_performance_wait_snapshot AS snapshot
    WHERE snapshot.instance_id = p_instance_id
      AND snapshot.collection_run_id = p_run_id
      AND snapshot.database_id = p_database_id
      AND snapshot.query_fingerprint = p_query_fingerprint
      AND snapshot.plan_fingerprint = p_plan_fingerprint;
END;
$fn$;
REVOKE ALL ON FUNCTION control.get_query_plan_wait_snapshot(uuid,uuid,integer,bytea,bytea)
    FROM PUBLIC, sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.get_query_plan_wait_snapshot(uuid,uuid,integer,bytea,bytea)
    TO sqlobserver_server;
