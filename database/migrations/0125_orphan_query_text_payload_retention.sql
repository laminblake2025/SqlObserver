-- Bound cleanup to old, unlinked query text. A reused payload is touched by
-- the dedup writer under a row lock before a later run can link it.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- This function must see links across target RLS policies. Provision a
-- dedicated NOLOGIN/BYPASSRLS definer with only the required table grants.
RESET ROLE;
DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = 'sqlobserver_payload_expirer') THEN
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                       WHERE rolname = current_user AND rolsuper) THEN
            RAISE EXCEPTION 'sqlobserver_payload_expirer must be provisioned by a PostgreSQL cluster administrator'
                USING ERRCODE = '42501';
        END IF;
        EXECUTE 'CREATE ROLE sqlobserver_payload_expirer WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles
               WHERE rolname = 'sqlobserver_payload_expirer'
                 AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole
                      OR rolinherit OR rolreplication OR NOT rolbypassrls))
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
                  JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
                  WHERE member_role.rolname = 'sqlobserver_payload_expirer')
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
                  JOIN pg_catalog.pg_roles granted_role ON granted_role.oid = membership.roleid
                  JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
                  WHERE granted_role.rolname = 'sqlobserver_payload_expirer'
                    AND (member_role.rolname <> current_user OR NOT membership.admin_option)) THEN
        RAISE EXCEPTION 'sqlobserver_payload_expirer has unsafe role attributes or memberships'
            USING ERRCODE = '55000';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
                   JOIN pg_catalog.pg_roles granted_role ON granted_role.oid = membership.roleid
                   JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
                   WHERE granted_role.rolname = 'sqlobserver_payload_expirer'
                     AND member_role.rolname = current_user AND membership.admin_option) THEN
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                       WHERE rolname = current_user AND rolsuper) THEN
            RAISE EXCEPTION 'sqlobserver_payload_expirer requires temporary bootstrap membership'
                USING ERRCODE = '42501';
        END IF;
        EXECUTE format('GRANT sqlobserver_payload_expirer TO %I WITH ADMIN OPTION', current_user);
    END IF;
END
$role$;
GRANT sqlobserver_payload_expirer TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;

ALTER TABLE security.protected_diagnostic_payload
    ADD COLUMN last_seen_at timestamptz;
ALTER TABLE security.protected_diagnostic_payload
    ALTER COLUMN last_seen_at SET DEFAULT clock_timestamp();
GRANT SELECT (last_seen_at), UPDATE (last_seen_at)
    ON security.protected_diagnostic_payload
    TO sqlobserver_collector;

CREATE FUNCTION system.prune_orphan_query_text_payloads(
    p_owner_execution_id uuid, p_fencing_token bigint, p_limit integer)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, system, control, security, events, audit
SET TimeZone = 'UTC'
AS $prune$
DECLARE
    v_deleted integer;
BEGIN
    IF p_owner_execution_id IS NULL OR p_fencing_token IS NULL
       OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'orphan payload cleanup arguments are invalid'
            USING ERRCODE = '22023';
    END IF;
    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);

    WITH candidate AS (
        SELECT payload.payload_id
        FROM security.protected_diagnostic_payload AS payload
        WHERE payload.payload_kind = 'query_text'
          AND payload.instance_id IS NOT NULL
          AND payload.created_at < clock_timestamp() - interval '7 days'
          AND coalesce(payload.last_seen_at, payload.created_at)
              < clock_timestamp() - interval '7 days'
          AND NOT EXISTS (
              SELECT 1 FROM events.query_performance_content_link AS link
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
                'retention.query_text_orphans.prune', 'not_applicable',
                'succeeded', gen_random_uuid(),
                jsonb_build_object('deletedCount', v_deleted,
                                   'minimumAgeDays', 7,
                                   'fencingToken', p_fencing_token));
    END IF;
    RETURN v_deleted;
END;
$prune$;

REVOKE ALL ON FUNCTION system.prune_orphan_query_text_payloads(uuid,bigint,integer)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.prune_orphan_query_text_payloads(uuid,bigint,integer)
    TO sqlobserver_collector;

GRANT USAGE ON SCHEMA system, control, security, events, audit
    TO sqlobserver_payload_expirer;
GRANT CREATE ON SCHEMA system TO sqlobserver_payload_expirer;
-- PostgreSQL requires UPDATE privilege for SELECT ... FOR UPDATE.
GRANT SELECT, UPDATE, DELETE ON security.protected_diagnostic_payload
    TO sqlobserver_payload_expirer;
GRANT SELECT ON events.query_performance_content_link, events.diagnostic_event
    TO sqlobserver_payload_expirer;
GRANT INSERT ON audit.activity TO sqlobserver_payload_expirer;
GRANT EXECUTE ON FUNCTION control.assert_worker_lease(text,uuid,bigint)
    TO sqlobserver_payload_expirer;
ALTER FUNCTION system.prune_orphan_query_text_payloads(uuid,bigint,integer)
    OWNER TO sqlobserver_payload_expirer;
REVOKE CREATE ON SCHEMA system FROM sqlobserver_payload_expirer;
RESET ROLE;
REVOKE sqlobserver_payload_expirer FROM sqlobserver_migrator;
REVOKE sqlobserver_payload_expirer FROM CURRENT_USER;
