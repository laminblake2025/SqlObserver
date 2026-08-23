-- Transactional: the migration runner executes this entire file and its ledger write
-- in one transaction. Do not run this file as independent autocommit statements.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE FUNCTION control.validate_metric_replay
(
    p_observed_ats timestamptz[],
    p_sample_ids uuid[],
    p_instance_ids uuid[],
    p_metric_keys text[],
    p_metric_values double precision[],
    p_dimensions jsonb[]
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
STABLE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    item_count integer;
BEGIN
    IF p_observed_ats IS NULL
       OR p_sample_ids IS NULL
       OR p_instance_ids IS NULL
       OR p_metric_keys IS NULL
       OR p_metric_values IS NULL
       OR p_dimensions IS NULL
       OR array_ndims(p_observed_ats) <> 1
       OR array_ndims(p_sample_ids) <> 1
       OR array_ndims(p_instance_ids) <> 1
       OR array_ndims(p_metric_keys) <> 1
       OR array_ndims(p_metric_values) <> 1
       OR array_ndims(p_dimensions) <> 1 THEN
        RAISE EXCEPTION 'metric replay inputs must be non-null one-dimensional arrays'
            USING ERRCODE = '22023';
    END IF;

    item_count := cardinality(p_observed_ats);
    IF item_count NOT BETWEEN 1 AND 10000
       OR cardinality(p_sample_ids) <> item_count
       OR cardinality(p_instance_ids) <> item_count
       OR cardinality(p_metric_keys) <> item_count
       OR cardinality(p_metric_values) <> item_count
       OR cardinality(p_dimensions) <> item_count THEN
        RAISE EXCEPTION 'metric replay arrays must have equal cardinality in range [1, 10000]'
            USING ERRCODE = '22023';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM ROWS FROM
        (
            pg_catalog.unnest(p_observed_ats),
            pg_catalog.unnest(p_sample_ids),
            pg_catalog.unnest(p_instance_ids),
            pg_catalog.unnest(p_metric_keys),
            pg_catalog.unnest(p_metric_values),
            pg_catalog.unnest(p_dimensions)
        ) AS candidate
        (
            observed_at,
            sample_id,
            instance_id,
            metric_key,
            metric_value,
            dimensions
        )
        WHERE candidate.observed_at IS NULL
           OR candidate.sample_id IS NULL
           OR candidate.instance_id IS NULL
           OR candidate.metric_key IS NULL
           OR candidate.metric_value IS NULL
           OR candidate.dimensions IS NULL
    ) THEN
        RAISE EXCEPTION 'metric replay arrays must not contain null elements'
            USING ERRCODE = '22023';
    END IF;

    RETURN NOT EXISTS
    (
        SELECT 1
        FROM ROWS FROM
        (
            pg_catalog.unnest(p_observed_ats),
            pg_catalog.unnest(p_sample_ids),
            pg_catalog.unnest(p_instance_ids),
            pg_catalog.unnest(p_metric_keys),
            pg_catalog.unnest(p_metric_values),
            pg_catalog.unnest(p_dimensions)
        ) AS candidate
        (
            observed_at,
            sample_id,
            instance_id,
            metric_key,
            metric_value,
            dimensions
        )
        LEFT JOIN telemetry.raw_metric_sample AS persisted
          ON persisted.observed_at = candidate.observed_at
         AND persisted.sample_id = candidate.sample_id
        WHERE persisted.sample_id IS NULL
           OR persisted.instance_id IS DISTINCT FROM candidate.instance_id
           OR persisted.metric_key IS DISTINCT FROM candidate.metric_key
           OR persisted.metric_value IS DISTINCT FROM candidate.metric_value
           OR persisted.dimensions IS DISTINCT FROM candidate.dimensions
    );
END
$sqlobserver$;

COMMENT ON FUNCTION control.validate_metric_replay
    (timestamptz[], uuid[], uuid[], text[], double precision[], jsonb[]) IS
    'Returns true only when every bounded caller-owned metric identity and value matches persisted content. Repository-assigned collected_at is intentionally excluded.';

CREATE FUNCTION control.validate_diagnostic_event_replay
(
    p_occurred_ats timestamptz[],
    p_event_ids uuid[],
    p_instance_ids uuid[],
    p_event_kinds text[],
    p_protected_payload_ids uuid[],
    p_collected_ats timestamptz[]
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
STABLE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    item_count integer;
BEGIN
    IF p_occurred_ats IS NULL
       OR p_event_ids IS NULL
       OR p_instance_ids IS NULL
       OR p_event_kinds IS NULL
       OR p_protected_payload_ids IS NULL
       OR p_collected_ats IS NULL
       OR array_ndims(p_occurred_ats) <> 1
       OR array_ndims(p_event_ids) <> 1
       OR array_ndims(p_instance_ids) <> 1
       OR array_ndims(p_event_kinds) <> 1
       OR array_ndims(p_protected_payload_ids) <> 1
       OR array_ndims(p_collected_ats) <> 1 THEN
        RAISE EXCEPTION 'diagnostic-event replay inputs must be non-null one-dimensional arrays'
            USING ERRCODE = '22023';
    END IF;

    item_count := cardinality(p_occurred_ats);
    IF item_count NOT BETWEEN 1 AND 10000
       OR cardinality(p_event_ids) <> item_count
       OR cardinality(p_instance_ids) <> item_count
       OR cardinality(p_event_kinds) <> item_count
       OR cardinality(p_protected_payload_ids) <> item_count
       OR cardinality(p_collected_ats) <> item_count THEN
        RAISE EXCEPTION 'diagnostic-event replay arrays must have equal cardinality in range [1, 10000]'
            USING ERRCODE = '22023';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM ROWS FROM
        (
            pg_catalog.unnest(p_occurred_ats),
            pg_catalog.unnest(p_event_ids),
            pg_catalog.unnest(p_instance_ids),
            pg_catalog.unnest(p_event_kinds),
            pg_catalog.unnest(p_protected_payload_ids),
            pg_catalog.unnest(p_collected_ats)
        ) AS candidate
        (
            occurred_at,
            event_id,
            instance_id,
            event_kind,
            protected_payload_id,
            collected_at
        )
        WHERE candidate.occurred_at IS NULL
           OR candidate.event_id IS NULL
           OR candidate.instance_id IS NULL
           OR candidate.event_kind IS NULL
           OR candidate.collected_at IS NULL
    ) THEN
        RAISE EXCEPTION 'diagnostic-event replay arrays contain an invalid null element'
            USING ERRCODE = '22023';
    END IF;

    RETURN NOT EXISTS
    (
        SELECT 1
        FROM ROWS FROM
        (
            pg_catalog.unnest(p_occurred_ats),
            pg_catalog.unnest(p_event_ids),
            pg_catalog.unnest(p_instance_ids),
            pg_catalog.unnest(p_event_kinds),
            pg_catalog.unnest(p_protected_payload_ids),
            pg_catalog.unnest(p_collected_ats)
        ) AS candidate
        (
            occurred_at,
            event_id,
            instance_id,
            event_kind,
            protected_payload_id,
            collected_at
        )
        LEFT JOIN events.diagnostic_event AS persisted
          ON persisted.occurred_at = candidate.occurred_at
         AND persisted.event_id = candidate.event_id
        WHERE persisted.event_id IS NULL
           OR persisted.instance_id IS DISTINCT FROM candidate.instance_id
           OR persisted.event_kind IS DISTINCT FROM candidate.event_kind
           OR persisted.protected_payload_id IS DISTINCT FROM candidate.protected_payload_id
           OR persisted.collected_at IS DISTINCT FROM candidate.collected_at
           OR persisted.severity <> 0
           OR persisted.safe_metadata <> '{}'::jsonb
    );
END
$sqlobserver$;

COMMENT ON FUNCTION control.validate_diagnostic_event_replay
    (timestamptz[], uuid[], uuid[], text[], uuid[], timestamptz[]) IS
    'Returns true only when every bounded caller-owned diagnostic-event identity and value matches persisted content, including repository-fixed severity and safe metadata.';

REVOKE ALL ON FUNCTION control.validate_metric_replay
    (timestamptz[], uuid[], uuid[], text[], double precision[], jsonb[])
    FROM PUBLIC;
REVOKE ALL ON FUNCTION control.validate_diagnostic_event_replay
    (timestamptz[], uuid[], uuid[], text[], uuid[], timestamptz[])
    FROM PUBLIC;

GRANT EXECUTE ON FUNCTION control.validate_metric_replay
    (timestamptz[], uuid[], uuid[], text[], double precision[], jsonb[])
    TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.validate_diagnostic_event_replay
    (timestamptz[], uuid[], uuid[], text[], uuid[], timestamptz[])
    TO sqlobserver_collector;
