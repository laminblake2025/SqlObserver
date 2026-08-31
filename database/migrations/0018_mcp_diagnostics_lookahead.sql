-- M11: preserve the diagnostic API's maximum page size while allowing the
-- read adapter to fetch one bounded sentinel row for continuation detection.
-- This forward-only replacement intentionally leaves migration 0014
-- immutable.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE OR REPLACE FUNCTION reporting.search_m10_diagnostics(
    p_instance_id uuid,
    p_target_revision bigint,
    p_from_utc timestamptz,
    p_to_utc timestamptz,
    p_limit integer,
    p_cursor_at timestamptz,
    p_cursor_event_id uuid,
    p_snapshot_utc timestamptz)
RETURNS TABLE(
    occurred_at timestamptz,
    event_id uuid,
    event_kind text,
    severity smallint,
    safe_metadata jsonb,
    collected_at timestamptz,
    target_revision bigint)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,events,control
SET TimeZone='UTC' AS $$
 SELECT d.occurred_at,d.event_id,d.event_kind,d.severity,d.safe_metadata,d.collected_at,p_target_revision
 FROM events.diagnostic_event d
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND d.instance_id=p_instance_id
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND p_from_utc>=p_snapshot_utc-interval '7 days'
   AND p_to_utc>p_from_utc
   AND p_to_utc<=p_snapshot_utc
   AND (p_cursor_at IS NULL OR (d.occurred_at,d.event_id)>(p_cursor_at,p_cursor_event_id))
 ORDER BY d.occurred_at,d.event_id
 LIMIT least(greatest(p_limit,1),101);
$$;

REVOKE ALL ON FUNCTION reporting.search_m10_diagnostics(
    uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)
FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.search_m10_diagnostics(
    uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)
TO sqlobserver_server;
