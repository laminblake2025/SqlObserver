-- Query text is returned only for an exact run/query/target link. Audit rows
-- deliberately contain identities and outcomes, never ciphertext or plaintext.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.query_text_access_audit (
    audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    instance_id uuid NOT NULL,
    collection_run_id uuid NOT NULL,
    database_id integer NOT NULL CHECK (database_id BETWEEN 1 AND 32767),
    query_fingerprint bytea NOT NULL CHECK (octet_length(query_fingerprint) = 32),
    actor_sid text NOT NULL CHECK (length(actor_sid) BETWEEN 1 AND 184),
    outcome text NOT NULL CHECK (outcome IN ('denied', 'unavailable', 'opened')),
    accessed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
REVOKE ALL ON events.query_text_access_audit FROM PUBLIC, sqlobserver_server, sqlobserver_collector;

CREATE FUNCTION control.get_query_text_payload(
    p_instance_id uuid, p_run_id uuid, p_database_id integer, p_query_fingerprint bytea)
RETURNS TABLE (fingerprint bytea, protection_algorithm text, key_identifier text,
    nonce bytea, authentication_tag bytea, ciphertext bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path = pg_catalog, control, events, security
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_run_id IS NULL OR p_database_id NOT BETWEEN 1 AND 32767
       OR octet_length(p_query_fingerprint) <> 32
       OR NOT control.query_performance_target_claim(p_instance_id) THEN
        RAISE EXCEPTION 'query text read preflight failed' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
    SELECT payload.fingerprint, payload.protection_algorithm, payload.key_identifier,
           payload.nonce, payload.authentication_tag, payload.ciphertext
    FROM events.query_performance_content_link AS link
    JOIN events.query_performance_query AS query_row
      ON query_row.collection_run_id = link.collection_run_id
     AND query_row.instance_id = link.instance_id
     AND query_row.database_id = link.database_id
     AND query_row.query_fingerprint = link.query_fingerprint
    JOIN security.protected_diagnostic_payload AS payload
      ON payload.payload_id = link.content_reference
     AND payload.instance_id = link.instance_id
     AND payload.payload_kind = 'query_text'
    WHERE link.instance_id = p_instance_id AND link.collection_run_id = p_run_id
      AND link.database_id = p_database_id
      AND link.query_fingerprint = p_query_fingerprint
      AND link.content_available
      AND octet_length(payload.ciphertext) BETWEEN 1 AND 16384
    ORDER BY payload.created_at DESC, payload.payload_id DESC
    LIMIT 1;
END;
$fn$;

CREATE FUNCTION control.audit_query_text_access(
    p_instance_id uuid, p_run_id uuid, p_database_id integer,
    p_query_fingerprint bytea, p_actor_sid text, p_outcome text)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, control, events
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_run_id IS NULL OR p_database_id NOT BETWEEN 1 AND 32767
       OR octet_length(p_query_fingerprint) <> 32
       OR p_actor_sid IS NULL OR length(p_actor_sid) NOT BETWEEN 1 AND 184
       OR p_outcome NOT IN ('denied', 'unavailable', 'opened') THEN
        RAISE EXCEPTION 'query text audit preflight failed' USING ERRCODE = '22023';
    END IF;
    INSERT INTO events.query_text_access_audit
        (instance_id, collection_run_id, database_id, query_fingerprint, actor_sid, outcome)
    VALUES (p_instance_id, p_run_id, p_database_id, p_query_fingerprint, p_actor_sid, p_outcome);
END;
$fn$;

REVOKE ALL ON FUNCTION control.get_query_text_payload(uuid,uuid,integer,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION control.audit_query_text_access(uuid,uuid,integer,bytea,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.get_query_text_payload(uuid,uuid,integer,bytea) TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION control.audit_query_text_access(uuid,uuid,integer,bytea,text,text) TO sqlobserver_server;
