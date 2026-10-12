-- Stage AG v2 evidence with an explicit observing replica and database/replica key.
-- No writer, schedule, partitions, or retention route is activated here.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.availability_replica_evidence
(
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    observed_at timestamptz NOT NULL CHECK (isfinite(observed_at)),
    collected_at timestamptz NOT NULL DEFAULT clock_timestamp() CHECK (isfinite(collected_at)),
    group_fingerprint bytea NOT NULL CHECK (octet_length(group_fingerprint)=32),
    replica_fingerprint bytea NOT NULL CHECK (octet_length(replica_fingerprint)=32),
    observer_replica_fingerprint bytea,
    replica_is_local boolean,
    visibility_scope smallint NOT NULL CHECK (visibility_scope BETWEEN 1 AND 3),
    state_observed boolean NOT NULL,
    role text CHECK (role IN ('PRIMARY','SECONDARY','RESOLVING')),
    operational_state text CHECK (operational_state IN
        ('PENDING_FAILOVER','PENDING','ONLINE','OFFLINE','FAILED','FAILED_NO_QUORUM')),
    connected_state text CHECK (connected_state IN ('CONNECTED','DISCONNECTED')),
    CONSTRAINT pk_availability_replica_evidence PRIMARY KEY
        (instance_id,observed_at,run_id,group_fingerprint,replica_fingerprint),
    CONSTRAINT ck_availability_replica_evidence_observer CHECK
        ((observer_replica_fingerprint IS NULL AND replica_is_local IS NULL AND visibility_scope=3)
         OR (observer_replica_fingerprint IS NOT NULL
             AND octet_length(observer_replica_fingerprint)=32
             AND replica_is_local IS NOT NULL
             AND replica_is_local=(observer_replica_fingerprint=replica_fingerprint))),
    CONSTRAINT ck_availability_replica_evidence_unobserved CHECK
        (state_observed OR (role IS NULL AND operational_state IS NULL AND connected_state IS NULL))
) PARTITION BY RANGE (observed_at);

CREATE INDEX ix_availability_replica_evidence_target_time
    ON telemetry.availability_replica_evidence
        (instance_id,observed_at DESC,run_id,group_fingerprint,replica_fingerprint);
COMMENT ON TABLE telemetry.availability_replica_evidence IS
    'Staged AG v2 configured-replica evidence. Remote operational state can be NULL when observed from a primary.';

CREATE TABLE telemetry.availability_database_replica_evidence
(
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    observed_at timestamptz NOT NULL CHECK (isfinite(observed_at)),
    collected_at timestamptz NOT NULL DEFAULT clock_timestamp() CHECK (isfinite(collected_at)),
    group_fingerprint bytea NOT NULL CHECK (octet_length(group_fingerprint)=32),
    replica_fingerprint bytea NOT NULL CHECK (octet_length(replica_fingerprint)=32),
    database_fingerprint bytea NOT NULL CHECK (octet_length(database_fingerprint)=32),
    observer_replica_fingerprint bytea,
    replica_is_local boolean,
    visibility_scope smallint NOT NULL CHECK (visibility_scope BETWEEN 1 AND 3),
    state_observed boolean NOT NULL,
    synchronization_state text CHECK (synchronization_state IN
        ('NOT SYNCHRONIZING','SYNCHRONIZING','SYNCHRONIZED','REVERTING','INITIALIZING')),
    synchronization_health text CHECK (synchronization_health IN
        ('NOT_HEALTHY','PARTIALLY_HEALTHY','HEALTHY')),
    database_state text CHECK (database_state IN
        ('ONLINE','RESTORING','RECOVERING','RECOVERY_PENDING','SUSPECT','EMERGENCY','OFFLINE')),
    is_suspended boolean,
    log_send_queue_kb bigint CHECK (log_send_queue_kb >= 0),
    log_send_rate_kb_per_second bigint CHECK (log_send_rate_kb_per_second >= 0),
    redo_queue_kb bigint CHECK (redo_queue_kb >= 0),
    redo_rate_kb_per_second bigint CHECK (redo_rate_kb_per_second >= 0),
    source_last_commit_local timestamp without time zone CHECK
        (isfinite(source_last_commit_local)
         AND source_last_commit_local >= timestamp '1753-01-01'
         AND source_last_commit_local < timestamp '10000-01-01'),
    CONSTRAINT pk_availability_database_replica_evidence PRIMARY KEY
        (instance_id,observed_at,run_id,group_fingerprint,replica_fingerprint,database_fingerprint),
    CONSTRAINT ck_availability_database_replica_evidence_observer CHECK
        ((observer_replica_fingerprint IS NULL AND replica_is_local IS NULL AND visibility_scope=3)
         OR (observer_replica_fingerprint IS NOT NULL
             AND octet_length(observer_replica_fingerprint)=32
             AND replica_is_local IS NOT NULL
             AND replica_is_local=(observer_replica_fingerprint=replica_fingerprint))),
    CONSTRAINT ck_availability_database_replica_evidence_unobserved CHECK
        (state_observed OR
         (synchronization_state IS NULL AND synchronization_health IS NULL
          AND database_state IS NULL AND is_suspended IS NULL
          AND log_send_queue_kb IS NULL AND log_send_rate_kb_per_second IS NULL
          AND redo_queue_kb IS NULL AND redo_rate_kb_per_second IS NULL
          AND source_last_commit_local IS NULL))
) PARTITION BY RANGE (observed_at);

CREATE INDEX ix_availability_database_replica_evidence_target_time
    ON telemetry.availability_database_replica_evidence
        (instance_id,observed_at DESC,run_id,group_fingerprint,replica_fingerprint,database_fingerprint);
COMMENT ON COLUMN telemetry.availability_database_replica_evidence.source_last_commit_local IS
    'Offset-free SQL Server last_commit_time, as last reported to the observing replica. This is not a UTC instant or an exact RPO.';

ALTER TABLE telemetry.availability_replica_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.availability_replica_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY availability_replica_evidence_scope
    ON telemetry.availability_replica_evidence
    USING (instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK (instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY availability_replica_evidence_migrator
    ON telemetry.availability_replica_evidence FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
CREATE TRIGGER availability_replica_evidence_append_only
    BEFORE UPDATE OR DELETE ON telemetry.availability_replica_evidence
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

ALTER TABLE telemetry.availability_database_replica_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.availability_database_replica_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY availability_database_replica_evidence_scope
    ON telemetry.availability_database_replica_evidence
    USING (instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK (instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY availability_database_replica_evidence_migrator
    ON telemetry.availability_database_replica_evidence FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
CREATE TRIGGER availability_database_replica_evidence_append_only
    BEFORE UPDATE OR DELETE ON telemetry.availability_database_replica_evidence
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON telemetry.availability_replica_evidence,
    telemetry.availability_database_replica_evidence
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
