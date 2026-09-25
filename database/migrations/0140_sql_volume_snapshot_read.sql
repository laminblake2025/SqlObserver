-- Bounded SQL-reported volume capacity reads for one authorized target.
-- A cursor fixes the run and target revision; a newer run cannot change the
-- rows between pages. All returned identities are keyed hashes, never paths.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE INDEX ix_sql_volume_snapshot_run_key
    ON telemetry.sql_volume_snapshot(instance_id,run_id,volume_key);

CREATE FUNCTION reporting.list_sql_volume_snapshot(
 p_instance_id uuid,p_snapshot_run_id uuid,p_snapshot_target_revision bigint,
 p_after_volume_key bytea,p_max_results integer)
RETURNS TABLE(
 target_revision bigint,snapshot_run_id uuid,evidence_state text,evidence_reason text,
 completed_at timestamptz,loss_kind text,minimum_lost_items bigint,
 volume_key text,identity_kind text,mapped_file_count integer,
 total_bytes bigint,available_bytes bigint,observed_at timestamptz,
 has_more boolean,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
DECLARE
 current_revision bigint;
 selected_run uuid;
 selected_outcome text;
 selected_reason text;
 selected_completed timestamptz;
 selected_loss text;
 selected_lost_items bigint;
 selected_inserted integer;
 latest_run uuid;
 collection_interval interval;
 stored_rows bigint;
 state text;
 reason text;
 now_utc timestamptz := clock_timestamp();
BEGIN
 IF p_instance_id IS NULL OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
    OR ((p_snapshot_run_id IS NULL) <> (p_snapshot_target_revision IS NULL))
    OR ((p_snapshot_run_id IS NULL) <> (p_after_volume_key IS NULL))
    OR (p_snapshot_target_revision IS NOT NULL AND p_snapshot_target_revision <= 0)
    OR (p_after_volume_key IS NOT NULL AND octet_length(p_after_volume_key) <> 32)
 THEN RAISE EXCEPTION 'SQL volume page bounds or cursor are invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'SQL volume target scope is required' USING ERRCODE='42501'; END IF;

 SELECT target.revision INTO current_revision
 FROM control.observation_target AS target WHERE target.instance_id=p_instance_id;
 IF NOT FOUND THEN RETURN; END IF;
 IF p_snapshot_target_revision IS NOT NULL AND p_snapshot_target_revision<>current_revision
 THEN RAISE EXCEPTION 'SQL volume cursor target revision changed' USING ERRCODE='22023'; END IF;

 SELECT schedule.collection_interval INTO collection_interval
 FROM control.collector_schedule AS schedule
 WHERE schedule.instance_id=p_instance_id AND schedule.collector_id='storage.volume';
 SELECT run.run_id INTO latest_run
 FROM telemetry.collection_run AS run
 JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
 WHERE run.instance_id=p_instance_id AND run.target_revision=current_revision
   AND run.collector_id='storage.volume'
 ORDER BY run.started_at DESC,run.run_id DESC LIMIT 1;

 IF p_snapshot_run_id IS NULL THEN
  selected_run := latest_run;
 ELSE
  selected_run := p_snapshot_run_id;
 END IF;
 IF selected_run IS NOT NULL THEN
  SELECT outcome.outcome,outcome.reason_code,outcome.completed_at,
         outcome.loss_kind,outcome.lost_row_count,outcome.inserted_item_count
    INTO selected_outcome,selected_reason,selected_completed,
         selected_loss,selected_lost_items,selected_inserted
  FROM telemetry.collection_run AS run
  JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
  WHERE run.run_id=selected_run AND run.instance_id=p_instance_id
    AND run.target_revision=current_revision AND run.collector_id='storage.volume';
  IF NOT FOUND THEN
   RAISE EXCEPTION 'SQL volume cursor snapshot is unavailable' USING ERRCODE='22023';
  END IF;
  IF p_snapshot_run_id IS NOT NULL AND selected_outcome NOT IN ('succeeded','partial') THEN
   RAISE EXCEPTION 'SQL volume cursor snapshot has no readable rows' USING ERRCODE='22023';
  END IF;
 END IF;

 IF selected_outcome IN ('succeeded','partial') THEN
  SELECT count(*) INTO stored_rows FROM telemetry.sql_volume_snapshot AS volume
  WHERE volume.instance_id=p_instance_id AND volume.target_revision=current_revision
    AND volume.run_id=selected_run;
  IF stored_rows<>selected_inserted THEN
   IF p_snapshot_run_id IS NOT NULL THEN
    RAISE EXCEPTION 'SQL volume cursor snapshot evidence is incomplete' USING ERRCODE='22023';
   END IF;
   selected_outcome := 'expired';
   selected_reason := 'evidence_expired';
  END IF;
 END IF;

 state := CASE
  WHEN selected_run IS NULL THEN 'unavailable'
  WHEN selected_outcome NOT IN ('succeeded','partial') THEN 'unavailable'
  WHEN selected_run<>latest_run THEN 'superseded'
  WHEN selected_outcome='partial' OR selected_loss<>'none' THEN 'partial'
  WHEN collection_interval IS NULL OR now_utc-selected_completed > greatest(interval '5 minutes',collection_interval*2)
    THEN 'stale'
  ELSE 'current' END;
 reason := CASE
  WHEN selected_run IS NULL THEN 'not_collected'
  WHEN state='superseded' THEN 'snapshot_superseded'
  WHEN state='stale' THEN 'sample_stale'
  ELSE selected_reason END;

 RETURN QUERY
 WITH page AS MATERIALIZED (
  SELECT volume.volume_key,volume.identity_kind,volume.mapped_file_count,
         volume.total_bytes,volume.available_bytes,volume.observed_at
  FROM telemetry.sql_volume_snapshot AS volume
  WHERE selected_outcome IN ('succeeded','partial')
    AND volume.instance_id=p_instance_id AND volume.target_revision=current_revision
    AND volume.run_id=selected_run
    AND (p_after_volume_key IS NULL OR volume.volume_key>p_after_volume_key)
  ORDER BY volume.volume_key LIMIT p_max_results+1
 )
 SELECT current_revision,selected_run,state,reason,selected_completed,
        selected_loss,selected_lost_items,
        encode(page.volume_key,'hex'),page.identity_kind,page.mapped_file_count,
        page.total_bytes,page.available_bytes,page.observed_at,
        (SELECT count(*)>p_max_results FROM page),now_utc
 FROM (SELECT 1) AS header LEFT JOIN page ON true
 ORDER BY page.volume_key NULLS LAST LIMIT p_max_results;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_sql_volume_snapshot(
 uuid,uuid,bigint,bytea,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_sql_volume_snapshot(
 uuid,uuid,bigint,bytea,integer) TO sqlobserver_server;
