-- M11 metric-series projection.  The continuation key mirrors the immutable
-- host metric primary key so equal timestamps cannot be skipped or repeated.
-- SECURITY DEFINER objects are always owned by the dedicated NOLOGIN role.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE OR REPLACE FUNCTION reporting.list_metric_series(
 p_instance_id uuid,
 p_target_revision bigint,
 p_from_utc timestamptz,
 p_to_utc timestamptz,
 p_metric_key text,
 p_limit integer,
 p_snapshot_utc timestamptz,
 p_cursor_at timestamptz,
 p_cursor_run_id uuid,
 p_cursor_metric_key text,
 p_cursor_dimensions jsonb)
RETURNS TABLE(observed_at timestamptz,run_id uuid,metric_key text,metric_value double precision,dimensions jsonb)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,control
SET TimeZone='UTC' AS $$
 SELECT h.observed_at,h.run_id,h.metric_key,h.metric_value,h.dimensions
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND h.instance_id=p_instance_id
   AND h.target_revision=p_target_revision
   AND h.metric_key=p_metric_key
   AND h.observed_at>=p_from_utc
   AND h.observed_at<p_to_utc
   AND h.observed_at<=p_snapshot_utc
   AND p_limit BETWEEN 1 AND 1001
   AND p_to_utc>p_from_utc
   AND p_to_utc-p_from_utc<=interval '31 days'
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND ((p_cursor_at IS NULL AND p_cursor_run_id IS NULL AND p_cursor_metric_key IS NULL AND p_cursor_dimensions IS NULL)
        OR (p_cursor_at IS NOT NULL AND p_cursor_run_id IS NOT NULL AND p_cursor_metric_key IS NOT NULL AND p_cursor_dimensions IS NOT NULL
            AND (h.observed_at,h.run_id,h.metric_key,h.dimensions)>
                (p_cursor_at,p_cursor_run_id,p_cursor_metric_key,p_cursor_dimensions)))
 ORDER BY h.observed_at,h.run_id,h.metric_key,h.dimensions
 LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)
 TO sqlobserver_server;
