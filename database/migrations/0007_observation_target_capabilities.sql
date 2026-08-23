-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

ALTER TABLE control.observation_target
    ADD COLUMN host_name text,
    ADD COLUMN instance_name text,
    ADD COLUMN tcp_port integer,
    ADD COLUMN certificate_host_name text,
    ADD COLUMN connect_timeout interval,
    ADD COLUMN authentication_mode text,
    ADD COLUMN transport_security_mode text,
    ADD COLUMN lifecycle_state text NOT NULL DEFAULT 'pending_discovery',
    ADD COLUMN revision bigint NOT NULL DEFAULT 1,
    ADD COLUMN updated_at timestamptz,
    ADD COLUMN discovery_requested_at timestamptz;

UPDATE control.observation_target
SET
    lifecycle_state = CASE WHEN retired_at IS NULL THEN 'pending_discovery' ELSE 'retired' END,
    updated_at = created_at,
    discovery_requested_at = created_at;

ALTER TABLE control.observation_target
    ALTER COLUMN updated_at SET NOT NULL,
    ALTER COLUMN updated_at SET DEFAULT clock_timestamp(),
    ALTER COLUMN discovery_requested_at SET NOT NULL,
    ALTER COLUMN discovery_requested_at SET DEFAULT clock_timestamp(),
    ADD CONSTRAINT ck_observation_target_endpoint_shape CHECK
    (
        (
            host_name IS NULL
            AND instance_name IS NULL
            AND tcp_port IS NULL
            AND certificate_host_name IS NULL
            AND connect_timeout IS NULL
            AND authentication_mode IS NULL
            AND transport_security_mode IS NULL
        )
        OR
        (
            host_name IS NOT NULL
            AND ((instance_name IS NULL) <> (tcp_port IS NULL))
            AND connect_timeout IS NOT NULL
            AND authentication_mode = 'windows_integrated_service_identity'
            AND transport_security_mode = 'mandatory_validated'
        )
    ),
    ADD CONSTRAINT ck_observation_target_host_name CHECK
    (
        host_name IS NULL
        OR
        (
            octet_length(host_name) BETWEEN 1 AND 255
            AND host_name ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'
        )
    ),
    ADD CONSTRAINT ck_observation_target_instance_name CHECK
    (
        instance_name IS NULL
        OR
        (
            octet_length(instance_name) BETWEEN 1 AND 128
            AND instance_name ~ '^[A-Za-z0-9][A-Za-z0-9_$-]*$'
        )
    ),
    ADD CONSTRAINT ck_observation_target_tcp_port CHECK
    (
        tcp_port IS NULL OR tcp_port BETWEEN 1 AND 65535
    ),
    ADD CONSTRAINT ck_observation_target_certificate_host CHECK
    (
        certificate_host_name IS NULL
        OR
        (
            octet_length(certificate_host_name) BETWEEN 1 AND 255
            AND certificate_host_name ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'
        )
    ),
    ADD CONSTRAINT ck_observation_target_connect_timeout CHECK
    (
        connect_timeout IS NULL
        OR connect_timeout BETWEEN interval '1 second' AND interval '30 seconds'
    ),
    ADD CONSTRAINT ck_observation_target_lifecycle CHECK
    (
        lifecycle_state IN ('pending_discovery', 'active', 'disabled', 'retired')
    ),
    ADD CONSTRAINT ck_observation_target_revision CHECK (revision > 0),
    ADD CONSTRAINT ck_observation_target_update_times CHECK
    (
        updated_at >= created_at
        AND discovery_requested_at >= created_at
        AND discovery_requested_at <= updated_at
        AND (retired_at IS NULL OR retired_at BETWEEN created_at AND updated_at)
    ),
    ADD CONSTRAINT ck_observation_target_retired_state CHECK
    (
        (lifecycle_state = 'retired') = (retired_at IS NOT NULL)
    );

COMMENT ON COLUMN control.observation_target.host_name IS
    'Structured SQL Server host only. It is never a connection string and contains no credential.';
COMMENT ON COLUMN control.observation_target.authentication_mode IS
    'M3 accepts only windows_integrated_service_identity; reusable target secrets are outside this slice.';
COMMENT ON COLUMN control.observation_target.transport_security_mode IS
    'M3 accepts only mandatory_validated; certificate validation cannot be silently disabled.';
COMMENT ON COLUMN control.observation_target.revision IS
    'Monotonic repository-issued configuration fence used by capability discovery.';

CREATE INDEX ix_observation_target_discovery_due
    ON control.observation_target (discovery_requested_at, instance_id)
    WHERE lifecycle_state = 'pending_discovery' AND host_name IS NOT NULL;

CREATE TABLE control.capability_discovery_attempt
(
    attempt_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL,
    collector_id text NOT NULL,
    collector_manifest_version integer NOT NULL,
    output_schema_version integer NOT NULL,
    outcome text NOT NULL,
    discovery_reason text,
    sql_server_major_version integer,
    sql_server_minor_version integer,
    sql_server_build integer,
    sql_server_revision integer,
    edition text,
    engine_edition integer,
    platform text,
    authentication_scheme text NOT NULL,
    transport_encrypted boolean NOT NULL,
    is_sysadmin boolean NOT NULL,
    duration_ms bigint NOT NULL,
    response_bytes bigint NOT NULL,
    checked_at timestamptz NOT NULL,
    valid_until timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL,
    CONSTRAINT fk_capability_attempt_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT ck_capability_attempt_revision CHECK (target_revision > 0),
    CONSTRAINT ck_capability_attempt_collector_id CHECK
    (
        octet_length(collector_id) BETWEEN 1 AND 128
        AND collector_id ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_capability_attempt_manifest_version CHECK
        (collector_manifest_version BETWEEN 1 AND 9999),
    CONSTRAINT ck_capability_attempt_output_version CHECK
        (output_schema_version BETWEEN 1 AND 9999),
    CONSTRAINT ck_capability_attempt_outcome CHECK
    (
        outcome IN
        (
            'pending',
            'supported',
            'degraded',
            'unsupported',
            'unreachable',
            'authentication_failed',
            'tls_validation_failed',
            'timed_out',
            'security_policy_rejected'
        )
    ),
    CONSTRAINT ck_capability_attempt_reason CHECK
    (
        (outcome = 'pending' AND discovery_reason IS NULL)
        OR
        (outcome = 'supported' AND discovery_reason = 'verified')
        OR
        (outcome = 'degraded' AND discovery_reason IN
            (
                'optional_capability_unavailable',
                'required_permission_missing',
                'authentication_scheme_fallback'
            ))
        OR
        (outcome = 'unsupported' AND discovery_reason IN
            ('unsupported_version', 'unsupported_platform', 'unsupported_edition'))
        OR
        (outcome = 'unreachable' AND discovery_reason IN
            ('network_unreachable', 'connection_refused'))
        OR
        (outcome = 'authentication_failed' AND discovery_reason = 'authentication_rejected')
        OR
        (outcome = 'tls_validation_failed' AND discovery_reason = 'certificate_validation_failed')
        OR
        (outcome = 'timed_out' AND discovery_reason = 'discovery_timed_out')
        OR
        (outcome = 'security_policy_rejected' AND discovery_reason IN
            ('excessive_privilege', 'transport_not_encrypted', 'authentication_scheme_mismatch'))
    ),
    CONSTRAINT ck_capability_attempt_major_version CHECK
    (
        sql_server_major_version IS NULL OR sql_server_major_version BETWEEN 1 AND 99
    ),
    CONSTRAINT ck_capability_attempt_minor_version CHECK
    (
        sql_server_minor_version IS NULL OR sql_server_minor_version BETWEEN 0 AND 99
    ),
    CONSTRAINT ck_capability_attempt_build CHECK
    (
        sql_server_build IS NULL OR sql_server_build BETWEEN 0 AND 99999
    ),
    CONSTRAINT ck_capability_attempt_version_revision CHECK
    (
        sql_server_revision IS NULL OR sql_server_revision BETWEEN 0 AND 99999
    ),
    CONSTRAINT ck_capability_attempt_edition CHECK
    (
        edition IS NULL
        OR
        (
            octet_length(edition) BETWEEN 1 AND 256
            AND edition = btrim(edition)
            AND edition !~ '[[:cntrl:]]'
        )
    ),
    CONSTRAINT ck_capability_attempt_platform CHECK
    (
        platform IS NULL
        OR platform IN
        (
            'windows',
            'linux',
            'other'
        )
    ),
    CONSTRAINT ck_capability_attempt_engine_edition CHECK
    (
        engine_edition IS NULL OR engine_edition IN (2, 3, 4, 5, 6, 8, 255)
    ),
    CONSTRAINT ck_capability_attempt_auth_scheme CHECK
    (
        authentication_scheme IN ('kerberos', 'ntlm', 'sql_authentication', 'unknown')
    ),
    CONSTRAINT ck_capability_attempt_duration CHECK
        (duration_ms BETWEEN 0 AND 120000),
    CONSTRAINT ck_capability_attempt_response_size CHECK
        (response_bytes BETWEEN 0 AND 1048576),
    CONSTRAINT ck_capability_attempt_validity CHECK
    (
        isfinite(checked_at)
        AND isfinite(valid_until)
        AND valid_until >= checked_at + interval '1 minute'
        AND valid_until <= checked_at + interval '7 days'
    ),
    CONSTRAINT ck_capability_attempt_identity_shape CHECK
    (
        (
            outcome IN ('supported', 'degraded', 'unsupported', 'security_policy_rejected')
            AND sql_server_major_version IS NOT NULL
            AND sql_server_minor_version IS NOT NULL
            AND sql_server_build IS NOT NULL
            AND sql_server_revision IS NOT NULL
            AND edition IS NOT NULL
            AND engine_edition IS NOT NULL
            AND platform IS NOT NULL
        )
        OR
        (
            outcome NOT IN ('supported', 'degraded', 'unsupported', 'security_policy_rejected')
            AND sql_server_major_version IS NULL
            AND sql_server_minor_version IS NULL
            AND sql_server_build IS NULL
            AND sql_server_revision IS NULL
            AND edition IS NULL
            AND engine_edition IS NULL
            AND platform IS NULL
        )
    ),
    CONSTRAINT ck_capability_attempt_security_evidence CHECK
    (
        (
            outcome = 'supported'
            AND authentication_scheme = 'kerberos'
            AND transport_encrypted
            AND NOT is_sysadmin
        )
        OR
        (
            outcome = 'degraded'
            AND authentication_scheme IN ('kerberos', 'ntlm')
            AND transport_encrypted
            AND NOT is_sysadmin
            AND
            (
                (authentication_scheme = 'ntlm' AND discovery_reason = 'authentication_scheme_fallback')
                OR
                (authentication_scheme = 'kerberos' AND discovery_reason <> 'authentication_scheme_fallback')
            )
        )
        OR
        (
            outcome = 'unsupported'
            AND authentication_scheme IN ('kerberos', 'ntlm')
            AND transport_encrypted
            AND NOT is_sysadmin
        )
        OR
        (
            outcome = 'security_policy_rejected'
            AND
            (
                (discovery_reason = 'excessive_privilege' AND is_sysadmin)
                OR (discovery_reason = 'transport_not_encrypted' AND NOT transport_encrypted)
                OR
                (
                    discovery_reason = 'authentication_scheme_mismatch'
                    AND authentication_scheme NOT IN ('kerberos', 'ntlm')
                )
            )
        )
        OR
        (
            outcome NOT IN ('supported', 'degraded', 'unsupported', 'security_policy_rejected')
            AND authentication_scheme = 'unknown'
            AND NOT transport_encrypted
            AND NOT is_sysadmin
        )
        OR
        (
            outcome = 'pending'
            AND authentication_scheme = 'unknown'
            AND NOT transport_encrypted
            AND NOT is_sysadmin
        )
    )
);

COMMENT ON TABLE control.capability_discovery_attempt IS
    'Append-only bounded discovery evidence. It contains structured facts and safe codes, never target errors, SQL text, or credentials.';

CREATE INDEX ix_capability_attempt_target_time
    ON control.capability_discovery_attempt (instance_id, recorded_at DESC, attempt_id DESC);

CREATE TABLE control.capability_profile
(
    profile_id uuid PRIMARY KEY,
    attempt_id uuid NOT NULL UNIQUE,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL,
    recorded_at timestamptz NOT NULL,
    CONSTRAINT fk_capability_profile_attempt FOREIGN KEY (attempt_id)
        REFERENCES control.capability_discovery_attempt (attempt_id),
    CONSTRAINT fk_capability_profile_target FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT ck_capability_profile_revision CHECK (target_revision > 0)
);

COMMENT ON TABLE control.capability_profile IS
    'Append-only profile identity associated one-to-one with a bounded discovery attempt.';

CREATE INDEX ix_capability_profile_target_time
    ON control.capability_profile (instance_id, recorded_at DESC, profile_id DESC);

CREATE TABLE control.capability_profile_permission
(
    profile_id uuid NOT NULL,
    permission_name text NOT NULL,
    permission_scope text NOT NULL,
    permission_state text NOT NULL,
    CONSTRAINT pk_capability_profile_permission PRIMARY KEY
        (profile_id, permission_name, permission_scope),
    CONSTRAINT fk_capability_permission_profile FOREIGN KEY (profile_id)
        REFERENCES control.capability_profile (profile_id),
    CONSTRAINT ck_capability_permission_name CHECK
    (
        octet_length(permission_name) BETWEEN 1 AND 128
        AND permission_name ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_capability_permission_scope CHECK
    (
        permission_scope IN ('server', 'database')
    ),
    CONSTRAINT ck_capability_permission_state CHECK
    (
        permission_state IN ('granted', 'denied', 'unknown', 'not_applicable')
    )
);

COMMENT ON TABLE control.capability_profile_permission IS
    'Append-only allowlisted permission observations; no arbitrary permission or error text is accepted.';

CREATE TABLE control.capability_profile_reason
(
    profile_id uuid NOT NULL,
    capability_name text NOT NULL,
    availability text NOT NULL,
    reason_code text NOT NULL,
    CONSTRAINT pk_capability_profile_reason PRIMARY KEY (profile_id, capability_name),
    CONSTRAINT fk_capability_reason_profile FOREIGN KEY (profile_id)
        REFERENCES control.capability_profile (profile_id),
    CONSTRAINT ck_capability_name CHECK
    (
        octet_length(capability_name) BETWEEN 1 AND 128
        AND capability_name ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_capability_availability CHECK
    (
        availability IN ('available', 'unavailable', 'unknown')
    ),
    CONSTRAINT ck_capability_reason_code CHECK
    (
        reason_code IN
        (
            'verified',
            'version_unsupported',
            'platform_unsupported',
            'edition_unsupported',
            'permission_denied',
            'feature_disabled',
            'enhanced_setup_absent',
            'probe_unavailable'
        )
        AND ((availability = 'available') = (reason_code = 'verified'))
    )
);

COMMENT ON TABLE control.capability_profile_reason IS
    'Append-only allowlisted explanation codes; unrestricted target error text is prohibited.';

CREATE FUNCTION control.reject_capability_history_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
AS $sqlobserver$
BEGIN
    RAISE EXCEPTION 'capability discovery history is append-only'
        USING ERRCODE = '55000';
END
$sqlobserver$;

CREATE FUNCTION control.list_due_capability_targets(p_max_targets integer)
RETURNS TABLE
(
    target_instance_id uuid,
    target_revision bigint,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    has_more boolean,
    repository_time timestamptz
)
LANGUAGE sql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    WITH repository_clock AS MATERIALIZED
    (
        SELECT clock_timestamp() AS value
    ),
    due AS MATERIALIZED
    (
        SELECT
            target.instance_id,
            target.revision,
            target.host_name,
            target.instance_name,
            target.tcp_port,
            target.certificate_host_name,
            target.connect_timeout,
            target.authentication_mode,
            target.transport_security_mode,
            target.discovery_requested_at,
            repository_clock.value AS repository_time
        FROM control.observation_target AS target
        CROSS JOIN repository_clock
        LEFT JOIN LATERAL
        (
            SELECT attempt.recorded_at, attempt.checked_at, attempt.valid_until
            FROM control.capability_discovery_attempt AS attempt
            WHERE attempt.instance_id = target.instance_id
            ORDER BY attempt.recorded_at DESC, attempt.attempt_id DESC
            LIMIT 1
        ) AS latest_attempt ON true
        WHERE p_max_targets BETWEEN 1 AND 16
          AND target.host_name IS NOT NULL
          AND target.lifecycle_state IN ('pending_discovery', 'active')
          AND
          (
              latest_attempt.recorded_at IS NULL
              OR latest_attempt.recorded_at < target.discovery_requested_at
              OR latest_attempt.recorded_at
                    + (latest_attempt.valid_until - latest_attempt.checked_at)
                    <= repository_clock.value
          )
        ORDER BY target.discovery_requested_at, target.instance_id
        LIMIT p_max_targets + 1
    )
    SELECT
        due.instance_id,
        due.revision,
        due.host_name,
        due.instance_name,
        due.tcp_port,
        due.certificate_host_name,
        due.connect_timeout,
        due.authentication_mode,
        due.transport_security_mode,
        (SELECT count(*) > p_max_targets FROM due),
        due.repository_time
    FROM due
    ORDER BY due.discovery_requested_at, due.instance_id
    LIMIT p_max_targets;
$sqlobserver$;

CREATE FUNCTION control.get_latest_capability_profile(p_instance_id uuid)
RETURNS TABLE
(
    profile_id uuid,
    target_instance_id uuid,
    target_revision bigint,
    collector_id text,
    collector_manifest_version integer,
    output_schema_version integer,
    outcome text,
    discovery_reason text,
    sql_server_major_version integer,
    sql_server_minor_version integer,
    sql_server_build integer,
    sql_server_revision integer,
    edition text,
    engine_edition integer,
    platform text,
    authentication_scheme text,
    transport_encrypted boolean,
    is_sysadmin boolean,
    duration_ms bigint,
    response_bytes bigint,
    checked_at timestamptz,
    valid_until timestamptz,
    recorded_at timestamptz
)
LANGUAGE sql
SECURITY DEFINER
STABLE
PARALLEL SAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    SELECT
        profile.profile_id,
        attempt.instance_id,
        attempt.target_revision,
        attempt.collector_id,
        attempt.collector_manifest_version,
        attempt.output_schema_version,
        attempt.outcome,
        attempt.discovery_reason,
        attempt.sql_server_major_version,
        attempt.sql_server_minor_version,
        attempt.sql_server_build,
        attempt.sql_server_revision,
        attempt.edition,
        attempt.engine_edition,
        attempt.platform,
        attempt.authentication_scheme,
        attempt.transport_encrypted,
        attempt.is_sysadmin,
        attempt.duration_ms,
        attempt.response_bytes,
        attempt.checked_at,
        attempt.valid_until,
        attempt.recorded_at
    FROM control.capability_profile AS profile
    INNER JOIN control.capability_discovery_attempt AS attempt
        ON attempt.attempt_id = profile.attempt_id
    WHERE profile.instance_id = p_instance_id
    ORDER BY profile.recorded_at DESC, profile.profile_id DESC
    LIMIT 1;
$sqlobserver$;

CREATE FUNCTION control.get_latest_capability_profiles(p_instance_ids uuid[])
RETURNS TABLE
(
    profile_id uuid,
    target_instance_id uuid,
    target_revision bigint,
    collector_id text,
    collector_manifest_version integer,
    output_schema_version integer,
    outcome text,
    discovery_reason text,
    sql_server_major_version integer,
    sql_server_minor_version integer,
    sql_server_build integer,
    sql_server_revision integer,
    edition text,
    engine_edition integer,
    platform text,
    authentication_scheme text,
    transport_encrypted boolean,
    is_sysadmin boolean,
    duration_ms bigint,
    response_bytes bigint,
    checked_at timestamptz,
    valid_until timestamptz,
    recorded_at timestamptz,
    capability_names text[],
    capability_availabilities text[],
    capability_reasons text[],
    permission_names text[],
    permission_scopes text[],
    permission_states text[]
)
LANGUAGE sql
SECURITY DEFINER
STABLE
PARALLEL SAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    WITH requested AS MATERIALIZED
    (
        SELECT DISTINCT requested_id AS instance_id
        FROM unnest(p_instance_ids) AS requested_id
        WHERE cardinality(p_instance_ids) BETWEEN 1 AND 100
    )
    SELECT
        latest.profile_id,
        latest.instance_id,
        latest.target_revision,
        latest.collector_id,
        latest.collector_manifest_version,
        latest.output_schema_version,
        latest.outcome,
        latest.discovery_reason,
        latest.sql_server_major_version,
        latest.sql_server_minor_version,
        latest.sql_server_build,
        latest.sql_server_revision,
        latest.edition,
        latest.engine_edition,
        latest.platform,
        latest.authentication_scheme,
        latest.transport_encrypted,
        latest.is_sysadmin,
        latest.duration_ms,
        latest.response_bytes,
        latest.checked_at,
        latest.valid_until,
        latest.recorded_at,
        coalesce(capabilities.names, ARRAY[]::text[]),
        coalesce(capabilities.availabilities, ARRAY[]::text[]),
        coalesce(capabilities.reasons, ARRAY[]::text[]),
        coalesce(permissions.names, ARRAY[]::text[]),
        coalesce(permissions.scopes, ARRAY[]::text[]),
        coalesce(permissions.states, ARRAY[]::text[])
    FROM requested
    CROSS JOIN LATERAL
    (
        SELECT
            profile.profile_id,
            attempt.*
        FROM control.capability_profile AS profile
        INNER JOIN control.capability_discovery_attempt AS attempt
            ON attempt.attempt_id = profile.attempt_id
        WHERE profile.instance_id = requested.instance_id
        ORDER BY profile.recorded_at DESC, profile.profile_id DESC
        LIMIT 1
    ) AS latest
    LEFT JOIN LATERAL
    (
        SELECT
            array_agg(reason.capability_name ORDER BY reason.capability_name) AS names,
            array_agg(reason.availability ORDER BY reason.capability_name) AS availabilities,
            array_agg(reason.reason_code ORDER BY reason.capability_name) AS reasons
        FROM control.capability_profile_reason AS reason
        WHERE reason.profile_id = latest.profile_id
    ) AS capabilities ON true
    LEFT JOIN LATERAL
    (
        SELECT
            array_agg(permission.permission_name
                ORDER BY permission.permission_name, permission.permission_scope) AS names,
            array_agg(permission.permission_scope
                ORDER BY permission.permission_name, permission.permission_scope) AS scopes,
            array_agg(permission.permission_state
                ORDER BY permission.permission_name, permission.permission_scope) AS states
        FROM control.capability_profile_permission AS permission
        WHERE permission.profile_id = latest.profile_id
    ) AS permissions ON true
    ORDER BY latest.instance_id;
$sqlobserver$;

CREATE FUNCTION control.get_capability_profile_reasons(p_profile_id uuid)
RETURNS TABLE(capability_name text, availability text, reason_code text)
LANGUAGE sql
SECURITY DEFINER
STABLE
PARALLEL SAFE
SET search_path = pg_catalog
AS $sqlobserver$
    SELECT reason.capability_name, reason.availability, reason.reason_code
    FROM control.capability_profile_reason AS reason
    WHERE reason.profile_id = p_profile_id
    ORDER BY reason.capability_name;
$sqlobserver$;

CREATE FUNCTION control.get_capability_profile_permissions(p_profile_id uuid)
RETURNS TABLE(permission_name text, permission_scope text, permission_state text)
LANGUAGE sql
SECURITY DEFINER
STABLE
PARALLEL SAFE
SET search_path = pg_catalog
AS $sqlobserver$
    SELECT permission.permission_name, permission.permission_scope, permission.permission_state
    FROM control.capability_profile_permission AS permission
    WHERE permission.profile_id = p_profile_id
    ORDER BY permission.permission_name, permission.permission_scope;
$sqlobserver$;

CREATE FUNCTION control.mutate_observation_target(
    p_operation text,
    p_instance_id uuid,
    p_expected_revision bigint,
    p_display_name text,
    p_host_name text,
    p_instance_name text,
    p_tcp_port integer,
    p_certificate_host_name text,
    p_connect_timeout interval,
    p_authentication_mode text,
    p_transport_security_mode text,
    p_actor_identifier text,
    p_correlation_id uuid,
    p_audit_action text
)
RETURNS TABLE
(
    result_status text,
    target_instance_id uuid,
    target_instance_key text,
    target_display_name text,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    target_lifecycle_state text,
    target_revision bigint,
    target_created_at timestamptz,
    target_discovery_requested_at timestamptz,
    target_updated_at timestamptz,
    target_retired_at timestamptz,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_target control.observation_target%ROWTYPE;
    captured_repository_time timestamptz;
    selected_status text;
    audit_outcome text;
    audit_reason text;
    expected_action text;
BEGIN
    expected_action := CASE p_operation
        WHEN 'update' THEN 'update_observation_target'
        WHEN 'retire' THEN 'retire_observation_target'
        WHEN 'rediscovery' THEN 'request_capability_rediscovery'
    END;

    IF expected_action IS NULL
       OR p_audit_action IS DISTINCT FROM expected_action
       OR p_instance_id IS NULL
       OR p_instance_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_expected_revision IS NULL
       OR p_expected_revision <= 0 THEN
        RAISE EXCEPTION 'invalid observation-target mutation identity or action'
            USING ERRCODE = '22023';
    END IF;

    IF p_operation = 'update' THEN
        PERFORM control.validate_observation_target_configuration(
            p_display_name,
            p_host_name,
            p_instance_name,
            p_tcp_port,
            p_certificate_host_name,
            p_connect_timeout,
            p_authentication_mode,
            p_transport_security_mode);
    END IF;

    SELECT target.*
    INTO selected_target
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
    FOR UPDATE;
    captured_repository_time := clock_timestamp();

    IF NOT FOUND THEN
        selected_status := 'not_found';
        audit_outcome := 'conflict';
        audit_reason := 'target_not_found';
    ELSIF selected_target.lifecycle_state = 'retired' THEN
        selected_status := 'already_retired';
        audit_outcome := 'conflict';
        audit_reason := 'target_retired';
    ELSIF selected_target.revision <> p_expected_revision THEN
        selected_status := 'revision_conflict';
        audit_outcome := 'conflict';
        audit_reason := 'revision_conflict';
        selected_target := NULL;
    ELSE
        IF p_operation = 'update' THEN
            UPDATE control.observation_target AS target
            SET
                display_name = p_display_name,
                host_name = p_host_name,
                instance_name = p_instance_name,
                tcp_port = p_tcp_port,
                certificate_host_name = p_certificate_host_name,
                connect_timeout = p_connect_timeout,
                authentication_mode = p_authentication_mode,
                transport_security_mode = p_transport_security_mode,
                lifecycle_state = 'pending_discovery',
                revision = target.revision + 1,
                discovery_requested_at = captured_repository_time,
                updated_at = captured_repository_time
            WHERE target.instance_id = p_instance_id
            RETURNING target.* INTO selected_target;
        ELSIF p_operation = 'retire' THEN
            UPDATE control.observation_target AS target
            SET
                lifecycle_state = 'retired',
                revision = target.revision + 1,
                retired_at = captured_repository_time,
                updated_at = captured_repository_time
            WHERE target.instance_id = p_instance_id
            RETURNING target.* INTO selected_target;
        ELSE
            UPDATE control.observation_target AS target
            SET
                lifecycle_state = 'pending_discovery',
                revision = target.revision + 1,
                discovery_requested_at = captured_repository_time,
                updated_at = captured_repository_time
            WHERE target.instance_id = p_instance_id
            RETURNING target.* INTO selected_target;
        END IF;

        selected_status := 'applied';
        audit_outcome := 'succeeded';
        audit_reason := 'completed';
    END IF;

    PERFORM * FROM audit.append_administrative_activity(
        p_actor_identifier,
        p_correlation_id,
        p_audit_action,
        p_instance_id,
        'granted',
        audit_outcome,
        audit_reason);

    RETURN QUERY SELECT
        selected_status,
        selected_target.instance_id,
        selected_target.instance_key,
        selected_target.display_name,
        selected_target.host_name,
        selected_target.instance_name,
        selected_target.tcp_port,
        selected_target.certificate_host_name,
        selected_target.connect_timeout,
        selected_target.authentication_mode,
        selected_target.transport_security_mode,
        selected_target.lifecycle_state,
        selected_target.revision,
        selected_target.created_at,
        selected_target.discovery_requested_at,
        selected_target.updated_at,
        selected_target.retired_at,
        captured_repository_time;
END
$sqlobserver$;

CREATE FUNCTION control.record_capability_profile(
    p_instance_id uuid,
    p_target_revision bigint,
    p_collector_id text,
    p_collector_manifest_version integer,
    p_output_schema_version integer,
    p_outcome text,
    p_discovery_reason text,
    p_sql_server_major_version integer,
    p_sql_server_minor_version integer,
    p_sql_server_build integer,
    p_sql_server_revision integer,
    p_edition text,
    p_engine_edition integer,
    p_platform text,
    p_authentication_scheme text,
    p_transport_encrypted boolean,
    p_is_sysadmin boolean,
    p_duration_ms bigint,
    p_response_bytes bigint,
    p_checked_at timestamptz,
    p_valid_until timestamptz,
    p_capability_names text[],
    p_capability_availabilities text[],
    p_capability_reasons text[],
    p_permission_names text[],
    p_permission_scopes text[],
    p_permission_states text[],
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint,
    p_actor_identifier text,
    p_correlation_id uuid,
    p_audit_action text
)
RETURNS TABLE(result_status text, recorded_at timestamptz)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_target control.observation_target%ROWTYPE;
    captured_repository_time timestamptz;
    generated_attempt_id uuid := gen_random_uuid();
    generated_profile_id uuid := gen_random_uuid();
    selected_status text;
    audit_reason text;
    capability_count integer;
    permission_count integer;
BEGIN
    capability_count := cardinality(p_capability_names);
    permission_count := cardinality(p_permission_names);

    IF p_audit_action <> 'record_capability_profile'
       OR p_instance_id IS NULL
       OR p_target_revision IS NULL
       OR p_target_revision <= 0
       OR capability_count IS NULL
       OR capability_count > 128
       OR cardinality(p_capability_availabilities) IS DISTINCT FROM capability_count
       OR cardinality(p_capability_reasons) IS DISTINCT FROM capability_count
       OR permission_count IS NULL
       OR permission_count > 128
       OR cardinality(p_permission_scopes) IS DISTINCT FROM permission_count
       OR cardinality(p_permission_states) IS DISTINCT FROM permission_count THEN
        RAISE EXCEPTION 'invalid bounded capability-profile arguments'
            USING ERRCODE = '22023';
    END IF;

    PERFORM control.assert_worker_lease(
        p_work_key,
        p_owner_execution_id,
        p_fencing_token);

    SELECT target.*
    INTO selected_target
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
    FOR UPDATE;
    captured_repository_time := clock_timestamp();

    IF NOT FOUND THEN
        selected_status := 'target_not_found';
        audit_reason := 'target_not_found';
    ELSIF selected_target.host_name IS NULL
          OR selected_target.lifecycle_state IN ('disabled', 'retired') THEN
        selected_status := 'target_inactive';
        audit_reason := CASE
            WHEN selected_target.lifecycle_state = 'retired' THEN 'target_retired'
            ELSE 'discovery_failed'
        END;
    ELSIF selected_target.revision <> p_target_revision THEN
        selected_status := 'revision_conflict';
        audit_reason := 'revision_conflict';
    ELSE
        INSERT INTO control.capability_discovery_attempt
        (
            attempt_id,
            instance_id,
            target_revision,
            collector_id,
            collector_manifest_version,
            output_schema_version,
            outcome,
            discovery_reason,
            sql_server_major_version,
            sql_server_minor_version,
            sql_server_build,
            sql_server_revision,
            edition,
            engine_edition,
            platform,
            authentication_scheme,
            transport_encrypted,
            is_sysadmin,
            duration_ms,
            response_bytes,
            checked_at,
            valid_until,
            recorded_at
        )
        VALUES
        (
            generated_attempt_id,
            p_instance_id,
            p_target_revision,
            p_collector_id,
            p_collector_manifest_version,
            p_output_schema_version,
            p_outcome,
            p_discovery_reason,
            p_sql_server_major_version,
            p_sql_server_minor_version,
            p_sql_server_build,
            p_sql_server_revision,
            p_edition,
            p_engine_edition,
            p_platform,
            p_authentication_scheme,
            p_transport_encrypted,
            p_is_sysadmin,
            p_duration_ms,
            p_response_bytes,
            p_checked_at,
            p_valid_until,
            captured_repository_time
        );

        INSERT INTO control.capability_profile
        (
            profile_id,
            attempt_id,
            instance_id,
            target_revision,
            recorded_at
        )
        VALUES
        (
            generated_profile_id,
            generated_attempt_id,
            p_instance_id,
            p_target_revision,
            captured_repository_time
        );

        INSERT INTO control.capability_profile_reason
        (
            profile_id,
            capability_name,
            availability,
            reason_code
        )
        SELECT
            generated_profile_id,
            capability.capability_name,
            capability.availability,
            capability.reason_code
        FROM unnest(
            p_capability_names,
            p_capability_availabilities,
            p_capability_reasons
        ) AS capability(capability_name, availability, reason_code);

        INSERT INTO control.capability_profile_permission
        (
            profile_id,
            permission_name,
            permission_scope,
            permission_state
        )
        SELECT
            generated_profile_id,
            permission.permission_name,
            permission.permission_scope,
            permission.permission_state
        FROM unnest(
            p_permission_names,
            p_permission_scopes,
            p_permission_states
        ) AS permission(permission_name, permission_scope, permission_state);

        UPDATE control.observation_target AS target
        SET
            lifecycle_state = CASE
                WHEN p_outcome IN ('supported', 'degraded') THEN 'active'
                ELSE 'pending_discovery'
            END,
            updated_at = captured_repository_time
        WHERE target.instance_id = p_instance_id;

        selected_status := 'recorded';
        audit_reason := 'completed';
    END IF;

    PERFORM * FROM audit.append_administrative_activity(
        p_actor_identifier,
        p_correlation_id,
        p_audit_action,
        p_instance_id,
        'granted',
        CASE WHEN selected_status = 'recorded' THEN 'succeeded' ELSE 'conflict' END,
        audit_reason);

    PERFORM control.assert_worker_lease(
        p_work_key,
        p_owner_execution_id,
        p_fencing_token);

    RETURN QUERY SELECT
        selected_status,
        CASE WHEN selected_status = 'recorded' THEN captured_repository_time ELSE NULL END;
END
$sqlobserver$;

CREATE FUNCTION control.update_observation_target(
    p_instance_id uuid,
    p_expected_revision bigint,
    p_display_name text,
    p_host_name text,
    p_instance_name text,
    p_tcp_port integer,
    p_certificate_host_name text,
    p_connect_timeout interval,
    p_authentication_mode text,
    p_transport_security_mode text,
    p_actor_identifier text,
    p_correlation_id uuid,
    p_audit_action text
)
RETURNS TABLE
(
    result_status text,
    target_instance_id uuid,
    target_instance_key text,
    target_display_name text,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    target_lifecycle_state text,
    target_revision bigint,
    target_created_at timestamptz,
    target_discovery_requested_at timestamptz,
    target_updated_at timestamptz,
    target_retired_at timestamptz,
    repository_time timestamptz
)
LANGUAGE sql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    SELECT *
    FROM control.mutate_observation_target(
        'update',
        p_instance_id,
        p_expected_revision,
        p_display_name,
        p_host_name,
        p_instance_name,
        p_tcp_port,
        p_certificate_host_name,
        p_connect_timeout,
        p_authentication_mode,
        p_transport_security_mode,
        p_actor_identifier,
        p_correlation_id,
        p_audit_action);
$sqlobserver$;

CREATE FUNCTION control.retire_observation_target(
    p_instance_id uuid,
    p_expected_revision bigint,
    p_actor_identifier text,
    p_correlation_id uuid,
    p_audit_action text
)
RETURNS TABLE
(
    result_status text,
    target_instance_id uuid,
    target_instance_key text,
    target_display_name text,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    target_lifecycle_state text,
    target_revision bigint,
    target_created_at timestamptz,
    target_discovery_requested_at timestamptz,
    target_updated_at timestamptz,
    target_retired_at timestamptz,
    repository_time timestamptz
)
LANGUAGE sql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    SELECT *
    FROM control.mutate_observation_target(
        'retire',
        p_instance_id,
        p_expected_revision,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        p_actor_identifier,
        p_correlation_id,
        p_audit_action);
$sqlobserver$;

CREATE FUNCTION control.request_capability_rediscovery(
    p_instance_id uuid,
    p_expected_revision bigint,
    p_actor_identifier text,
    p_correlation_id uuid,
    p_audit_action text
)
RETURNS TABLE
(
    result_status text,
    target_instance_id uuid,
    target_instance_key text,
    target_display_name text,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    target_lifecycle_state text,
    target_revision bigint,
    target_created_at timestamptz,
    target_discovery_requested_at timestamptz,
    target_updated_at timestamptz,
    target_retired_at timestamptz,
    repository_time timestamptz
)
LANGUAGE sql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
    SELECT *
    FROM control.mutate_observation_target(
        'rediscovery',
        p_instance_id,
        p_expected_revision,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        p_actor_identifier,
        p_correlation_id,
        p_audit_action);
$sqlobserver$;

CREATE TRIGGER capability_attempt_append_only
BEFORE UPDATE OR DELETE ON control.capability_discovery_attempt
FOR EACH ROW EXECUTE FUNCTION control.reject_capability_history_mutation();

CREATE TRIGGER capability_profile_append_only
BEFORE UPDATE OR DELETE ON control.capability_profile
FOR EACH ROW EXECUTE FUNCTION control.reject_capability_history_mutation();

CREATE TRIGGER capability_permission_append_only
BEFORE UPDATE OR DELETE ON control.capability_profile_permission
FOR EACH ROW EXECUTE FUNCTION control.reject_capability_history_mutation();

CREATE TRIGGER capability_reason_append_only
BEFORE UPDATE OR DELETE ON control.capability_profile_reason
FOR EACH ROW EXECUTE FUNCTION control.reject_capability_history_mutation();

CREATE VIEW reporting.observation_target_status
WITH (security_barrier = true)
AS
SELECT
    target.instance_id,
    target.instance_key,
    target.display_name,
    target.host_name,
    target.instance_name,
    target.tcp_port,
    target.certificate_host_name,
    target.connect_timeout,
    target.authentication_mode,
    target.transport_security_mode,
    target.lifecycle_state,
    target.revision,
    target.created_at,
    target.updated_at,
    target.retired_at,
    target.discovery_requested_at,
    target.host_name IS NOT NULL AS endpoint_configured,
    latest_attempt.attempt_id AS latest_attempt_id,
    latest_attempt.outcome AS latest_outcome,
    latest_attempt.discovery_reason AS latest_reason,
    latest_attempt.collector_id,
    latest_attempt.collector_manifest_version,
    latest_attempt.output_schema_version,
    latest_attempt.sql_server_major_version,
    latest_attempt.sql_server_minor_version,
    latest_attempt.sql_server_build,
    latest_attempt.sql_server_revision,
    latest_attempt.edition,
    latest_attempt.engine_edition,
    latest_attempt.platform,
    latest_attempt.authentication_scheme,
    latest_attempt.transport_encrypted,
    latest_attempt.is_sysadmin,
    latest_attempt.duration_ms,
    latest_attempt.response_bytes,
    latest_attempt.checked_at,
    latest_attempt.valid_until,
    latest_attempt.recorded_at AS latest_recorded_at,
    clock_timestamp() AS repository_time
FROM control.observation_target AS target
LEFT JOIN LATERAL
(
    SELECT attempt.*
    FROM control.capability_discovery_attempt AS attempt
    WHERE attempt.instance_id = target.instance_id
    ORDER BY attempt.recorded_at DESC, attempt.attempt_id DESC
    LIMIT 1
) AS latest_attempt ON true;

COMMENT ON VIEW reporting.observation_target_status IS
    'Sanitized structured target and latest capability status. It excludes credentials, connection strings, target error text, and arbitrary diagnostic payloads.';

REVOKE ALL ON TABLE
    control.capability_discovery_attempt,
    control.capability_profile,
    control.capability_profile_permission,
    control.capability_profile_reason,
    reporting.observation_target_status
    FROM PUBLIC;
REVOKE ALL ON FUNCTION control.reject_capability_history_mutation() FROM PUBLIC;

CREATE FUNCTION control.validate_observation_target_configuration(
    p_display_name text,
    p_host_name text,
    p_instance_name text,
    p_tcp_port integer,
    p_certificate_host_name text,
    p_connect_timeout interval,
    p_authentication_mode text,
    p_transport_security_mode text
)
RETURNS void
LANGUAGE plpgsql
SECURITY INVOKER
IMMUTABLE
PARALLEL SAFE
SET search_path = pg_catalog
AS $sqlobserver$
BEGIN
    IF p_display_name IS NULL
       OR p_display_name !~ '[^[:space:]]'
       OR octet_length(p_display_name) > 512
       OR p_display_name ~ '[[:cntrl:]]'
       OR p_host_name IS NULL
       OR octet_length(p_host_name) NOT BETWEEN 1 AND 255
       OR p_host_name !~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'
       OR ((p_instance_name IS NULL) = (p_tcp_port IS NULL))
       OR (p_instance_name IS NOT NULL AND
           (octet_length(p_instance_name) NOT BETWEEN 1 AND 128
            OR p_instance_name !~ '^[A-Za-z0-9][A-Za-z0-9_$-]*$'))
       OR (p_tcp_port IS NOT NULL AND p_tcp_port NOT BETWEEN 1 AND 65535)
       OR (p_certificate_host_name IS NOT NULL AND
           (octet_length(p_certificate_host_name) NOT BETWEEN 1 AND 255
            OR p_certificate_host_name !~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'))
       OR p_connect_timeout IS NULL
       OR p_connect_timeout NOT BETWEEN interval '1 second' AND interval '30 seconds'
       OR p_authentication_mode <> 'windows_integrated_service_identity'
       OR p_transport_security_mode <> 'mandatory_validated' THEN
        RAISE EXCEPTION 'invalid structured observation-target configuration'
            USING ERRCODE = '22023';
    END IF;
END
$sqlobserver$;

CREATE FUNCTION audit.append_administrative_activity(
    p_actor_identifier text,
    p_correlation_id uuid,
    p_action text,
    p_target_id uuid,
    p_authorization_decision text,
    p_operation_outcome text,
    p_reason text
)
RETURNS TABLE(audit_activity_id uuid, repository_time timestamptz)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    captured_repository_time timestamptz := clock_timestamp();
    generated_activity_id uuid := gen_random_uuid();
    persisted_action_name text;
BEGIN
    IF p_actor_identifier IS NULL
       OR octet_length(p_actor_identifier) NOT BETWEEN 1 AND 184
       OR p_actor_identifier !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
       OR p_correlation_id IS NULL
       OR p_correlation_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_target_id IS NULL
       OR p_target_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_action NOT IN
          (
              'register_observation_target',
              'update_observation_target',
              'retire_observation_target',
              'request_capability_rediscovery',
              'record_capability_profile'
          )
       OR p_authorization_decision NOT IN ('granted', 'denied')
       OR p_operation_outcome NOT IN ('succeeded', 'denied', 'failed', 'conflict')
       OR ((p_authorization_decision = 'denied') <> (p_operation_outcome = 'denied'))
       OR p_reason NOT IN
          (
              'completed',
              'principal_disabled',
              'required_role_missing',
              'target_out_of_scope',
              'already_exists',
              'revision_conflict',
              'target_not_found',
              'target_retired',
              'discovery_failed',
              'repository_failure'
          ) THEN
        RAISE EXCEPTION 'invalid bounded administrative audit fields'
            USING ERRCODE = '22023';
    END IF;

    persisted_action_name := CASE p_action
        WHEN 'register_observation_target' THEN 'observation_target.register'
        WHEN 'update_observation_target' THEN 'observation_target.update'
        WHEN 'retire_observation_target' THEN 'observation_target.retire'
        WHEN 'request_capability_rediscovery' THEN 'capability.rediscovery_request'
        WHEN 'record_capability_profile' THEN 'capability.profile_record'
    END;

    INSERT INTO audit.activity
    (
        occurred_at,
        activity_id,
        actor_kind,
        actor_identifier,
        action_name,
        authorization_result,
        outcome,
        subject_kind,
        subject_identifier,
        correlation_id,
        safe_details
    )
    VALUES
    (
        captured_repository_time,
        generated_activity_id,
        CASE p_action WHEN 'record_capability_profile' THEN 'service' ELSE 'user' END,
        p_actor_identifier,
        persisted_action_name,
        CASE p_authorization_decision WHEN 'granted' THEN 'allowed' ELSE 'denied' END,
        CASE p_operation_outcome
            WHEN 'succeeded' THEN 'succeeded'
            WHEN 'failed' THEN 'failed'
            ELSE 'rejected'
        END,
        'observation_target',
        p_target_id::text,
        p_correlation_id,
        jsonb_build_object('reason', p_reason)
    );

    RETURN QUERY SELECT generated_activity_id, captured_repository_time;
END
$sqlobserver$;

CREATE FUNCTION audit.append_denied_administrative_activity(
    p_actor_identifier text,
    p_correlation_id uuid,
    p_action text,
    p_target_id uuid,
    p_reason text
)
RETURNS TABLE(audit_activity_id uuid, repository_time timestamptz)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
BEGIN
    IF p_action NOT IN
       (
           'register_observation_target',
           'update_observation_target',
           'retire_observation_target',
           'request_capability_rediscovery'
       )
       OR p_reason NOT IN
       (
           'principal_disabled',
           'required_role_missing',
           'target_out_of_scope'
       ) THEN
        RAISE EXCEPTION 'only bounded user-administration denials may use this audit entry point'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    SELECT result.audit_activity_id, result.repository_time
    FROM audit.append_administrative_activity(
        p_actor_identifier,
        p_correlation_id,
        p_action,
        p_target_id,
        'denied',
        'denied',
        p_reason) AS result;
END
$sqlobserver$;

CREATE FUNCTION control.register_observation_target(
    p_instance_id uuid,
    p_instance_key text,
    p_display_name text,
    p_host_name text,
    p_instance_name text,
    p_tcp_port integer,
    p_certificate_host_name text,
    p_connect_timeout interval,
    p_authentication_mode text,
    p_transport_security_mode text,
    p_actor_identifier text,
    p_correlation_id uuid,
    p_audit_action text
)
RETURNS TABLE
(
    result_status text,
    target_instance_id uuid,
    target_instance_key text,
    target_display_name text,
    target_host_name text,
    target_instance_name text,
    target_tcp_port integer,
    target_certificate_host_name text,
    target_connect_timeout interval,
    target_authentication_mode text,
    target_transport_security_mode text,
    target_lifecycle_state text,
    target_revision bigint,
    target_created_at timestamptz,
    target_discovery_requested_at timestamptz,
    target_updated_at timestamptz,
    target_retired_at timestamptz,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    selected_target control.observation_target%ROWTYPE;
    captured_repository_time timestamptz;
    selected_status text;
    audit_outcome text;
    audit_reason text;
BEGIN
    IF p_audit_action <> 'register_observation_target'
       OR p_instance_id IS NULL
       OR p_instance_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_instance_key IS NULL
       OR octet_length(p_instance_key) NOT BETWEEN 1 AND 256
       OR p_instance_key !~ '^[a-z0-9][a-z0-9._:-]*$' THEN
        RAISE EXCEPTION 'invalid observation-target registration identity'
            USING ERRCODE = '22023';
    END IF;

    PERFORM control.validate_observation_target_configuration(
        p_display_name,
        p_host_name,
        p_instance_name,
        p_tcp_port,
        p_certificate_host_name,
        p_connect_timeout,
        p_authentication_mode,
        p_transport_security_mode);

    PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:target-registration', 0));
    captured_repository_time := clock_timestamp();

    SELECT target.*
    INTO selected_target
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
    FOR UPDATE;

    IF FOUND THEN
        IF selected_target.instance_key = p_instance_key
           AND selected_target.display_name = p_display_name
           AND selected_target.host_name = p_host_name
           AND selected_target.instance_name IS NOT DISTINCT FROM p_instance_name
           AND selected_target.tcp_port IS NOT DISTINCT FROM p_tcp_port
           AND selected_target.certificate_host_name IS NOT DISTINCT FROM p_certificate_host_name
           AND selected_target.connect_timeout = p_connect_timeout
           AND selected_target.authentication_mode = p_authentication_mode
           AND selected_target.transport_security_mode = p_transport_security_mode THEN
            selected_status := 'already_exists';
            audit_outcome := 'succeeded';
        ELSE
            selected_status := 'target_id_conflict';
            audit_outcome := 'conflict';
            selected_target := NULL;
        END IF;
        audit_reason := 'already_exists';
    ELSE
        SELECT target.*
        INTO selected_target
        FROM control.observation_target AS target
        WHERE target.instance_key = p_instance_key
        FOR UPDATE;

        IF FOUND THEN
            selected_status := 'target_key_conflict';
            audit_outcome := 'conflict';
            audit_reason := 'already_exists';
            selected_target := NULL;
        ELSE
            INSERT INTO control.observation_target
            (
                instance_id,
                instance_key,
                display_name,
                host_name,
                instance_name,
                tcp_port,
                certificate_host_name,
                connect_timeout,
                authentication_mode,
                transport_security_mode,
                lifecycle_state,
                revision,
                created_at,
                discovery_requested_at,
                updated_at
            )
            VALUES
            (
                p_instance_id,
                p_instance_key,
                p_display_name,
                p_host_name,
                p_instance_name,
                p_tcp_port,
                p_certificate_host_name,
                p_connect_timeout,
                p_authentication_mode,
                p_transport_security_mode,
                'pending_discovery',
                1,
                captured_repository_time,
                captured_repository_time,
                captured_repository_time
            )
            RETURNING * INTO selected_target;

            selected_status := 'registered';
            audit_outcome := 'succeeded';
            audit_reason := 'completed';
        END IF;
    END IF;

    PERFORM * FROM audit.append_administrative_activity(
        p_actor_identifier,
        p_correlation_id,
        p_audit_action,
        p_instance_id,
        'granted',
        audit_outcome,
        audit_reason);

    RETURN QUERY SELECT
        selected_status,
        selected_target.instance_id,
        selected_target.instance_key,
        selected_target.display_name,
        selected_target.host_name,
        selected_target.instance_name,
        selected_target.tcp_port,
        selected_target.certificate_host_name,
        selected_target.connect_timeout,
        selected_target.authentication_mode,
        selected_target.transport_security_mode,
        selected_target.lifecycle_state,
        selected_target.revision,
        selected_target.created_at,
        selected_target.discovery_requested_at,
        selected_target.updated_at,
        selected_target.retired_at,
        captured_repository_time;
END
$sqlobserver$;

COMMENT ON FUNCTION audit.append_administrative_activity(text, uuid, text, uuid, text, text, text) IS
    'Internal owner-only bounded audit primitive used by atomic repository functions.';
COMMENT ON FUNCTION audit.append_denied_administrative_activity(text, uuid, text, uuid, text) IS
    'Server-only denial audit wrapper for bounded user-administration actions and denial reasons.';
COMMENT ON FUNCTION control.register_observation_target(uuid, text, text, text, text, integer, text, interval, text, text, text, uuid, text) IS
    'Atomically registers an exact structured target identity and appends its administrative audit.';
COMMENT ON FUNCTION control.update_observation_target(uuid, bigint, text, text, text, integer, text, interval, text, text, text, uuid, text) IS
    'Revision-checks a structured target update, requests rediscovery, and appends audit atomically.';
COMMENT ON FUNCTION control.retire_observation_target(uuid, bigint, text, uuid, text) IS
    'Revision-checks target retirement and appends audit atomically.';
COMMENT ON FUNCTION control.request_capability_rediscovery(uuid, bigint, text, uuid, text) IS
    'Revision-checks a repository-clock rediscovery request and appends audit atomically.';
COMMENT ON FUNCTION control.list_due_capability_targets(integer) IS
    'Returns at most sixteen configured, non-retired targets whose repository-anchored refresh interval is due.';
COMMENT ON FUNCTION control.record_capability_profile(
    uuid, bigint, text, integer, integer, text, text, integer, integer, integer,
    integer, text, integer, text, text, boolean, boolean, bigint, bigint,
    timestamptz, timestamptz, text[], text[], text[], text[], text[], text[],
    text, uuid, bigint, text, uuid, text) IS
    'Lease- and revision-fenced append-only capability recording with atomic administrative audit.';

REVOKE SELECT ON TABLE control.observation_target
    FROM sqlobserver_server, sqlobserver_collector;
GRANT SELECT (instance_id, instance_key, display_name, created_at, retired_at)
    ON TABLE control.observation_target
    TO sqlobserver_server, sqlobserver_collector;

REVOKE ALL ON TABLE reporting.observation_target_status FROM PUBLIC;
GRANT SELECT ON TABLE reporting.observation_target_status
    TO sqlobserver_server;

REVOKE ALL ON FUNCTION
    control.validate_observation_target_configuration(text, text, text, integer, text, interval, text, text),
    control.mutate_observation_target(text, uuid, bigint, text, text, text, integer, text, interval, text, text, text, uuid, text),
    control.reject_capability_history_mutation(),
    control.register_observation_target(uuid, text, text, text, text, integer, text, interval, text, text, text, uuid, text),
    control.update_observation_target(uuid, bigint, text, text, text, integer, text, interval, text, text, text, uuid, text),
    control.retire_observation_target(uuid, bigint, text, uuid, text),
    control.request_capability_rediscovery(uuid, bigint, text, uuid, text),
    control.list_due_capability_targets(integer),
    control.record_capability_profile(
        uuid, bigint, text, integer, integer, text, text, integer, integer, integer,
        integer, text, integer, text, text, boolean, boolean, bigint, bigint,
        timestamptz, timestamptz, text[], text[], text[], text[], text[], text[],
        text, uuid, bigint, text, uuid, text),
    control.get_latest_capability_profile(uuid),
    control.get_latest_capability_profiles(uuid[]),
    control.get_capability_profile_reasons(uuid),
    control.get_capability_profile_permissions(uuid),
    audit.append_administrative_activity(text, uuid, text, uuid, text, text, text),
    audit.append_denied_administrative_activity(text, uuid, text, uuid, text)
    FROM PUBLIC;

REVOKE EXECUTE ON FUNCTION
    audit.append_administrative_activity(text, uuid, text, uuid, text, text, text)
    FROM sqlobserver_server, sqlobserver_collector;

GRANT EXECUTE ON FUNCTION
    control.register_observation_target(uuid, text, text, text, text, integer, text, interval, text, text, text, uuid, text),
    control.update_observation_target(uuid, bigint, text, text, text, integer, text, interval, text, text, text, uuid, text),
    control.retire_observation_target(uuid, bigint, text, uuid, text),
    control.request_capability_rediscovery(uuid, bigint, text, uuid, text)
    TO sqlobserver_server;

GRANT EXECUTE ON FUNCTION
    control.list_due_capability_targets(integer),
    control.record_capability_profile(
        uuid, bigint, text, integer, integer, text, text, integer, integer, integer,
        integer, text, integer, text, text, boolean, boolean, bigint, bigint,
        timestamptz, timestamptz, text[], text[], text[], text[], text[], text[],
        text, uuid, bigint, text, uuid, text)
    TO sqlobserver_collector;

GRANT EXECUTE ON FUNCTION
    control.get_latest_capability_profile(uuid),
    control.get_latest_capability_profiles(uuid[]),
    control.get_capability_profile_reasons(uuid),
    control.get_capability_profile_permissions(uuid)
    TO sqlobserver_server, sqlobserver_collector;

GRANT EXECUTE ON FUNCTION
    audit.append_denied_administrative_activity(text, uuid, text, uuid, text)
    TO sqlobserver_server;
