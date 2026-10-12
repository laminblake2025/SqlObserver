-- Stage an operator-gated M5 reader route; installation keeps original reads.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.collection_run_v2_activity_read_route (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 use_v2 boolean NOT NULL DEFAULT false,
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 changed_at timestamptz,
 changed_by text
);
INSERT INTO system.collection_run_v2_activity_read_route(singleton) VALUES(true);
REVOKE ALL ON system.collection_run_v2_activity_read_route
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

ALTER FUNCTION reporting.list_activity_sessions(uuid,uuid,bigint,integer,integer) RENAME TO list_activity_sessions_legacy;
REVOKE ALL ON FUNCTION reporting.list_activity_sessions_legacy(uuid,uuid,bigint,integer,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.list_activity_sessions
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_session_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    collector_id text,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    observed_at timestamptz,
    session_id integer,
    status_code text,
    is_user_process boolean,
    database_id integer,
    open_transaction_count integer,
    cpu_ms bigint,
    memory_usage_pages bigint,
    reads bigint,
    writes bigint,
    logical_reads bigint,
    total_elapsed_ms bigint,
    has_more boolean,
    cursor_valid boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_activity_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.list_activity_sessions_v2_shadow(p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_session_id,p_max_results);
 ELSE
  RETURN QUERY SELECT * FROM reporting.list_activity_sessions_legacy(p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_session_id,p_max_results);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.list_activity_sessions(uuid,uuid,bigint,integer,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_activity_sessions(uuid,uuid,bigint,integer,integer) TO sqlobserver_server;

ALTER FUNCTION reporting.list_activity_requests(uuid,uuid,bigint,integer,integer,integer) RENAME TO list_activity_requests_legacy;
REVOKE ALL ON FUNCTION reporting.list_activity_requests_legacy(uuid,uuid,bigint,integer,integer,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.list_activity_requests
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_session_id integer,
    p_after_request_id integer,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    collector_id text,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    observed_at timestamptz,
    session_id integer,
    request_id integer,
    status_code text,
    command_code text,
    database_id integer,
    cpu_ms bigint,
    total_elapsed_ms bigint,
    reads bigint,
    writes bigint,
    logical_reads bigint,
    row_count bigint,
    percent_complete double precision,
    has_more boolean,
    cursor_valid boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_activity_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.list_activity_requests_v2_shadow(p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_session_id,p_after_request_id,p_max_results);
 ELSE
  RETURN QUERY SELECT * FROM reporting.list_activity_requests_legacy(p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_session_id,p_after_request_id,p_max_results);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.list_activity_requests(uuid,uuid,bigint,integer,integer,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_activity_requests(uuid,uuid,bigint,integer,integer,integer) TO sqlobserver_server;

ALTER FUNCTION reporting.list_current_blocking(uuid,uuid,bigint,integer,text,integer,text,integer) RENAME TO list_current_blocking_legacy;
REVOKE ALL ON FUNCTION reporting.list_current_blocking_legacy(uuid,uuid,bigint,integer,text,integer,text,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.list_current_blocking
(
    p_instance_id uuid,
    p_snapshot_run_id uuid,
    p_snapshot_target_revision bigint,
    p_after_blocked_session_id integer,
    p_after_blocker_kind text,
    p_after_blocker_session_id integer,
    p_after_wait_type text,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    collector_id text,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    observed_at timestamptz,
    blocked_session_id integer,
    blocker_kind text,
    blocker_session_id integer,
    wait_type text,
    waiting_task_count bigint,
    wait_duration_ms bigint,
    root_blocker_session_id integer,
    chain_depth integer,
    chain_state text,
    has_more boolean,
    cursor_valid boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_activity_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.list_current_blocking_v2_shadow(p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_blocked_session_id,p_after_blocker_kind,p_after_blocker_session_id,p_after_wait_type,p_max_results);
 ELSE
  RETURN QUERY SELECT * FROM reporting.list_current_blocking_legacy(p_instance_id,p_snapshot_run_id,p_snapshot_target_revision,p_after_blocked_session_id,p_after_blocker_kind,p_after_blocker_session_id,p_after_wait_type,p_max_results);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.list_current_blocking(uuid,uuid,bigint,integer,text,integer,text,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_current_blocking(uuid,uuid,bigint,integer,text,integer,text,integer) TO sqlobserver_server;

ALTER FUNCTION reporting.list_blocking_history(uuid,timestamptz,timestamptz,timestamptz,uuid,integer,text,integer,text,integer) RENAME TO list_blocking_history_legacy;
REVOKE ALL ON FUNCTION reporting.list_blocking_history_legacy(uuid,timestamptz,timestamptz,timestamptz,uuid,integer,text,integer,text,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION reporting.list_blocking_history
(
    p_instance_id uuid,
    p_from_utc timestamptz,
    p_to_utc timestamptz,
    p_after_observed_at timestamptz,
    p_after_run_id uuid,
    p_after_blocked_session_id integer,
    p_after_blocker_kind text,
    p_after_blocker_session_id integer,
    p_after_wait_type text,
    p_max_results integer
)
RETURNS TABLE
(
    target_instance_id uuid,
    observed_at timestamptz,
    snapshot_run_id uuid,
    snapshot_target_revision bigint,
    outcome text,
    reason_code text,
    loss_kind text,
    loss_count_exact boolean,
    lost_row_count bigint,
    lost_byte_count bigint,
    snapshot_completed_at timestamptz,
    blocked_session_id integer,
    blocker_kind text,
    blocker_session_id integer,
    wait_type text,
    waiting_task_count bigint,
    wait_duration_ms bigint,
    root_blocker_session_id integer,
    chain_depth integer,
    chain_state text,
    has_more boolean,
    repository_time timestamptz
)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $route$
BEGIN
 IF (SELECT route.use_v2 FROM system.collection_run_v2_activity_read_route AS route
     WHERE route.singleton) THEN
  RETURN QUERY SELECT * FROM reporting.list_blocking_history_v2_shadow(p_instance_id,p_from_utc,p_to_utc,p_after_observed_at,p_after_run_id,p_after_blocked_session_id,p_after_blocker_kind,p_after_blocker_session_id,p_after_wait_type,p_max_results);
 ELSE
  RETURN QUERY SELECT * FROM reporting.list_blocking_history_legacy(p_instance_id,p_from_utc,p_to_utc,p_after_observed_at,p_after_run_id,p_after_blocked_session_id,p_after_blocker_kind,p_after_blocker_session_id,p_after_wait_type,p_max_results);
 END IF;
END $route$;
REVOKE ALL ON FUNCTION reporting.list_blocking_history(uuid,timestamptz,timestamptz,timestamptz,uuid,integer,text,integer,text,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_blocking_history(uuid,timestamptz,timestamptz,timestamptz,uuid,integer,text,integer,text,integer) TO sqlobserver_server;

-- The fleet verifier holds the writer fence through this transaction. The
-- switch may be turned back off while original runs remain canonical.
CREATE FUNCTION control.set_collection_run_v2_activity_read_route(
 p_use_v2 boolean,p_max_pairs integer DEFAULT 10000)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,system
SET TimeZone='UTC'
AS $cutover$
DECLARE
 v_revision bigint;
BEGIN
 IF p_use_v2 IS NULL OR p_max_pairs IS NULL
    OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'activity read route bounds are invalid'
   USING ERRCODE='22023';
 END IF;
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'activity read route requires migrator'
   USING ERRCODE='42501';
 END IF;
 LOCK TABLE system.collection_run_v2_activity_read_route
  IN SHARE ROW EXCLUSIVE MODE;
 IF p_use_v2 THEN
  PERFORM 1 FROM control.verify_collection_run_v2_fleet_fenced(p_max_pairs);
 END IF;
 UPDATE system.collection_run_v2_activity_read_route AS route
 SET use_v2=p_use_v2,
     revision=route.revision+CASE WHEN route.use_v2<>p_use_v2 THEN 1 ELSE 0 END,
     changed_at=CASE WHEN route.use_v2<>p_use_v2 THEN clock_timestamp()
                     ELSE route.changed_at END,
     changed_by=CASE WHEN route.use_v2<>p_use_v2 THEN session_user::text
                     ELSE route.changed_by END
 WHERE route.singleton RETURNING route.revision INTO v_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'activity read route is missing' USING ERRCODE='55000';
 END IF;
 RETURN v_revision;
END $cutover$;
REVOKE ALL ON FUNCTION control.set_collection_run_v2_activity_read_route(boolean,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_collection_run_v2_activity_read_route(boolean,integer)
 TO sqlobserver_migrator;
