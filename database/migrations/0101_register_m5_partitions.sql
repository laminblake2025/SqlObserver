-- Register the canonical M5 partitions that receive collector writes. M10's
-- legacy v2 parents are not the source of truth for these streams.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.ensure_activity_daily_partitions(p_partition_day date)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET TimeZone='UTC'
AS $$
DECLARE table_name text; v_partition_name text;
BEGIN
 IF p_partition_day IS NULL OR p_partition_day<current_date-2 OR p_partition_day>current_date+2 THEN
  RAISE EXCEPTION 'activity partition day is outside the bounded collection window' USING ERRCODE='22023';
 END IF;
 FOREACH table_name IN ARRAY ARRAY[
  'activity_session_snapshot','activity_request_snapshot','server_wait_snapshot']::text[] LOOP
  v_partition_name:=format('%s_%s',table_name,to_char(p_partition_day,'YYYYMMDD'));
  EXECUTE format('CREATE TABLE IF NOT EXISTS telemetry.%I PARTITION OF telemetry.%I FOR VALUES FROM (%L) TO (%L)',
   v_partition_name,table_name,p_partition_day::timestamptz,(p_partition_day+1)::timestamptz);
  EXECUTE format('REVOKE ALL ON TABLE telemetry.%I FROM PUBLIC',v_partition_name);
  IF NOT EXISTS (
   SELECT 1 FROM pg_catalog.pg_inherits i
   JOIN pg_catalog.pg_class part ON part.oid=i.inhrelid
   WHERE i.inhparent=format('telemetry.%I',table_name)::regclass
     AND part.oid=format('telemetry.%I',v_partition_name)::regclass
     AND pg_catalog.pg_get_expr(part.relpartbound,part.oid)=format('FOR VALUES FROM (%L) TO (%L)',
       p_partition_day::timestamptz,(p_partition_day+1)::timestamptz)) THEN
   RAISE EXCEPTION 'M5 activity partition has unexpected attachment or bounds: %',v_partition_name USING ERRCODE='55000';
  END IF;
  INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
  VALUES('telemetry',table_name,'telemetry',v_partition_name,'day',p_partition_day::timestamptz,(p_partition_day+1)::timestamptz)
  ON CONFLICT DO NOTHING;
  IF NOT EXISTS (
   SELECT 1 FROM system.partition_registry r WHERE r.parent_schema='telemetry' AND r.parent_table=table_name::name
    AND r.partition_schema='telemetry' AND r.partition_name=v_partition_name::name
    AND r.partition_granularity='day' AND r.range_start=p_partition_day::timestamptz
    AND r.range_end=(p_partition_day+1)::timestamptz AND r.lifecycle_state='attached') THEN
   RAISE EXCEPTION 'M5 activity partition registry conflicts with catalog: %',v_partition_name USING ERRCODE='55000';
  END IF;
 END LOOP;
END $$;

CREATE OR REPLACE FUNCTION control.ensure_blocking_monthly_partition(p_partition_month date)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system SET TimeZone='UTC'
AS $$
DECLARE month_start date; v_partition_name text;
BEGIN
 month_start:=date_trunc('month',p_partition_month)::date;
 IF p_partition_month IS NULL
    OR month_start<date_trunc('month',current_date-32)::date
    OR month_start>date_trunc('month',current_date+32)::date THEN
  RAISE EXCEPTION 'blocking partition month is outside the bounded collection window' USING ERRCODE='22023';
 END IF;
 v_partition_name:=format('blocking_edge_%s',to_char(month_start,'YYYYMM'));
 EXECUTE format('CREATE TABLE IF NOT EXISTS events.%I PARTITION OF events.blocking_edge FOR VALUES FROM (%L) TO (%L)',
  v_partition_name,month_start::timestamptz,(month_start+interval '1 month')::timestamptz);
 EXECUTE format('REVOKE ALL ON TABLE events.%I FROM PUBLIC',v_partition_name);
 IF NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_inherits i
  JOIN pg_catalog.pg_class part ON part.oid=i.inhrelid
  WHERE i.inhparent='events.blocking_edge'::regclass
    AND part.oid=format('events.%I',v_partition_name)::regclass
    AND pg_catalog.pg_get_expr(part.relpartbound,part.oid)=format('FOR VALUES FROM (%L) TO (%L)',
      month_start::timestamptz,(month_start+interval '1 month')::timestamptz)) THEN
  RAISE EXCEPTION 'M5 blocking partition has unexpected attachment or bounds: %',v_partition_name USING ERRCODE='55000';
 END IF;
 INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
 VALUES('events','blocking_edge','events',v_partition_name,'month',month_start::timestamptz,(month_start+interval '1 month')::timestamptz)
 ON CONFLICT DO NOTHING;
 IF NOT EXISTS (
  SELECT 1 FROM system.partition_registry r WHERE r.parent_schema='events' AND r.parent_table='blocking_edge'
   AND r.partition_schema='events' AND r.partition_name=v_partition_name::name
   AND r.partition_granularity='month' AND r.range_start=month_start::timestamptz
   AND r.range_end=(month_start+interval '1 month')::timestamptz AND r.lifecycle_state='attached') THEN
  RAISE EXCEPTION 'M5 blocking partition registry conflicts with catalog: %',v_partition_name USING ERRCODE='55000';
 END IF;
END $$;

-- Earlier migrations created attached children without registry records.
-- Recover their UTC bounds from the deterministic names used by the same
-- maintenance functions; reject an unexpected attached child for review.
DO $$
DECLARE child record; suffix text; start_day date; end_at timestamptz; granularity text; expected_name text;
BEGIN
 FOR child IN
  SELECT parent_ns.nspname AS parent_schema,parent.relname AS parent_table,
         child_ns.nspname AS partition_schema,part.relname AS partition_name,
         pg_catalog.pg_get_expr(part.relpartbound,part.oid) AS actual_bound
  FROM pg_catalog.pg_inherits inheritance
  JOIN pg_catalog.pg_class parent ON parent.oid=inheritance.inhparent
  JOIN pg_catalog.pg_namespace parent_ns ON parent_ns.oid=parent.relnamespace
  JOIN pg_catalog.pg_class part ON part.oid=inheritance.inhrelid
  JOIN pg_catalog.pg_namespace child_ns ON child_ns.oid=part.relnamespace
  WHERE (parent_ns.nspname='telemetry' AND parent.relname IN
          ('activity_session_snapshot','activity_request_snapshot','server_wait_snapshot'))
     OR (parent_ns.nspname='events' AND parent.relname='blocking_edge')
 LOOP
  IF child.partition_schema<>child.parent_schema THEN
   RAISE EXCEPTION 'M5 partition schema mismatch: %.%',child.partition_schema,child.partition_name USING ERRCODE='55000';
  END IF;
  granularity:=CASE WHEN child.parent_table='blocking_edge' THEN 'month' ELSE 'day' END;
  suffix:=substring(child.partition_name FROM length(child.parent_table)+2);
  IF granularity='month' THEN
   IF suffix !~ '^[0-9]{6}$' THEN RAISE EXCEPTION 'Unrecognized M5 partition: %',child.partition_name USING ERRCODE='55000'; END IF;
   start_day:=to_date(suffix||'01','YYYYMMDD');
   expected_name:=format('%s_%s',child.parent_table,to_char(start_day,'YYYYMM'));
  ELSE
   IF suffix !~ '^[0-9]{8}$' THEN RAISE EXCEPTION 'Unrecognized M5 partition: %',child.partition_name USING ERRCODE='55000'; END IF;
   start_day:=to_date(suffix,'YYYYMMDD');
   expected_name:=format('%s_%s',child.parent_table,to_char(start_day,'YYYYMMDD'));
  END IF;
  IF child.partition_name<>expected_name THEN
   RAISE EXCEPTION 'M5 partition name/boundary mismatch: %',child.partition_name USING ERRCODE='55000';
  END IF;
  end_at:=CASE WHEN granularity='month' THEN start_day::timestamptz+interval '1 month'
    ELSE start_day::timestamptz+interval '1 day' END;
  IF child.actual_bound IS DISTINCT FROM format('FOR VALUES FROM (%L) TO (%L)',start_day::timestamptz,end_at) THEN
   RAISE EXCEPTION 'M5 attached partition has unexpected bounds: %',child.partition_name USING ERRCODE='55000';
  END IF;
  INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
  VALUES(child.parent_schema,child.parent_table,child.partition_schema,child.partition_name,granularity,start_day::timestamptz,end_at)
  ON CONFLICT DO NOTHING;
  IF NOT EXISTS (
   SELECT 1 FROM system.partition_registry r WHERE r.parent_schema=child.parent_schema::name
    AND r.parent_table=child.parent_table::name AND r.partition_schema=child.partition_schema::name
    AND r.partition_name=child.partition_name::name AND r.partition_granularity=granularity
    AND r.range_start=start_day::timestamptz AND r.range_end=end_at AND r.lifecycle_state='attached') THEN
   RAISE EXCEPTION 'M5 historical partition registry conflicts with catalog: %',child.partition_name USING ERRCODE='55000';
  END IF;
 END LOOP;
END $$;
