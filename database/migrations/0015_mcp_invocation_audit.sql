-- M11: MCP has a distinct terminal-outcome vocabulary.  audit.activity is
-- retained for administrative writes because its four-value outcome check
-- cannot safely represent invalid, unknown-tool, limited, or oversize calls.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE TABLE audit.mcp_invocation
(
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    invocation_id uuid NOT NULL,
    actor_kind text NOT NULL DEFAULT 'mcp_client',
    actor_identifier text NOT NULL,
    tool_name text NOT NULL,
    action_name text NOT NULL,
    authorization_result text NOT NULL,
    outcome text NOT NULL,
    reason text NOT NULL,
    target_id uuid,
    incident_id uuid,
    correlation_id uuid NOT NULL,
    parameter_digest bytea NOT NULL,
    duration_ms bigint NOT NULL,
    response_bytes bigint NOT NULL,
    safe_detail text NOT NULL DEFAULT '',
    CONSTRAINT pk_mcp_invocation_audit PRIMARY KEY (invocation_id),
    CONSTRAINT ck_mcp_invocation_actor_kind CHECK (actor_kind = 'mcp_client'),
    CONSTRAINT ck_mcp_invocation_actor_identifier CHECK (
        octet_length(actor_identifier) BETWEEN 1 AND 512
        AND actor_identifier = btrim(actor_identifier)
        AND actor_identifier !~ '[[:cntrl:]]'
    ),
    CONSTRAINT ck_mcp_invocation_tool CHECK (
        octet_length(tool_name) BETWEEN 1 AND 128
        AND tool_name ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_mcp_invocation_action CHECK (
        octet_length(action_name) BETWEEN 1 AND 128
        AND action_name ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_mcp_invocation_authorization CHECK (
        authorization_result IN ('allowed', 'denied', 'not_applicable')
    ),
    CONSTRAINT ck_mcp_invocation_outcome CHECK (
        outcome IN ('succeeded', 'denied', 'invalid', 'unknown_tool', 'timeout',
                    'cancelled', 'limited', 'oversize', 'repository_failure')
    ),
    CONSTRAINT ck_mcp_invocation_reason CHECK (
        reason IN ('completed', 'authorization_denied', 'invalid_request',
                   'unknown_tool', 'timeout', 'cancelled', 'concurrency_limit',
                   'response_oversize', 'repository_failure')
    ),
    CONSTRAINT ck_mcp_invocation_digest CHECK (octet_length(parameter_digest) = 32),
    CONSTRAINT ck_mcp_invocation_duration CHECK (duration_ms >= 0),
    CONSTRAINT ck_mcp_invocation_response_size CHECK (response_bytes >= 0),
    CONSTRAINT ck_mcp_invocation_safe_detail CHECK (
        octet_length(safe_detail) <= 128
        AND safe_detail ~ '^[a-zA-Z0-9._-]*$'
    ),
    CONSTRAINT ck_mcp_invocation_denial_pair CHECK (
        (outcome = 'denied') = (authorization_result = 'denied')
    ),
    CONSTRAINT ck_mcp_invocation_success_pair CHECK (
        outcome <> 'succeeded' OR authorization_result = 'allowed'
    )
);

COMMENT ON TABLE audit.mcp_invocation IS
    'Append-only terminal audit for every MCP invocation; no raw parameters, results, query text, plans, tokens, connection strings, or exception messages.';
COMMENT ON COLUMN audit.mcp_invocation.parameter_digest IS
    'SHA-256 of canonicalized JSON parameters; the parameters themselves are never persisted.';

CREATE INDEX ix_mcp_invocation_audit_time
    ON audit.mcp_invocation (recorded_at DESC);
CREATE INDEX ix_mcp_invocation_audit_actor_time
    ON audit.mcp_invocation (actor_identifier, recorded_at DESC);
CREATE INDEX ix_mcp_invocation_audit_target_time
    ON audit.mcp_invocation (target_id, recorded_at DESC)
    WHERE target_id IS NOT NULL;

CREATE TRIGGER mcp_invocation_audit_append_only
BEFORE UPDATE OR DELETE ON audit.mcp_invocation
FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION audit.append_mcp_invocation(
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
       OR p_actor_identifier !~ '^[^[:cntrl:]]{1,512}$'
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
    ON CONFLICT (invocation_id) DO NOTHING;

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

REVOKE ALL ON TABLE audit.mcp_invocation FROM PUBLIC, sqlobserver_server,
    sqlobserver_collector, sqlobserver_auditor;
REVOKE ALL ON FUNCTION audit.append_mcp_invocation(
    uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text
) FROM PUBLIC, sqlobserver_server, sqlobserver_collector, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION audit.append_mcp_invocation(
    uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text
) TO sqlobserver_server;
GRANT SELECT ON TABLE audit.mcp_invocation TO sqlobserver_auditor;
