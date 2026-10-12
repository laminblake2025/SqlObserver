-- Bounded daily SQL-target volume history for capacity planning. Raw paths stay hidden.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE INDEX ix_sql_volume_snapshot_target_key_time
    ON telemetry.sql_volume_snapshot(instance_id, target_revision, volume_key, observed_at DESC);

CREATE FUNCTION reporting.list_sql_volume_capacity_history(
    p_instance_id uuid, p_target_revision bigint, p_snapshot_run_id uuid,
    p_volume_keys bytea[])
RETURNS TABLE(volume_key text, observed_at timestamptz,
    total_bytes bigint, available_bytes bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
DECLARE
    anchor_time timestamptz;
BEGIN
    IF p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision < 1
       OR p_snapshot_run_id IS NULL OR p_volume_keys IS NULL
       OR cardinality(p_volume_keys) NOT BETWEEN 1 AND 100
       OR EXISTS (SELECT 1 FROM unnest(p_volume_keys) AS keys(value)
                  WHERE keys.value IS NULL OR octet_length(keys.value) <> 32)
       OR (SELECT count(DISTINCT keys.value) FROM unnest(p_volume_keys) AS keys(value))
          <> cardinality(p_volume_keys)
    THEN RAISE EXCEPTION 'SQL volume history bounds are invalid' USING ERRCODE='22023'; END IF;
    IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_instance_id::text
    THEN RAISE EXCEPTION 'SQL volume target scope is required' USING ERRCODE='42501'; END IF;
    IF NOT EXISTS (SELECT 1 FROM control.observation_target AS target
                   WHERE target.instance_id=p_instance_id AND target.revision=p_target_revision)
    THEN RAISE EXCEPTION 'SQL volume target revision changed' USING ERRCODE='22023'; END IF;

    SELECT outcome.completed_at INTO anchor_time
    FROM telemetry.collection_run AS run
    JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
    WHERE run.run_id=p_snapshot_run_id AND run.instance_id=p_instance_id
      AND run.target_revision=p_target_revision AND run.collector_id='storage.volume'
      AND outcome.outcome='succeeded' AND outcome.loss_kind='none';
    IF anchor_time IS NULL THEN RETURN; END IF;

    RETURN QUERY
    WITH daily AS (
        SELECT DISTINCT ON (sample.volume_key,
                            (sample.observed_at AT TIME ZONE 'UTC')::date)
               sample.volume_key, sample.observed_at,
               sample.total_bytes, sample.available_bytes
        FROM telemetry.sql_volume_snapshot AS sample
        JOIN telemetry.collection_run AS run ON run.run_id=sample.run_id
        JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
        WHERE sample.instance_id=p_instance_id
          AND sample.target_revision=p_target_revision
          AND sample.volume_key=ANY(p_volume_keys)
          AND sample.observed_at BETWEEN anchor_time-interval '14 days' AND anchor_time
          AND run.instance_id=p_instance_id AND run.target_revision=p_target_revision
          AND run.collector_id='storage.volume'
          AND outcome.outcome='succeeded' AND outcome.loss_kind='none'
          AND sample.total_bytes IS NOT NULL AND sample.available_bytes IS NOT NULL
        ORDER BY sample.volume_key,
                 (sample.observed_at AT TIME ZONE 'UTC')::date,
                 sample.observed_at DESC, sample.run_id DESC
    )
    SELECT encode(daily.volume_key, 'hex'), daily.observed_at,
           daily.total_bytes, daily.available_bytes
    FROM daily
    ORDER BY daily.volume_key, daily.observed_at;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_sql_volume_capacity_history(
    uuid,bigint,uuid,bytea[]) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_sql_volume_capacity_history(
    uuid,bigint,uuid,bytea[]) TO sqlobserver_server;
