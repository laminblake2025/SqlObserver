-- Backfill historical query-observation target keys in bounded, target-scoped
-- transactions. The read paths and RLS policies retain their existing joins.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE TABLE system.query_observation_identity_backfill (
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
    cursor_run_id uuid,
    cursor_database_id integer,
    cursor_query_fingerprint bytea,
    cursor_observation_key bytea,
    processed_rows bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz,
    CONSTRAINT ck_query_identity_backfill_cursor CHECK (
        (cursor_run_id IS NULL AND cursor_database_id IS NULL AND
         cursor_query_fingerprint IS NULL AND cursor_observation_key IS NULL) OR
        (cursor_run_id IS NOT NULL AND cursor_database_id IS NOT NULL AND
         cursor_query_fingerprint IS NOT NULL AND cursor_observation_key IS NOT NULL)
    ),
    CONSTRAINT ck_query_identity_backfill_count CHECK (processed_rows >= 0)
);
REVOKE ALL ON system.query_observation_identity_backfill FROM PUBLIC;

CREATE OR REPLACE FUNCTION events.reject_query_performance_mutation() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, events AS $fn$
BEGIN
  IF TG_OP = 'UPDATE' AND TG_TABLE_SCHEMA = 'events' AND
     TG_TABLE_NAME = 'query_performance_observation' AND
     current_setting('sqlobserver.query_identity_backfill', true) = 'enabled' AND
     pg_has_role(session_user,'sqlobserver_migrator','member') AND
     OLD.instance_id IS NULL AND NEW.instance_id IS NOT NULL AND
     NEW.instance_id = nullif(current_setting('sqlobserver.target_scope', true),'')::uuid AND
     (to_jsonb(NEW) - 'instance_id') = (to_jsonb(OLD) - 'instance_id') THEN
    RETURN NEW;
  END IF;
  IF TG_OP IN ('UPDATE','DELETE') AND
     current_setting('sqlobserver.retention_context', true) = 'envelope_cascade' AND
     pg_has_role(current_user,'sqlobserver_migrator','member') THEN RETURN OLD; END IF;
  RAISE EXCEPTION 'query performance evidence is append-only' USING ERRCODE = '55000';
END;
$fn$;
REVOKE ALL ON FUNCTION events.reject_query_performance_mutation() FROM PUBLIC;

CREATE FUNCTION control.backfill_query_observation_identity(p_instance_id uuid, p_limit integer)
RETURNS TABLE(updated_rows integer, complete boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, control, events, system
SET row_security = on AS $fn$
DECLARE
  state_row system.query_observation_identity_backfill%ROWTYPE;
  last_run uuid;
  last_database integer;
  last_query bytea;
  last_observation bytea;
  changed_rows integer;
BEGIN
  IF p_instance_id IS NULL OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN
    RAISE EXCEPTION 'query observation backfill bounds invalid' USING ERRCODE = '22023';
  END IF;
  IF NOT control.query_performance_target_claim(p_instance_id) THEN
    RAISE EXCEPTION 'query observation backfill target scope missing' USING ERRCODE = '42501';
  END IF;

  INSERT INTO system.query_observation_identity_backfill(instance_id)
  VALUES(p_instance_id) ON CONFLICT DO NOTHING;
  SELECT * INTO state_row FROM system.query_observation_identity_backfill
  WHERE instance_id = p_instance_id FOR UPDATE;
  IF state_row.completed_at IS NOT NULL THEN
    RETURN QUERY SELECT 0, true;
    RETURN;
  END IF;

  SELECT next_row.collection_run_id,next_row.database_id,
         next_row.query_fingerprint,next_row.observation_key
  INTO last_run,last_database,last_query,last_observation
  FROM (
    SELECT o.collection_run_id,o.database_id,o.query_fingerprint,o.observation_key
    FROM events.query_performance_query AS q
    JOIN events.query_performance_observation AS o
      USING(collection_run_id,database_id,query_fingerprint)
    WHERE q.instance_id = p_instance_id AND o.instance_id IS NULL
      AND (state_row.cursor_run_id IS NULL OR
           (o.collection_run_id,o.database_id,o.query_fingerprint,o.observation_key) >
           (state_row.cursor_run_id,state_row.cursor_database_id,
            state_row.cursor_query_fingerprint,state_row.cursor_observation_key))
    ORDER BY o.collection_run_id,o.database_id,o.query_fingerprint,o.observation_key
    LIMIT p_limit
  ) AS next_row
  ORDER BY next_row.collection_run_id DESC,next_row.database_id DESC,
           next_row.query_fingerprint DESC,next_row.observation_key DESC
  LIMIT 1;

  IF last_run IS NULL THEN
    UPDATE system.query_observation_identity_backfill
    SET completed_at = clock_timestamp(),updated_at = clock_timestamp()
    WHERE instance_id = p_instance_id;
    RETURN QUERY SELECT 0, true;
    RETURN;
  END IF;

  PERFORM set_config('sqlobserver.query_identity_backfill','enabled',true);
  UPDATE events.query_performance_observation AS o
  SET instance_id = p_instance_id
  FROM events.query_performance_query AS q
  WHERE q.collection_run_id = o.collection_run_id
    AND q.database_id = o.database_id
    AND q.query_fingerprint = o.query_fingerprint
    AND q.instance_id = p_instance_id AND o.instance_id IS NULL
    AND (state_row.cursor_run_id IS NULL OR
         (o.collection_run_id,o.database_id,o.query_fingerprint,o.observation_key) >
         (state_row.cursor_run_id,state_row.cursor_database_id,
          state_row.cursor_query_fingerprint,state_row.cursor_observation_key))
    AND (o.collection_run_id,o.database_id,o.query_fingerprint,o.observation_key) <=
        (last_run,last_database,last_query,last_observation);
  GET DIAGNOSTICS changed_rows = ROW_COUNT;
  PERFORM set_config('sqlobserver.query_identity_backfill','',true);
  IF changed_rows > p_limit THEN
    RAISE EXCEPTION 'query observation backfill exceeded row bound' USING ERRCODE = '22023';
  END IF;

  UPDATE system.query_observation_identity_backfill
  SET cursor_run_id = last_run,cursor_database_id = last_database,
      cursor_query_fingerprint = last_query,cursor_observation_key = last_observation,
      processed_rows = processed_rows + changed_rows,updated_at = clock_timestamp()
  WHERE instance_id = p_instance_id;
  RETURN QUERY SELECT changed_rows, false;
END;
$fn$;
REVOKE ALL ON FUNCTION control.backfill_query_observation_identity(uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.backfill_query_observation_identity(uuid,integer) TO sqlobserver_migrator;
