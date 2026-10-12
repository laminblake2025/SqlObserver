-- Repair MCP audit append validation and unambiguous invocation replay.
-- Preserve the table's existing byte, whitespace, and control-character bounds.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION audit.append_mcp_invocation(
    p_invocation_id uuid,
    p_actor_identifier text,
    p_tool_name text,
    p_action_name text,
    p_authorization_result text,
    p_outcome text,
    p_reason text,
    p_target_id uuid,
    p_incident_id uuid,
    p_correlation_id uuid,
    p_parameter_digest bytea,
    p_duration_ms bigint,
    p_response_bytes bigint,
    p_safe_detail text
)
RETURNS TABLE(invocation_id uuid, recorded_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path = pg_catalog, audit
SET TimeZone = 'UTC'
AS $mcp_append$
DECLARE
    existing audit.mcp_invocation%ROWTYPE;
BEGIN
    IF p_invocation_id IS NULL OR p_actor_identifier IS NULL OR p_tool_name IS NULL
       OR p_action_name IS NULL OR p_authorization_result IS NULL OR p_outcome IS NULL
       OR p_reason IS NULL OR p_correlation_id IS NULL OR p_parameter_digest IS NULL
       OR p_duration_ms IS NULL OR p_response_bytes IS NULL OR p_safe_detail IS NULL
       OR octet_length(p_parameter_digest) <> 32
       OR p_duration_ms < 0 OR p_response_bytes < 0
       OR octet_length(p_actor_identifier) NOT BETWEEN 1 AND 512
       OR p_actor_identifier <> btrim(p_actor_identifier)
       OR p_actor_identifier ~ '[[:cntrl:]]'
       OR p_tool_name !~ '^[a-z][a-z0-9._-]{0,127}$'
       OR p_action_name !~ '^[a-z][a-z0-9._-]{0,127}$'
       OR p_authorization_result NOT IN ('allowed','denied','not_applicable')
       OR p_outcome NOT IN ('succeeded','denied','invalid','unknown_tool','timeout','cancelled','limited','oversize','repository_failure')
       OR p_reason NOT IN ('completed','authorization_denied','invalid_request','unknown_tool','timeout','cancelled','concurrency_limit','response_oversize','repository_failure')
       OR octet_length(p_safe_detail) > 128 OR p_safe_detail !~ '^[a-zA-Z0-9._-]*$'
       OR (p_outcome = 'denied') <> (p_authorization_result = 'denied')
       OR (p_outcome = 'succeeded' AND p_authorization_result <> 'allowed') THEN
        RAISE EXCEPTION 'MCP audit bounds rejected' USING ERRCODE = '22023';
    END IF;

    INSERT INTO audit.mcp_invocation
    (
        recorded_at, invocation_id, actor_identifier, tool_name, action_name,
        authorization_result, outcome, reason, target_id, incident_id,
        correlation_id, parameter_digest, duration_ms, response_bytes, safe_detail
    )
    VALUES
    (
        clock_timestamp(), p_invocation_id,
        p_actor_identifier, p_tool_name, p_action_name, p_authorization_result,
        p_outcome, p_reason, p_target_id, p_incident_id, p_correlation_id,
        p_parameter_digest, p_duration_ms, p_response_bytes, p_safe_detail
    )
    ON CONFLICT ON CONSTRAINT pk_mcp_invocation_audit DO NOTHING;

    SELECT * INTO existing FROM audit.mcp_invocation a WHERE a.invocation_id = p_invocation_id;
    IF existing.actor_kind IS DISTINCT FROM 'mcp_client'
       OR existing.actor_identifier IS DISTINCT FROM p_actor_identifier
       OR existing.tool_name IS DISTINCT FROM p_tool_name
       OR existing.action_name IS DISTINCT FROM p_action_name
       OR existing.authorization_result IS DISTINCT FROM p_authorization_result
       OR existing.outcome IS DISTINCT FROM p_outcome
       OR existing.reason IS DISTINCT FROM p_reason
       OR existing.target_id IS DISTINCT FROM p_target_id
       OR existing.incident_id IS DISTINCT FROM p_incident_id
       OR existing.correlation_id IS DISTINCT FROM p_correlation_id
       OR existing.parameter_digest IS DISTINCT FROM p_parameter_digest
       OR existing.duration_ms IS DISTINCT FROM p_duration_ms
       OR existing.response_bytes IS DISTINCT FROM p_response_bytes
       OR existing.safe_detail IS DISTINCT FROM p_safe_detail THEN
        RAISE EXCEPTION 'MCP audit invocation identity conflict' USING ERRCODE = '40001';
    END IF;
    invocation_id := existing.invocation_id;
    recorded_at := existing.recorded_at;
    RETURN NEXT;
END;
$mcp_append$;

REVOKE ALL ON FUNCTION audit.append_mcp_invocation(
    uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text
) FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION audit.append_mcp_invocation(
    uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text
) TO sqlobserver_server;
