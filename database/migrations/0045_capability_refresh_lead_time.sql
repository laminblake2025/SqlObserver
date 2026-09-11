-- Renew capability evidence ahead of expiry so collectors with the same period
-- cannot repeatedly claim an expired profile just before discovery finishes.
-- Preserve repository-clock scheduling, bounds, leases and target revisions.
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION control.list_due_capability_targets(p_max_targets integer)
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
                    - least(interval '90 seconds', (latest_attempt.valid_until - latest_attempt.checked_at) / 2)
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
