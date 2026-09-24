-- Only maintain partitions for streams that currently receive writes. The
-- other M10 v2 parents remain available for a future, explicit cutover.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE OR REPLACE FUNCTION control.ensure_m10_partition_set(p_anchor date DEFAULT current_date)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,events,analytics,system SET TimeZone='UTC'
AS $ensure_m10$
DECLARE
 d date;
 m date;
 spec text;
 parent_name text;
 schema_name text;
 partition_name text;
 n integer := 0;
 daily text[] := ARRAY[
  'telemetry:host_metric_snapshot_v2',
  'telemetry:replication_snapshot_v2',
  'analytics:metric_rollup_v2'];
 monthly text[] := ARRAY[
  'events:diagnostic_event',
  'analytics:evidence_packet_v2'];
BEGIN
 IF p_anchor IS NULL OR p_anchor < current_date-1 OR p_anchor > current_date+1 THEN
  RAISE EXCEPTION 'M10 anchor outside UTC maintenance window' USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:m10:partition-set',0));
 FOREACH spec IN ARRAY daily LOOP
  schema_name := split_part(spec,':',1);
  parent_name := split_part(spec,':',2);
  FOR d IN SELECT p_anchor+i FROM generate_series(-1,7) i LOOP
   partition_name := format('%s_p%s',parent_name,to_char(d,'YYYYMMDD'));
   EXECUTE format('CREATE TABLE IF NOT EXISTS %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',
    schema_name,partition_name,schema_name,parent_name,d::timestamptz,(d+1)::timestamptz);
   INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
   VALUES(schema_name,parent_name,schema_name,partition_name,'day',d::timestamptz,(d+1)::timestamptz)
   ON CONFLICT DO NOTHING;
   n := n+1;
  END LOOP;
 END LOOP;
 m := date_trunc('month',p_anchor)::date;
 FOREACH spec IN ARRAY monthly LOOP
  schema_name := split_part(spec,':',1);
  parent_name := split_part(spec,':',2);
  FOR d IN SELECT (m + (i||' month')::interval)::date FROM generate_series(0,2) i LOOP
   partition_name := format('%s_p%s',parent_name,to_char(d,'YYYYMM'));
   EXECUTE format('CREATE TABLE IF NOT EXISTS %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',
    schema_name,partition_name,schema_name,parent_name,d::timestamptz,(d+interval '1 month')::timestamptz);
   INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
   VALUES(schema_name,parent_name,schema_name,partition_name,'month',d::timestamptz,(d+interval '1 month')::timestamptz)
   ON CONFLICT DO NOTHING;
   n := n+1;
  END LOOP;
 END LOOP;
 RETURN n;
END $ensure_m10$;
