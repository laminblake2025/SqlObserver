-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE FUNCTION control.ensure_daily_metric_partition(p_partition_day date)
RETURNS TABLE
(
    partition_relation regclass,
    created boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog, control, system
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    partition_name name;
    ensured_partition regclass;
    partition_range_start timestamptz;
    partition_range_end timestamptz;
    registry_entry system.partition_registry%ROWTYPE;
    registry_found boolean;
    was_created boolean := false;
    repository_now timestamptz;
BEGIN
    IF p_partition_day IS NULL
       OR p_partition_day < DATE '2000-01-01'
       OR p_partition_day >= DATE '2100-01-01' THEN
        RAISE EXCEPTION 'partition day is outside the supported UTC range [2000-01-01, 2100-01-01)'
            USING ERRCODE = '22023';
    END IF;

    partition_name := format('raw_metric_sample_p%s', to_char(p_partition_day, 'YYYYMMDD'));
    partition_range_start := p_partition_day::timestamp AT TIME ZONE 'UTC';
    partition_range_end := (p_partition_day + 1)::timestamp AT TIME ZONE 'UTC';

    PERFORM pg_advisory_xact_lock(
        hashtextextended('sqlobserver:partition:telemetry.raw_metric_sample:' || p_partition_day::text, 0)
    );
    repository_now := clock_timestamp();

    SELECT registry.*
    INTO registry_entry
    FROM system.partition_registry AS registry
    WHERE registry.parent_schema = 'telemetry'::name
      AND registry.parent_table = 'raw_metric_sample'::name
      AND registry.range_start = partition_range_start;

    registry_found := FOUND;

    ensured_partition := to_regclass(format('%I.%I', 'telemetry', partition_name));

    IF registry_found THEN
        IF registry_entry.partition_schema <> 'telemetry'::name
           OR registry_entry.partition_name <> partition_name
           OR registry_entry.partition_granularity <> 'day'
           OR registry_entry.range_end <> partition_range_end
           OR registry_entry.lifecycle_state <> 'attached' THEN
            RAISE EXCEPTION 'daily partition registry entry does not match the requested bounds'
                USING ERRCODE = '55000';
        END IF;

        IF ensured_partition IS NULL THEN
            RAISE EXCEPTION 'daily partition registry references a missing relation'
                USING ERRCODE = '55000';
        END IF;
    ELSE
        IF ensured_partition IS NOT NULL THEN
            RAISE EXCEPTION 'unregistered relation occupies the expected daily partition name'
                USING ERRCODE = '55000';
        END IF;

        EXECUTE format(
            'CREATE TABLE %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',
            'telemetry',
            partition_name,
            'telemetry',
            'raw_metric_sample',
            partition_range_start,
            partition_range_end
        );

        INSERT INTO system.partition_registry
        (
            parent_schema,
            parent_table,
            partition_schema,
            partition_name,
            partition_granularity,
            range_start,
            range_end,
            created_at
        )
        VALUES
        (
            'telemetry',
            'raw_metric_sample',
            'telemetry',
            partition_name,
            'day',
            partition_range_start,
            partition_range_end,
            repository_now
        );

        ensured_partition := to_regclass(format('%I.%I', 'telemetry', partition_name));
        was_created := true;
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_inherits AS inheritance
        WHERE inheritance.inhrelid = ensured_partition
          AND inheritance.inhparent = 'telemetry.raw_metric_sample'::regclass
    ) THEN
        RAISE EXCEPTION 'daily partition relation is not attached to the expected parent'
            USING ERRCODE = '55000';
    END IF;

    RETURN QUERY SELECT ensured_partition, was_created, repository_now;
END
$sqlobserver$;

CREATE FUNCTION control.ensure_monthly_event_partition(p_partition_month date)
RETURNS TABLE
(
    partition_relation regclass,
    created boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog, control, system
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    partition_name name;
    ensured_partition regclass;
    partition_range_start timestamptz;
    partition_range_end timestamptz;
    registry_entry system.partition_registry%ROWTYPE;
    registry_found boolean;
    was_created boolean := false;
    repository_now timestamptz;
BEGIN
    IF p_partition_month IS NULL
       OR p_partition_month < DATE '2000-01-01'
       OR p_partition_month >= DATE '2100-01-01'
       OR extract(day FROM p_partition_month) <> 1 THEN
        RAISE EXCEPTION 'partition month must be the first day of a month in UTC range [2000-01-01, 2100-01-01)'
            USING ERRCODE = '22023';
    END IF;

    partition_name := format('diagnostic_event_p%s', to_char(p_partition_month, 'YYYYMM'));
    partition_range_start := p_partition_month::timestamp AT TIME ZONE 'UTC';
    partition_range_end := (p_partition_month + interval '1 month')::timestamp AT TIME ZONE 'UTC';

    PERFORM pg_advisory_xact_lock(
        hashtextextended('sqlobserver:partition:events.diagnostic_event:' || p_partition_month::text, 0)
    );
    repository_now := clock_timestamp();

    SELECT registry.*
    INTO registry_entry
    FROM system.partition_registry AS registry
    WHERE registry.parent_schema = 'events'::name
      AND registry.parent_table = 'diagnostic_event'::name
      AND registry.range_start = partition_range_start;

    registry_found := FOUND;

    ensured_partition := to_regclass(format('%I.%I', 'events', partition_name));

    IF registry_found THEN
        IF registry_entry.partition_schema <> 'events'::name
           OR registry_entry.partition_name <> partition_name
           OR registry_entry.partition_granularity <> 'month'
           OR registry_entry.range_end <> partition_range_end
           OR registry_entry.lifecycle_state <> 'attached' THEN
            RAISE EXCEPTION 'monthly partition registry entry does not match the requested bounds'
                USING ERRCODE = '55000';
        END IF;

        IF ensured_partition IS NULL THEN
            RAISE EXCEPTION 'monthly partition registry references a missing relation'
                USING ERRCODE = '55000';
        END IF;
    ELSE
        IF ensured_partition IS NOT NULL THEN
            RAISE EXCEPTION 'unregistered relation occupies the expected monthly partition name'
                USING ERRCODE = '55000';
        END IF;

        EXECUTE format(
            'CREATE TABLE %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',
            'events',
            partition_name,
            'events',
            'diagnostic_event',
            partition_range_start,
            partition_range_end
        );

        INSERT INTO system.partition_registry
        (
            parent_schema,
            parent_table,
            partition_schema,
            partition_name,
            partition_granularity,
            range_start,
            range_end,
            created_at
        )
        VALUES
        (
            'events',
            'diagnostic_event',
            'events',
            partition_name,
            'month',
            partition_range_start,
            partition_range_end,
            repository_now
        );

        ensured_partition := to_regclass(format('%I.%I', 'events', partition_name));
        was_created := true;
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_inherits AS inheritance
        WHERE inheritance.inhrelid = ensured_partition
          AND inheritance.inhparent = 'events.diagnostic_event'::regclass
    ) THEN
        RAISE EXCEPTION 'monthly partition relation is not attached to the expected parent'
            USING ERRCODE = '55000';
    END IF;

    RETURN QUERY SELECT ensured_partition, was_created, repository_now;
END
$sqlobserver$;

CREATE FUNCTION control.acquire_worker_lease(
    p_work_key text,
    p_owner_execution_id uuid,
    p_ttl interval
)
RETURNS TABLE
(
    acquired boolean,
    fencing_token bigint,
    acquired_at timestamptz,
    renewed_at timestamptz,
    expires_at timestamptz,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog, control
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    repository_now timestamptz;
    acquired_token bigint;
    lease_acquired_at timestamptz;
    lease_renewed_at timestamptz;
    acquired_expiry timestamptz;
BEGIN
    IF p_work_key IS NULL
       OR octet_length(p_work_key) NOT BETWEEN 1 AND 256
       OR p_work_key <> btrim(p_work_key) THEN
        RAISE EXCEPTION 'work key must be trimmed and contain between 1 and 256 bytes'
            USING ERRCODE = '22023';
    END IF;

    IF p_owner_execution_id IS NULL THEN
        RAISE EXCEPTION 'owner execution id is required'
            USING ERRCODE = '22023';
    END IF;

    IF p_ttl IS NULL OR p_ttl < interval '5 seconds' OR p_ttl > interval '10 minutes' THEN
        RAISE EXCEPTION 'lease TTL must be between 5 seconds and 10 minutes'
            USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(
        hashtextextended('sqlobserver:lease:' || p_work_key, 0)
    );
    repository_now := clock_timestamp();

    INSERT INTO control.worker_lease AS lease
    (
        work_key,
        owner_execution_id,
        fencing_token,
        acquired_at,
        renewed_at,
        expires_at,
        released_at
    )
    VALUES
    (
        p_work_key,
        p_owner_execution_id,
        1,
        repository_now,
        repository_now,
        repository_now + p_ttl,
        NULL
    )
    ON CONFLICT (work_key) DO UPDATE
    SET owner_execution_id = EXCLUDED.owner_execution_id,
        fencing_token = lease.fencing_token + 1,
        acquired_at = EXCLUDED.acquired_at,
        renewed_at = EXCLUDED.renewed_at,
        expires_at = EXCLUDED.expires_at,
        released_at = NULL
    WHERE lease.released_at IS NOT NULL OR lease.expires_at <= EXCLUDED.acquired_at
    RETURNING
        lease.fencing_token,
        lease.acquired_at,
        lease.renewed_at,
        lease.expires_at
    INTO
        acquired_token,
        lease_acquired_at,
        lease_renewed_at,
        acquired_expiry;

    IF FOUND THEN
        RETURN QUERY SELECT
            true,
            acquired_token,
            lease_acquired_at,
            lease_renewed_at,
            acquired_expiry,
            repository_now;
    ELSE
        RETURN QUERY SELECT
            false,
            NULL::bigint,
            NULL::timestamptz,
            NULL::timestamptz,
            NULL::timestamptz,
            repository_now;
    END IF;
END
$sqlobserver$;

CREATE FUNCTION control.renew_worker_lease(
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint,
    p_ttl interval
)
RETURNS TABLE
(
    renewed boolean,
    acquired_at timestamptz,
    renewed_at timestamptz,
    expires_at timestamptz,
    repository_time timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog, control
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    repository_now timestamptz;
    lease_acquired_at timestamptz;
    lease_renewed_at timestamptz;
    renewed_expiry timestamptz;
BEGIN
    IF p_work_key IS NULL
       OR octet_length(p_work_key) NOT BETWEEN 1 AND 256
       OR p_work_key <> btrim(p_work_key)
       OR p_owner_execution_id IS NULL
       OR p_fencing_token IS NULL
       OR p_fencing_token <= 0 THEN
        RAISE EXCEPTION 'valid work key, owner execution id, and positive fencing token are required'
            USING ERRCODE = '22023';
    END IF;

    IF p_ttl IS NULL OR p_ttl < interval '5 seconds' OR p_ttl > interval '10 minutes' THEN
        RAISE EXCEPTION 'lease TTL must be between 5 seconds and 10 minutes'
            USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(
        hashtextextended('sqlobserver:lease:' || p_work_key, 0)
    );
    repository_now := clock_timestamp();

    UPDATE control.worker_lease AS lease
    SET renewed_at = repository_now,
        expires_at = repository_now + p_ttl
    WHERE lease.work_key = p_work_key
      AND lease.owner_execution_id = p_owner_execution_id
      AND lease.fencing_token = p_fencing_token
      AND lease.released_at IS NULL
      AND lease.expires_at > repository_now
    RETURNING lease.acquired_at, lease.renewed_at, lease.expires_at
    INTO lease_acquired_at, lease_renewed_at, renewed_expiry;

    IF FOUND THEN
        RETURN QUERY SELECT
            true,
            lease_acquired_at,
            lease_renewed_at,
            renewed_expiry,
            repository_now;
    ELSE
        RETURN QUERY SELECT
            false,
            NULL::timestamptz,
            NULL::timestamptz,
            NULL::timestamptz,
            repository_now;
    END IF;
END
$sqlobserver$;

CREATE FUNCTION control.release_worker_lease(
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog, control
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    repository_now timestamptz;
BEGIN
    IF p_work_key IS NULL
       OR octet_length(p_work_key) NOT BETWEEN 1 AND 256
       OR p_work_key <> btrim(p_work_key)
       OR p_owner_execution_id IS NULL
       OR p_fencing_token IS NULL
       OR p_fencing_token <= 0 THEN
        RAISE EXCEPTION 'valid work key, owner execution id, and positive fencing token are required'
            USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(
        hashtextextended('sqlobserver:lease:' || p_work_key, 0)
    );
    repository_now := clock_timestamp();

    UPDATE control.worker_lease AS lease
    SET released_at = repository_now
    WHERE lease.work_key = p_work_key
      AND lease.owner_execution_id = p_owner_execution_id
      AND lease.fencing_token = p_fencing_token
      AND lease.released_at IS NULL
      AND lease.expires_at > repository_now;

    RETURN FOUND;
END
$sqlobserver$;

CREATE FUNCTION control.assert_worker_lease(
    p_work_key text,
    p_owner_execution_id uuid,
    p_fencing_token bigint
)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog, control
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    repository_now timestamptz;
    asserted_token bigint;
BEGIN
    IF p_work_key IS NULL
       OR octet_length(p_work_key) NOT BETWEEN 1 AND 256
       OR p_work_key <> btrim(p_work_key)
       OR p_owner_execution_id IS NULL
       OR p_fencing_token IS NULL
       OR p_fencing_token <= 0 THEN
        RAISE EXCEPTION 'valid work key, owner execution id, and positive fencing token are required'
            USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(
        hashtextextended('sqlobserver:lease:' || p_work_key, 0)
    );
    repository_now := clock_timestamp();

    SELECT lease.fencing_token
    INTO asserted_token
    FROM control.worker_lease AS lease
    WHERE lease.work_key = p_work_key
      AND lease.owner_execution_id = p_owner_execution_id
      AND lease.fencing_token = p_fencing_token
      AND lease.released_at IS NULL
      AND lease.expires_at > repository_now
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'worker lease assertion failed'
            USING ERRCODE = '55000';
    END IF;

    RETURN asserted_token;
END
$sqlobserver$;

COMMENT ON FUNCTION control.ensure_daily_metric_partition(date) IS
    'Concurrency-safe creation/verification of one allowlisted UTC daily raw-metric partition.';
COMMENT ON FUNCTION control.ensure_monthly_event_partition(date) IS
    'Concurrency-safe creation/verification of one allowlisted UTC monthly event partition.';
COMMENT ON FUNCTION control.acquire_worker_lease(text, uuid, interval) IS
    'Atomically acquires an absent, released, or expired lease using repository time and a monotonic fence.';
COMMENT ON FUNCTION control.renew_worker_lease(text, uuid, bigint, interval) IS
    'Renews only the current unexpired owner/fence using repository time.';
COMMENT ON FUNCTION control.release_worker_lease(text, uuid, bigint) IS
    'Marks only the current unexpired owner/fence released without deleting its monotonic fence history.';
COMMENT ON FUNCTION control.assert_worker_lease(text, uuid, bigint) IS
    'Fails closed and row-locks the current owner/fence; invoke in the same transaction as protected writes.';

REVOKE ALL ON FUNCTION control.ensure_daily_metric_partition(date) FROM PUBLIC;
REVOKE ALL ON FUNCTION control.ensure_monthly_event_partition(date) FROM PUBLIC;
REVOKE ALL ON FUNCTION control.acquire_worker_lease(text, uuid, interval) FROM PUBLIC;
REVOKE ALL ON FUNCTION control.renew_worker_lease(text, uuid, bigint, interval) FROM PUBLIC;
REVOKE ALL ON FUNCTION control.release_worker_lease(text, uuid, bigint) FROM PUBLIC;
REVOKE ALL ON FUNCTION control.assert_worker_lease(text, uuid, bigint) FROM PUBLIC;
