-- Derive bounded per-file latency evidence from directional cumulative stalls.
-- Resets, unknown legacy splits, and missing/changed baselines never contribute.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.get_database_file_io_trend(
 p_instance_id uuid,p_database_id integer,p_file_id integer,
 p_from_utc timestamptz,p_to_utc timestamptz)
RETURNS TABLE(bucket_start_utc timestamptz,source_rows bigint,
 comparable_rows bigint,baseline_rows bigint,reset_rows bigint,
 gap_rows bigint,legacy_rows bigint,read_ops numeric,
 read_stall_ms numeric,write_ops numeric,write_stall_ms numeric)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,control,telemetry
SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $file_trend$
DECLARE
 current_revision bigint;
 bounded_rows integer;
BEGIN
 IF p_instance_id IS NULL OR p_database_id NOT BETWEEN 1 AND 32767
    OR p_file_id NOT BETWEEN 1 AND 65535
    OR p_from_utc IS NULL OR p_to_utc IS NULL
    OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
    OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
 THEN RAISE EXCEPTION 'database file trend bounds are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'database file trend target scope is required' USING ERRCODE='42501'; END IF;

 SELECT target.revision INTO current_revision
 FROM control.observation_target AS target WHERE target.instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN; END IF;
 SELECT count(*) INTO bounded_rows FROM (
  SELECT 1 FROM telemetry.database_file_snapshot AS snapshot
  WHERE snapshot.instance_id=p_instance_id
    AND snapshot.target_revision=current_revision
    AND snapshot.database_id=p_database_id AND snapshot.file_id=p_file_id
    AND snapshot.observed_at>=p_from_utc AND snapshot.observed_at<p_to_utc
  LIMIT 20001
 ) AS bounded;
 IF bounded_rows>20000
 THEN RAISE EXCEPTION 'database file trend source cap exceeded' USING ERRCODE='54000'; END IF;

 RETURN QUERY
 WITH predecessor AS MATERIALIZED (
  SELECT snapshot.observed_at,snapshot.snapshot_id,snapshot.logical_name,
         snapshot.read_count,snapshot.write_count,
         snapshot.io_stall_read_ms,snapshot.io_stall_write_ms
  FROM telemetry.database_file_snapshot AS snapshot
  WHERE snapshot.instance_id=p_instance_id
    AND snapshot.target_revision=current_revision
    AND snapshot.database_id=p_database_id AND snapshot.file_id=p_file_id
    AND snapshot.observed_at<p_from_utc
  ORDER BY snapshot.observed_at DESC,snapshot.snapshot_id DESC LIMIT 1
 ), source AS MATERIALIZED (
  SELECT snapshot.observed_at,snapshot.snapshot_id,snapshot.logical_name,
         snapshot.read_count,snapshot.write_count,
         snapshot.io_stall_read_ms,snapshot.io_stall_write_ms
  FROM telemetry.database_file_snapshot AS snapshot
  WHERE snapshot.instance_id=p_instance_id
    AND snapshot.target_revision=current_revision
    AND snapshot.database_id=p_database_id AND snapshot.file_id=p_file_id
    AND snapshot.observed_at>=p_from_utc AND snapshot.observed_at<p_to_utc
 ), ordered AS (
  SELECT rows.*,
         lag(rows.observed_at) OVER scan AS previous_at,
         lag(rows.logical_name) OVER scan AS previous_name,
         lag(rows.read_count) OVER scan AS previous_reads,
         lag(rows.write_count) OVER scan AS previous_writes,
         lag(rows.io_stall_read_ms) OVER scan AS previous_read_stall,
         lag(rows.io_stall_write_ms) OVER scan AS previous_write_stall
  FROM (SELECT * FROM predecessor UNION ALL SELECT * FROM source) AS rows
  WINDOW scan AS (ORDER BY rows.observed_at,rows.snapshot_id)
 ), classified AS (
  SELECT ordered.*,
         CASE
          WHEN ordered.observed_at<p_from_utc THEN 'predecessor'
          WHEN ordered.io_stall_read_ms IS NULL OR ordered.io_stall_write_ms IS NULL
            THEN 'legacy'
          WHEN ordered.previous_at IS NULL THEN 'baseline'
          WHEN ordered.previous_read_stall IS NULL OR ordered.previous_write_stall IS NULL
            THEN 'legacy'
          WHEN ordered.observed_at<=ordered.previous_at
            OR ordered.observed_at-ordered.previous_at>interval '1 hour'
            OR ordered.logical_name IS DISTINCT FROM ordered.previous_name
            THEN 'gap'
          WHEN ordered.read_count<ordered.previous_reads
            OR ordered.write_count<ordered.previous_writes
            OR ordered.io_stall_read_ms<ordered.previous_read_stall
            OR ordered.io_stall_write_ms<ordered.previous_write_stall
            THEN 'reset'
          ELSE 'comparable'
         END AS comparison
  FROM ordered
 )
 SELECT date_bin(interval '1 hour',classified.observed_at,
                 '2000-01-01 00:00:00+00'::timestamptz),
        count(*),count(*) FILTER (WHERE comparison='comparable'),
        count(*) FILTER (WHERE comparison='baseline'),
        count(*) FILTER (WHERE comparison='reset'),
        count(*) FILTER (WHERE comparison='gap'),
        count(*) FILTER (WHERE comparison='legacy'),
        sum((read_count-previous_reads)::numeric)
          FILTER (WHERE comparison='comparable'),
        sum((io_stall_read_ms-previous_read_stall)::numeric)
          FILTER (WHERE comparison='comparable'),
        sum((write_count-previous_writes)::numeric)
          FILTER (WHERE comparison='comparable'),
        sum((io_stall_write_ms-previous_write_stall)::numeric)
          FILTER (WHERE comparison='comparable')
 FROM classified WHERE classified.observed_at>=p_from_utc
 GROUP BY 1 ORDER BY 1 LIMIT 169;
END $file_trend$;

REVOKE ALL ON FUNCTION reporting.get_database_file_io_trend(
 uuid,integer,integer,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_database_file_io_trend(
 uuid,integer,integer,timestamptz,timestamptz)
 TO sqlobserver_server;
