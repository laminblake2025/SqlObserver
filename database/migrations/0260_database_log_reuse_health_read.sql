-- Extend database health reads without changing the historical function's row shape.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_database_health_v2
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_database_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    instance_id uuid,
    observation_target_revision bigint,
    database_id integer,
    database_name text,
    state_code text,
    recovery_model text,
    user_access text,
    is_read_only boolean,
    compatibility_level integer,
    observed_at timestamptz,
    collector_id text,
    collector_version integer,
    output_schema_version integer,
    health_state text,
    health_reason text,
    circuit_state text,
    consecutive_failure_count integer,
    circuit_open_until timestamptz,
    run_id uuid,
    target_revision bigint,
    run_outcome text,
    run_reason text,
    duration_ms bigint,
    attempt_count integer,
    source_row_count integer,
    output_item_count integer,
    response_bytes bigint,
    output_bytes bigint,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    inserted_item_count integer,
    duplicate_item_count integer,
    rejected_item_count integer,
    persisted_bytes bigint,
    scheduled_at timestamptz,
    last_attempt_at timestamptz,
    last_success_at timestamptz,
    next_due_at timestamptz,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    has_more boolean,
    repository_time timestamptz,
    log_reuse_wait_desc text
)
LANGUAGE sql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog SET TimeZone = 'UTC'
AS $health$
    SELECT health.*, snapshot.log_reuse_wait_desc
    FROM reporting.list_database_health(
        p_instance_id, p_snapshot_run_id, p_snapshot_target_revision,
        p_after_database_id, p_max_results) AS health
    LEFT JOIN telemetry.database_inventory_snapshot AS snapshot
      ON snapshot.instance_id = health.instance_id
     AND snapshot.collection_run_id = health.snapshot_run_id
     AND snapshot.database_id = health.database_id;
$health$;

REVOKE ALL ON FUNCTION reporting.list_database_health_v2(uuid,uuid,bigint,integer,integer)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor,sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.list_database_health_v2(uuid,uuid,bigint,integer,integer)
    TO sqlobserver_server;

COMMENT ON FUNCTION reporting.list_database_health_v2(uuid,uuid,bigint,integer,integer) IS
    'Bounded database-health page with nullable log-reuse state from the exact snapshot run.';
