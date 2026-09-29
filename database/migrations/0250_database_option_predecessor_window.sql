-- Preserve the immediately preceding inventory run regardless of its age.
-- A fixed lookback silently missed a change after a long collection pause.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION reporting.list_database_option_changes(
    p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
    p_max_results integer)
RETURNS TABLE(database_id integer,database_name text,option_key text,
    previous_value text,current_value text,previous_observed_at timestamptz,
    observed_at timestamptz,detected_at timestamptz,run_id uuid)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
BEGIN
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
       OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 200
    THEN RAISE EXCEPTION 'Database option change bounds are invalid' USING ERRCODE='22023'; END IF;
    IF NOT control.query_performance_target_claim(p_instance_id)
    THEN RAISE EXCEPTION 'Database option target scope is required' USING ERRCODE='42501'; END IF;

    RETURN QUERY
    WITH prior_run AS MATERIALIZED (
        SELECT run.run_id,run.target_revision,outcome.completed_at AS detected,
               outcome.outcome='succeeded' AND NOT outcome.loss_detected
                   AND NOT outcome.truncated AS complete
        FROM telemetry.collection_run AS run
        JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
        JOIN control.observation_target AS target
          ON target.instance_id=run.instance_id
         AND target.revision=run.target_revision
         AND target.lifecycle_state='active'
        WHERE run.instance_id=p_instance_id
          AND run.collector_id='database.inventory'
          AND outcome.completed_at<p_from_utc
        ORDER BY outcome.completed_at DESC,run.run_id DESC
        LIMIT 1
    ), window_runs AS MATERIALIZED (
        SELECT run.run_id,run.target_revision,outcome.completed_at AS detected,
               outcome.outcome='succeeded' AND NOT outcome.loss_detected
                   AND NOT outcome.truncated AS complete
        FROM telemetry.collection_run AS run
        JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
        JOIN control.observation_target AS target
          ON target.instance_id=run.instance_id
         AND target.revision=run.target_revision
         AND target.lifecycle_state='active'
        WHERE run.instance_id=p_instance_id
          AND run.collector_id='database.inventory'
          AND outcome.completed_at>=p_from_utc
          AND outcome.completed_at<p_to_utc
    ), runs AS MATERIALIZED (
        SELECT selected.run_id,selected.target_revision,selected.detected,
               selected.complete,
               row_number() OVER (ORDER BY selected.detected,selected.run_id) AS run_ordinal
        FROM (SELECT * FROM prior_run UNION ALL SELECT * FROM window_runs) AS selected
    ), source AS MATERIALIZED (
        SELECT runs.run_id,runs.run_ordinal,runs.detected,
               snapshot.database_id,snapshot.database_name,
               snapshot.is_read_only,snapshot.recovery_model,
               snapshot.user_access,snapshot.compatibility_level,
               snapshot.observed_at
        FROM runs
        JOIN telemetry.database_inventory_snapshot AS snapshot
          ON snapshot.collection_run_id=runs.run_id
         AND snapshot.instance_id=p_instance_id
         AND snapshot.target_revision=runs.target_revision
        WHERE runs.complete AND snapshot.database_id>4
    ), paired AS (
        SELECT source.*,
               lag(source.run_ordinal) OVER sequence AS previous_ordinal,
               lag(source.database_name) OVER sequence AS previous_name,
               lag(source.is_read_only) OVER sequence AS previous_read_only,
               lag(source.recovery_model) OVER sequence AS previous_recovery,
               lag(source.user_access) OVER sequence AS previous_access,
               lag(source.compatibility_level) OVER sequence AS previous_compatibility,
               lag(source.observed_at) OVER sequence AS previous_observed
        FROM source
        WINDOW sequence AS (PARTITION BY source.database_id ORDER BY source.run_ordinal)
    )
    SELECT paired.database_id,paired.database_name,option_change.option_key,
           option_change.previous_value,option_change.current_value,
           paired.previous_observed,paired.observed_at,paired.detected,paired.run_id
    FROM paired
    CROSS JOIN LATERAL (VALUES
        ('recovery_model',paired.previous_recovery,paired.recovery_model),
        ('user_access',paired.previous_access,paired.user_access),
        ('is_read_only',paired.previous_read_only::text,paired.is_read_only::text),
        ('compatibility_level',paired.previous_compatibility::text,
                               paired.compatibility_level::text)
    ) AS option_change(option_key,previous_value,current_value)
    WHERE paired.previous_ordinal=paired.run_ordinal-1
      AND paired.previous_name=paired.database_name
      AND option_change.previous_value IS DISTINCT FROM option_change.current_value
      AND paired.detected>=p_from_utc
    ORDER BY paired.detected DESC,paired.run_id DESC,
             paired.database_id,option_change.option_key
    LIMIT p_max_results;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_database_option_changes(
    uuid,timestamptz,timestamptz,integer)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_database_option_changes(
    uuid,timestamptz,timestamptz,integer) TO sqlobserver_server;
