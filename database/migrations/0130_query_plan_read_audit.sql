-- Plan XML is readable only for an exact target/run/query/plan link. Access
-- audit stores safe identities and outcomes, never the XML or ciphertext.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.query_plan_access_audit (
    audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    instance_id uuid NOT NULL,
    collection_run_id uuid NOT NULL,
    database_id integer NOT NULL CHECK (database_id BETWEEN 1 AND 32767),
    query_fingerprint bytea NOT NULL CHECK (octet_length(query_fingerprint) = 32),
    plan_fingerprint bytea NOT NULL CHECK (octet_length(plan_fingerprint) = 32),
    actor_sid text NOT NULL CHECK (length(actor_sid) BETWEEN 1 AND 184),
    outcome text NOT NULL CHECK (outcome IN ('denied', 'unavailable', 'opened')),
    accessed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
REVOKE ALL ON events.query_plan_access_audit
    FROM PUBLIC, sqlobserver_server, sqlobserver_collector;

CREATE FUNCTION control.get_query_plan_payload(
    p_instance_id uuid, p_run_id uuid, p_database_id integer,
    p_query_fingerprint bytea, p_plan_fingerprint bytea)
RETURNS TABLE (fingerprint bytea, protection_algorithm text, key_identifier text,
    nonce bytea, authentication_tag bytea, ciphertext bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, control, events, security
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_run_id IS NULL
       OR p_database_id NOT BETWEEN 1 AND 32767
       OR octet_length(p_query_fingerprint) <> 32
       OR octet_length(p_plan_fingerprint) <> 32
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query plan read preflight failed' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
    SELECT payload.fingerprint, payload.protection_algorithm,
           payload.key_identifier, payload.nonce,
           payload.authentication_tag, payload.ciphertext
    FROM events.query_performance_plan_content_link AS link
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = link.collection_run_id
     AND query_row.instance_id = link.instance_id
     AND query_row.database_id = link.database_id
     AND query_row.query_fingerprint = link.query_fingerprint
    JOIN events.query_performance_plan AS plan_row
      ON plan_row.collection_run_id = query_row.collection_run_id
     AND plan_row.database_id = query_row.database_id
     AND plan_row.query_fingerprint = query_row.query_fingerprint
     AND plan_row.plan_fingerprint = link.plan_fingerprint
    JOIN security.protected_diagnostic_payload AS payload
      ON payload.payload_id = link.content_reference
     AND payload.instance_id = link.instance_id
     AND payload.payload_kind = 'execution_plan'
    WHERE link.instance_id = p_instance_id
      AND link.collection_run_id = p_run_id
      AND link.database_id = p_database_id
      AND link.query_fingerprint = p_query_fingerprint
      AND link.plan_fingerprint = p_plan_fingerprint
      AND link.content_available
      AND octet_length(payload.ciphertext) BETWEEN 1 AND 1048576
    ORDER BY payload.created_at DESC, payload.payload_id DESC
    LIMIT 1;
END;
$fn$;

CREATE FUNCTION control.audit_query_plan_access(
    p_instance_id uuid, p_run_id uuid, p_database_id integer,
    p_query_fingerprint bytea, p_plan_fingerprint bytea,
    p_actor_sid text, p_outcome text)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, control, events
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_run_id IS NULL
       OR p_database_id NOT BETWEEN 1 AND 32767
       OR octet_length(p_query_fingerprint) <> 32
       OR octet_length(p_plan_fingerprint) <> 32
       OR p_actor_sid IS NULL OR length(p_actor_sid) NOT BETWEEN 1 AND 184
       OR p_outcome NOT IN ('denied', 'unavailable', 'opened') THEN
        RAISE EXCEPTION 'query plan audit preflight failed' USING ERRCODE = '22023';
    END IF;
    INSERT INTO events.query_plan_access_audit
        (instance_id, collection_run_id, database_id, query_fingerprint,
         plan_fingerprint, actor_sid, outcome)
    VALUES (p_instance_id, p_run_id, p_database_id, p_query_fingerprint,
            p_plan_fingerprint, p_actor_sid, p_outcome);
END;
$fn$;

REVOKE ALL ON FUNCTION control.get_query_plan_payload(uuid,uuid,integer,bytea,bytea)
    FROM PUBLIC, sqlobserver_collector;
REVOKE ALL ON FUNCTION control.audit_query_plan_access(uuid,uuid,integer,bytea,bytea,text,text)
    FROM PUBLIC, sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.get_query_plan_payload(uuid,uuid,integer,bytea,bytea)
    TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION control.audit_query_plan_access(uuid,uuid,integer,bytea,bytea,text,text)
    TO sqlobserver_server;
