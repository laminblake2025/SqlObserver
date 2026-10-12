-- Expire old plan ciphertext only when no run or diagnostic event refers to it.
-- Keep the query-text cleanup contract unchanged for rolling deployments.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

RESET ROLE;
DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = 'sqlobserver_payload_expirer'
                     AND rolcanlogin = false AND rolbypassrls = true
                     AND rolsuper = false AND rolcreatedb = false
                     AND rolcreaterole = false AND rolinherit = false
                     AND rolreplication = false) THEN
        RAISE EXCEPTION 'safe payload expirer role is required' USING ERRCODE = '55000';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
               JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
               WHERE member_role.rolname = 'sqlobserver_payload_expirer') THEN
        RAISE EXCEPTION 'payload expirer must not inherit another role' USING ERRCODE = '55000';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
                   JOIN pg_catalog.pg_roles granted_role ON granted_role.oid = membership.roleid
                   JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
                   WHERE granted_role.rolname = 'sqlobserver_payload_expirer'
                     AND member_role.rolname = current_user AND membership.admin_option) THEN
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                       WHERE rolname = current_user AND rolsuper) THEN
            RAISE EXCEPTION 'payload expirer requires temporary bootstrap membership'
                USING ERRCODE = '42501';
        END IF;
        EXECUTE format('GRANT sqlobserver_payload_expirer TO %I WITH ADMIN OPTION', current_user);
    END IF;
END
$role$;
GRANT sqlobserver_payload_expirer TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;

CREATE FUNCTION system.prune_orphan_query_plan_payloads(
    p_owner_execution_id uuid, p_fencing_token bigint, p_limit integer)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, system, control, security, events, audit
SET TimeZone = 'UTC'
AS $prune$
DECLARE v_deleted integer;
BEGIN
    IF p_owner_execution_id IS NULL OR p_fencing_token IS NULL
       OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'orphan plan cleanup arguments are invalid' USING ERRCODE = '22023';
    END IF;
    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);

    WITH candidate AS (
        SELECT payload.payload_id
        FROM security.protected_diagnostic_payload AS payload
        WHERE payload.payload_kind = 'execution_plan'
          AND payload.instance_id IS NOT NULL
          AND payload.created_at < clock_timestamp() - interval '7 days'
          AND coalesce(payload.last_seen_at, payload.created_at)
              < clock_timestamp() - interval '7 days'
          AND NOT EXISTS (
              SELECT 1 FROM events.query_performance_plan_content_link AS link
              WHERE link.content_reference = payload.payload_id)
          AND NOT EXISTS (
              SELECT 1 FROM events.diagnostic_event AS event
              WHERE event.protected_payload_id = payload.payload_id)
        ORDER BY payload.created_at, payload.payload_id
        LIMIT p_limit
        FOR UPDATE OF payload SKIP LOCKED
    ), deleted AS (
        DELETE FROM security.protected_diagnostic_payload AS payload
        USING candidate
        WHERE payload.payload_id = candidate.payload_id
        RETURNING payload.payload_id
    )
    SELECT count(*) INTO v_deleted FROM deleted;

    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);
    IF v_deleted > 0 THEN
        INSERT INTO audit.activity
            (activity_id, actor_kind, actor_identifier, action_name,
             authorization_result, outcome, correlation_id, safe_details)
        VALUES (gen_random_uuid(), 'system', 'collector/retention',
                'retention.query_plan_orphans.prune', 'not_applicable',
                'succeeded', gen_random_uuid(),
                jsonb_build_object('deletedCount', v_deleted,
                                   'minimumAgeDays', 7,
                                   'fencingToken', p_fencing_token));
    END IF;
    RETURN v_deleted;
END;
$prune$;

REVOKE ALL ON FUNCTION system.prune_orphan_query_plan_payloads(uuid,bigint,integer)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.prune_orphan_query_plan_payloads(uuid,bigint,integer)
    TO sqlobserver_collector;
GRANT SELECT ON events.query_performance_plan_content_link
    TO sqlobserver_payload_expirer;
GRANT CREATE ON SCHEMA system TO sqlobserver_payload_expirer;
ALTER FUNCTION system.prune_orphan_query_plan_payloads(uuid,bigint,integer)
    OWNER TO sqlobserver_payload_expirer;
REVOKE CREATE ON SCHEMA system FROM sqlobserver_payload_expirer;
RESET ROLE;
REVOKE sqlobserver_payload_expirer FROM sqlobserver_migrator;
REVOKE sqlobserver_payload_expirer FROM CURRENT_USER;
