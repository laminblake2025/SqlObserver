-- One exact versioned SQL baseline window, without row-level pagination.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.read_sql_metric_baselines(p_instance uuid,p_revision bigint,p_metric text,
 p_from timestamptz,p_to timestamptz,p_snapshot timestamptz,p_generation bigint)
RETURNS SETOF analytics.sql_metric_baseline
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE n integer; latest_computation timestamptz;
BEGIN
 IF p_instance IS NULL OR p_revision IS NULL OR p_revision<1 OR p_from IS NULL OR p_to IS NULL OR p_snapshot IS NULL
 OR p_generation IS NULL OR p_generation<1 OR p_to-p_from<>interval '28 days' OR p_to<>date_trunc('day',p_to)
 OR p_snapshot<p_to OR p_snapshot>clock_timestamp()+interval '1 minute'
 OR p_metric IS NULL OR p_metric NOT IN ('engine.user_connections','engine.process_physical_memory_bytes','engine.os_available_memory_bytes',
  'engine.memory_grants_pending','engine.scheduler_runnable_tasks','engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')
 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
 THEN RAISE EXCEPTION 'SQL baseline read bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.resolve_m10_target_revision(p_instance,p_revision);
 SELECT count(*),max(b.computed_at) INTO n,latest_computation FROM analytics.sql_metric_baseline b
 WHERE b.instance_id=p_instance AND b.target_revision=p_revision AND b.metric_key=p_metric AND b.window_end=p_to
  AND b.generation=p_generation AND b.catalog_version=2;
 IF n NOT IN (0,168) THEN RAISE EXCEPTION 'SQL baseline stored hours are incomplete' USING ERRCODE='55000'; END IF;
 -- A snapshot during computation does not publish a partial hourly bundle.
 IF n=0 OR latest_computation>p_snapshot THEN RETURN; END IF;
 RETURN QUERY SELECT b.* FROM analytics.sql_metric_baseline b
 WHERE b.instance_id=p_instance AND b.target_revision=p_revision AND b.metric_key=p_metric AND b.window_end=p_to
  AND b.generation=p_generation AND b.catalog_version=2 AND b.source_cutoff_utc<=p_snapshot ORDER BY b.hour_of_week;
END $$;
REVOKE ALL ON FUNCTION reporting.read_sql_metric_baselines(uuid,bigint,text,timestamptz,timestamptz,timestamptz,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_sql_metric_baselines(uuid,bigint,text,timestamptz,timestamptz,timestamptz,bigint) TO sqlobserver_server;
