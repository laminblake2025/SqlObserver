SET LOCAL ROLE sqlobserver_migrator;
-- The dimensionless replication series reports the worst observed subscription
-- latency and total pending commands per run. Missing values remain absent.
CREATE OR REPLACE FUNCTION reporting.list_metric_series(
 p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_limit integer,
 p_snapshot_utc timestamptz,p_cursor_at timestamptz,p_cursor_run_id uuid,p_cursor_metric_key text,p_cursor_dimensions jsonb)
RETURNS TABLE(observed_at timestamptz,run_id uuid,metric_key text,metric_value double precision,dimensions jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 WITH source AS (
  SELECT h.observed_at,h.run_id,h.metric_key,h.metric_value,h.dimensions
  FROM telemetry.host_metric_snapshot_v2 h
  WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND h.metric_key=p_metric_key
    AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc
  UNION ALL
  SELECT r.observed_at,r.run_id,p_metric_key,
    CASE p_metric_key WHEN 'replication.latency_seconds' THEN max(r.latency_seconds) ELSE sum(r.pending_commands)::double precision END,'{}'::jsonb
  FROM telemetry.replication_snapshot_v2 r
  WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision
    AND p_metric_key IN ('replication.latency_seconds','replication.pending_commands')
    AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc
    AND r.visibility_scope=1
  GROUP BY r.observed_at,r.run_id
 ) SELECT s.* FROM source s
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND p_limit BETWEEN 1 AND 1001 AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '31 days'
   AND s.metric_value IS NOT NULL
   AND ((p_cursor_at IS NULL AND p_cursor_run_id IS NULL AND p_cursor_metric_key IS NULL AND p_cursor_dimensions IS NULL)
     OR (p_cursor_at IS NOT NULL AND p_cursor_run_id IS NOT NULL AND p_cursor_metric_key IS NOT NULL AND p_cursor_dimensions IS NOT NULL
       AND (s.observed_at,s.run_id,s.metric_key,s.dimensions)>(p_cursor_at,p_cursor_run_id,p_cursor_metric_key,p_cursor_dimensions)))
 ORDER BY s.observed_at,s.run_id,s.metric_key,s.dimensions LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb) TO sqlobserver_server;
