-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE TABLE control.observation_target
(
    instance_id uuid PRIMARY KEY,
    instance_key text NOT NULL UNIQUE,
    display_name text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    retired_at timestamptz,
    CONSTRAINT ck_observation_target_instance_key CHECK (
        octet_length(instance_key) BETWEEN 1 AND 256
        AND instance_key ~ '^[a-z0-9][a-z0-9._:-]*$'
    ),
    CONSTRAINT ck_observation_target_display_name CHECK (
        octet_length(display_name) BETWEEN 1 AND 512
    ),
    CONSTRAINT ck_observation_target_retired_after_created CHECK (
        retired_at IS NULL OR retired_at >= created_at
    )
);

COMMENT ON TABLE control.observation_target IS
    'Minimal stable identity for a monitored SQL Server instance; connection and credential metadata belong to M3.';
COMMENT ON COLUMN control.observation_target.instance_key IS
    'Application-normalized stable identifier. It is not an endpoint or connection string.';
COMMENT ON COLUMN control.observation_target.display_name IS
    'Untrusted operator-facing text; consumers must output-encode it and keep it out of metric labels.';

CREATE TABLE control.worker_lease
(
    work_key text PRIMARY KEY,
    owner_execution_id uuid NOT NULL,
    fencing_token bigint NOT NULL,
    acquired_at timestamptz NOT NULL,
    renewed_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    released_at timestamptz,
    CONSTRAINT ck_worker_lease_work_key CHECK (
        octet_length(work_key) BETWEEN 1 AND 256
        AND work_key = btrim(work_key)
    ),
    CONSTRAINT ck_worker_lease_fencing_token CHECK (fencing_token > 0),
    CONSTRAINT ck_worker_lease_times CHECK (
        renewed_at >= acquired_at AND expires_at > renewed_at
    ),
    CONSTRAINT ck_worker_lease_release_time CHECK (
        released_at IS NULL OR released_at >= acquired_at
    )
);

COMMENT ON TABLE control.worker_lease IS
    'Repository-clock leases. Rows persist across release so fencing tokens never reset for a work key.';
COMMENT ON COLUMN control.worker_lease.released_at IS
    'Explicit repository-clock release marker; NULL means ownership may still be active until expires_at.';

CREATE INDEX ix_worker_lease_expires_at
    ON control.worker_lease (expires_at);

CREATE TABLE security.protected_diagnostic_payload
(
    payload_id uuid PRIMARY KEY,
    payload_kind text NOT NULL,
    fingerprint bytea NOT NULL,
    protection_algorithm text NOT NULL,
    key_identifier text NOT NULL,
    nonce bytea NOT NULL,
    authentication_tag bytea NOT NULL,
    ciphertext bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_protected_payload_dedup UNIQUE (
        payload_kind,
        fingerprint
    ),
    CONSTRAINT ck_protected_payload_kind CHECK (
        payload_kind IN ('query_text', 'execution_plan')
    ),
    CONSTRAINT ck_protected_payload_fingerprint CHECK (
        octet_length(fingerprint) = 32
    ),
    CONSTRAINT ck_protected_payload_algorithm CHECK (
        octet_length(protection_algorithm) BETWEEN 1 AND 64
        AND protection_algorithm ~ '^[A-Za-z0-9._+-]+$'
    ),
    CONSTRAINT ck_protected_payload_key_identifier CHECK (
        octet_length(key_identifier) BETWEEN 1 AND 128
    ),
    CONSTRAINT ck_protected_payload_nonce CHECK (
        octet_length(nonce) BETWEEN 8 AND 32
    ),
    CONSTRAINT ck_protected_payload_authentication_tag CHECK (
        octet_length(authentication_tag) BETWEEN 12 AND 32
    ),
    CONSTRAINT ck_protected_payload_ciphertext CHECK (
        octet_length(ciphertext) BETWEEN 1 AND 1048576
    )
);

COMMENT ON TABLE security.protected_diagnostic_payload IS
    'Deduplicated encrypted query text and plans. The repository stores ciphertext and opaque 32-byte fingerprints only.';
COMMENT ON COLUMN security.protected_diagnostic_payload.fingerprint IS
    'Opaque application-produced 32-byte fingerprint; this column must never contain query text or a reversible value.';
COMMENT ON COLUMN security.protected_diagnostic_payload.key_identifier IS
    'External protected-key reference only; encryption key material is never stored in this repository.';

CREATE TABLE telemetry.raw_metric_sample
(
    observed_at timestamptz NOT NULL,
    sample_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    metric_key text NOT NULL,
    metric_value double precision NOT NULL,
    dimensions jsonb NOT NULL DEFAULT '{}'::jsonb,
    collected_at timestamptz NOT NULL,
    CONSTRAINT pk_raw_metric_sample PRIMARY KEY (observed_at, sample_id),
    CONSTRAINT fk_raw_metric_sample_instance FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT ck_raw_metric_sample_metric_key CHECK (
        octet_length(metric_key) BETWEEN 1 AND 128
        AND metric_key ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_raw_metric_sample_value_finite CHECK (
        metric_value NOT IN (
            'NaN'::double precision,
            'Infinity'::double precision,
            '-Infinity'::double precision
        )
    ),
    CONSTRAINT ck_raw_metric_sample_dimensions CHECK (
        jsonb_typeof(dimensions) = 'object'
        AND octet_length(dimensions::text) <= 16384
    )
) PARTITION BY RANGE (observed_at);

COMMENT ON TABLE telemetry.raw_metric_sample IS
    'High-volume bounded binary-COPY target, partitioned by UTC observed day with no default partition.';
COMMENT ON COLUMN telemetry.raw_metric_sample.dimensions IS
    'Bounded allowlisted dimensions only; unrestricted query text, plans, errors, or secrets are prohibited.';

CREATE INDEX ix_raw_metric_sample_observed_at_brin
    ON telemetry.raw_metric_sample USING brin (observed_at)
    WITH (pages_per_range = 128, autosummarize = on);

CREATE INDEX ix_raw_metric_sample_instance_time
    ON telemetry.raw_metric_sample (instance_id, observed_at DESC);

CREATE TABLE events.diagnostic_event
(
    occurred_at timestamptz NOT NULL,
    event_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    event_kind text NOT NULL,
    severity smallint NOT NULL DEFAULT 0,
    protected_payload_id uuid,
    safe_metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    collected_at timestamptz NOT NULL,
    CONSTRAINT pk_diagnostic_event PRIMARY KEY (occurred_at, event_id),
    CONSTRAINT fk_diagnostic_event_instance FOREIGN KEY (instance_id)
        REFERENCES control.observation_target (instance_id),
    CONSTRAINT fk_diagnostic_event_payload FOREIGN KEY (protected_payload_id)
        REFERENCES security.protected_diagnostic_payload (payload_id),
    CONSTRAINT ck_diagnostic_event_kind CHECK (
        octet_length(event_kind) BETWEEN 1 AND 128
        AND event_kind ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_diagnostic_event_severity CHECK (severity BETWEEN 0 AND 100),
    CONSTRAINT ck_diagnostic_event_safe_metadata CHECK (
        jsonb_typeof(safe_metadata) = 'object'
        AND octet_length(safe_metadata::text) <= 65536
    )
) PARTITION BY RANGE (occurred_at);

COMMENT ON TABLE events.diagnostic_event IS
    'Lower-volume diagnostic event envelope, partitioned by UTC month with no default partition.';
COMMENT ON COLUMN events.diagnostic_event.safe_metadata IS
    'Bounded allowlisted metadata only; sensitive or unrestricted diagnostic content belongs in protected ciphertext storage.';

CREATE INDEX ix_diagnostic_event_occurred_at_brin
    ON events.diagnostic_event USING brin (occurred_at)
    WITH (pages_per_range = 32, autosummarize = on);

CREATE INDEX ix_diagnostic_event_instance_time
    ON events.diagnostic_event (instance_id, occurred_at DESC);

CREATE TABLE audit.activity
(
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    activity_id uuid NOT NULL,
    actor_kind text NOT NULL,
    actor_identifier text NOT NULL,
    action_name text NOT NULL,
    authorization_result text NOT NULL,
    outcome text NOT NULL,
    subject_kind text,
    subject_identifier text,
    correlation_id uuid NOT NULL,
    parameter_digest bytea,
    duration_ms bigint,
    response_bytes bigint,
    safe_details jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT pk_audit_activity PRIMARY KEY (occurred_at, activity_id),
    CONSTRAINT ck_audit_actor_kind CHECK (
        actor_kind IN ('user', 'service', 'mcp_client', 'system')
    ),
    CONSTRAINT ck_audit_actor_identifier CHECK (
        octet_length(actor_identifier) BETWEEN 1 AND 512
    ),
    CONSTRAINT ck_audit_action_name CHECK (
        octet_length(action_name) BETWEEN 1 AND 128
        AND action_name ~ '^[a-z][a-z0-9._-]*$'
    ),
    CONSTRAINT ck_audit_authorization_result CHECK (
        authorization_result IN ('allowed', 'denied', 'not_applicable')
    ),
    CONSTRAINT ck_audit_outcome CHECK (
        outcome IN ('succeeded', 'failed', 'cancelled', 'rejected')
    ),
    CONSTRAINT ck_audit_subject_pair CHECK (
        (subject_kind IS NULL) = (subject_identifier IS NULL)
    ),
    CONSTRAINT ck_audit_subject_kind CHECK (
        subject_kind IS NULL OR octet_length(subject_kind) BETWEEN 1 AND 128
    ),
    CONSTRAINT ck_audit_subject_identifier CHECK (
        subject_identifier IS NULL OR octet_length(subject_identifier) BETWEEN 1 AND 512
    ),
    CONSTRAINT ck_audit_parameter_digest CHECK (
        parameter_digest IS NULL OR octet_length(parameter_digest) = 32
    ),
    CONSTRAINT ck_audit_duration CHECK (duration_ms IS NULL OR duration_ms >= 0),
    CONSTRAINT ck_audit_response_size CHECK (response_bytes IS NULL OR response_bytes >= 0),
    CONSTRAINT ck_audit_safe_details CHECK (
        jsonb_typeof(safe_details) = 'object'
        AND octet_length(safe_details::text) <= 16384
    )
);

COMMENT ON TABLE audit.activity IS
    'Append-only safe audit envelope for administrative writes and MCP calls, including denied attempts where safe.';
COMMENT ON COLUMN audit.activity.safe_details IS
    'Allowlisted non-secret metadata only; response content, query text, plans, tokens, and connection strings are prohibited.';

CREATE INDEX ix_audit_activity_time
    ON audit.activity (occurred_at DESC);

CREATE INDEX ix_audit_activity_actor_time
    ON audit.activity (actor_kind, actor_identifier, occurred_at DESC);

REVOKE ALL ON ALL TABLES IN SCHEMA control, security, telemetry, events, audit FROM PUBLIC;
