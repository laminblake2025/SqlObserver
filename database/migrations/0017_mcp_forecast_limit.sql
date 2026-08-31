-- M11: make the server forecast projection limit explicit.  The prior
-- six-argument function remains immutable for existing callers; this
-- overload gives MCP a bounded limit+1 lookahead contract.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE OR REPLACE FUNCTION reporting.get_m10_forecast_scoped(
    p_instance_id uuid,
    p_target_revision bigint,
    p_metric_key text,
    p_dimensions jsonb,
    p_horizon interval,
    p_snapshot_utc timestamptz,
    p_limit integer)
RETURNS SETOF analytics.metric_forecast
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control
SET TimeZone='UTC'
AS $m11_forecast_scoped$
    SELECT f.*
      FROM analytics.metric_forecast f
     WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
       AND f.instance_id=p_instance_id
       AND f.target_revision=p_target_revision
       AND f.metric_key=p_metric_key
       AND f.dimensions=p_dimensions
       AND f.horizon_start<=p_snapshot_utc+p_horizon
       AND f.horizon_end>p_snapshot_utc
       AND p_horizon BETWEEN interval '1 hour' AND interval '366 days'
       AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
       AND p_limit BETWEEN 1 AND 201
     ORDER BY f.horizon_start,f.forecast_id
     LIMIT p_limit;
$m11_forecast_scoped$;

REVOKE ALL ON FUNCTION reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer)
    TO sqlobserver_server;
