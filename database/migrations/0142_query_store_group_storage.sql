-- Separate cumulative Query Store group state from additive group deltas.
-- No collector writes these relations until a fenced batch writer is installed.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.query_store_group_watermark
(
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    database_id integer NOT NULL CHECK (database_id BETWEEN 1 AND 32767),
    database_incarnation uuid NOT NULL CHECK (database_incarnation <> '00000000-0000-0000-0000-000000000000'::uuid),
    query_fingerprint bytea NOT NULL CHECK (octet_length(query_fingerprint) = 32),
    plan_fingerprint bytea NOT NULL CHECK (octet_length(plan_fingerprint) = 32),
    plan_initial_compile_at timestamptz NOT NULL,
    source_interval_id bigint NOT NULL CHECK (source_interval_id > 0),
    interval_start timestamptz NOT NULL,
    interval_end timestamptz NOT NULL,
    execution_type smallint NOT NULL CHECK (execution_type BETWEEN 0 AND 10),
    cpu_ms bigint NOT NULL CHECK (cpu_ms >= 0),
    duration_ms bigint NOT NULL CHECK (duration_ms >= 0),
    execution_count bigint NOT NULL CHECK (execution_count >= 0),
    logical_reads bigint NOT NULL CHECK (logical_reads >= 0),
    writes bigint NOT NULL CHECK (writes >= 0),
    rows_processed bigint NOT NULL CHECK (rows_processed >= 0),
    first_execution_at timestamptz NOT NULL,
    last_execution_at timestamptz NOT NULL,
    runtime_observed_at timestamptz NOT NULL,
    wait_categories jsonb,
    wait_observed_at timestamptz,
    last_run_id uuid NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT pk_query_store_group_watermark PRIMARY KEY
      (interval_end,instance_id,target_revision,database_id,database_incarnation,
       query_fingerprint,plan_fingerprint,plan_initial_compile_at,
       source_interval_id,interval_start,execution_type),
    CONSTRAINT ck_query_store_group_watermark_time CHECK
      (isfinite(interval_start) AND isfinite(interval_end)
       AND isfinite(plan_initial_compile_at) AND isfinite(first_execution_at)
       AND isfinite(last_execution_at) AND isfinite(runtime_observed_at)
       AND isfinite(updated_at) AND interval_end > interval_start
       AND interval_end - interval_start <= interval '7 days'
       AND last_execution_at >= first_execution_at),
    CONSTRAINT ck_query_store_group_watermark_wait CHECK
      ((wait_categories IS NULL AND wait_observed_at IS NULL)
       OR (jsonb_typeof(wait_categories) = 'object'
           AND octet_length(wait_categories::text) <= 4096
           AND wait_observed_at IS NOT NULL
           AND isfinite(wait_observed_at)))
) PARTITION BY RANGE (interval_end);

CREATE INDEX ix_query_store_group_watermark_target_plan
    ON events.query_store_group_watermark(instance_id,database_id,plan_fingerprint,interval_end DESC);

CREATE TABLE events.query_store_group_delta
(
    observed_at timestamptz NOT NULL,
    run_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    database_id integer NOT NULL CHECK (database_id BETWEEN 1 AND 32767),
    database_incarnation uuid NOT NULL CHECK (database_incarnation <> '00000000-0000-0000-0000-000000000000'::uuid),
    query_fingerprint bytea NOT NULL CHECK (octet_length(query_fingerprint) = 32),
    plan_fingerprint bytea NOT NULL CHECK (octet_length(plan_fingerprint) = 32),
    plan_initial_compile_at timestamptz NOT NULL,
    source_interval_id bigint NOT NULL CHECK (source_interval_id > 0),
    interval_start timestamptz NOT NULL,
    interval_end timestamptz NOT NULL,
    execution_type smallint NOT NULL CHECK (execution_type BETWEEN 0 AND 10),
    source_window_start timestamptz NOT NULL,
    source_window_end timestamptz NOT NULL,
    runtime_transition text NOT NULL CHECK (runtime_transition IN
      ('baseline_unavailable','comparable','reset','epoch_ambiguous')),
    cpu_ms bigint,
    duration_ms bigint,
    execution_count bigint,
    logical_reads bigint,
    writes bigint,
    rows_processed bigint,
    wait_transition text NOT NULL CHECK (wait_transition IN
      ('unavailable','baseline_unavailable','comparable','reset')),
    wait_delta jsonb,
    wait_observed_at timestamptz,
    committed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT pk_query_store_group_delta PRIMARY KEY
      (observed_at,run_id,database_id,query_fingerprint,plan_fingerprint,
       source_interval_id,execution_type),
    CONSTRAINT ck_query_store_group_delta_time CHECK
      (isfinite(observed_at) AND isfinite(plan_initial_compile_at)
       AND isfinite(interval_start) AND isfinite(interval_end)
       AND isfinite(source_window_start) AND isfinite(source_window_end)
       AND isfinite(committed_at) AND interval_end > interval_start
       AND interval_end - interval_start <= interval '7 days'
       AND source_window_end > source_window_start
       AND source_window_end - source_window_start <= interval '7 days'
       AND (wait_observed_at IS NULL OR isfinite(wait_observed_at))),
    CONSTRAINT ck_query_store_group_delta_counters CHECK
      ((runtime_transition = 'comparable'
        AND cpu_ms >= 0 AND duration_ms >= 0 AND execution_count >= 0
        AND logical_reads >= 0 AND writes >= 0 AND rows_processed >= 0)
       OR (runtime_transition <> 'comparable'
        AND cpu_ms IS NULL AND duration_ms IS NULL AND execution_count IS NULL
        AND logical_reads IS NULL AND writes IS NULL AND rows_processed IS NULL)),
    CONSTRAINT ck_query_store_group_delta_wait CHECK
      ((wait_transition = 'comparable' AND jsonb_typeof(wait_delta) = 'object'
        AND octet_length(wait_delta::text) <= 4096
        AND wait_observed_at IS NOT NULL)
       OR (wait_transition <> 'comparable' AND wait_delta IS NULL))
) PARTITION BY RANGE (observed_at);

CREATE INDEX ix_query_store_group_delta_target_time
    ON events.query_store_group_delta(instance_id,observed_at DESC,run_id);

ALTER TABLE events.query_store_group_watermark ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.query_store_group_watermark FORCE ROW LEVEL SECURITY;
CREATE POLICY query_store_group_watermark_scope
    ON events.query_store_group_watermark FOR ALL TO sqlobserver_migrator
    USING (instance_id::text = current_setting('sqlobserver.target_scope',true))
    WITH CHECK (instance_id::text = current_setting('sqlobserver.target_scope',true));

ALTER TABLE events.query_store_group_delta ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.query_store_group_delta FORCE ROW LEVEL SECURITY;
CREATE POLICY query_store_group_delta_scope
    ON events.query_store_group_delta FOR ALL TO sqlobserver_migrator
    USING (instance_id::text = current_setting('sqlobserver.target_scope',true))
    WITH CHECK (instance_id::text = current_setting('sqlobserver.target_scope',true));
CREATE TRIGGER query_store_group_delta_append_only
    BEFORE UPDATE OR DELETE ON events.query_store_group_delta
    FOR EACH ROW EXECUTE FUNCTION events.reject_query_performance_mutation();

REVOKE ALL ON events.query_store_group_watermark,events.query_store_group_delta
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.ensure_query_store_group_partitions(p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
DECLARE partition_day date; partition_name text; parent_name text;
        parent_oid oid; child_oid oid; created integer := 0;
BEGIN
 IF p_anchor IS NULL OR p_anchor <> (clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'Query Store partition anchor must be current UTC date'
   USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:query-store-group:partition-set',0));
 FOREACH parent_name IN ARRAY ARRAY['query_store_group_watermark','query_store_group_delta'] LOOP
  parent_oid := format('events.%I',parent_name)::regclass;
  FOR partition_day IN SELECT p_anchor+i FROM generate_series(-7,8) AS i LOOP
   partition_name := format('%s_p%s',parent_name,to_char(partition_day,'YYYYMMDD'));
   child_oid := to_regclass(format('events.%I',partition_name));
   IF child_oid IS NULL THEN
    EXECUTE format('CREATE TABLE events.%I PARTITION OF events.%I FOR VALUES FROM (%L) TO (%L)',
      partition_name,parent_name,partition_day::timestamptz,(partition_day+1)::timestamptz);
    created := created + 1;
    child_oid := to_regclass(format('events.%I',partition_name));
   END IF;
   IF NOT EXISTS (SELECT 1 FROM pg_inherits
                  WHERE inhrelid=child_oid AND inhparent=parent_oid) THEN
    RAISE EXCEPTION 'Query Store partition name is occupied by another relation'
     USING ERRCODE='55000';
   END IF;
   EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',partition_name);
  END LOOP;
 END LOOP;
 RETURN created;
END $fn$;
REVOKE ALL ON FUNCTION control.ensure_query_store_group_partitions(date)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_query_store_group_partitions(date)
    TO sqlobserver_collector;
SELECT control.ensure_query_store_group_partitions(current_date);
