-- After the bounded historical backfill, cut query observations over to their
-- direct target key. The constraint validation scans all rows and fails before
-- any policy changes when a legacy NULL remains; the caller owns the transaction.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE FUNCTION control.finalize_query_observation_identity()
RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, control, events
SET lock_timeout = '5s'
SET statement_timeout = '5min'
AS $fn$
DECLARE
  role_suffix text;
  direct_target_predicate text := $predicate$
    control.query_performance_target_claim(
      nullif(current_setting('sqlobserver.target_scope',true),'')::uuid)
    AND instance_id = nullif(current_setting('sqlobserver.target_scope',true),'')::uuid
  $predicate$;
BEGIN
  IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
    RAISE EXCEPTION 'query observation identity cutover requires migrator login'
      USING ERRCODE = '42501';
  END IF;

  -- Validation is global even though the table uses forced target RLS. If it
  -- fails, PostgreSQL rolls back both the DDL and this function call.
  ALTER TABLE events.query_performance_observation
    VALIDATE CONSTRAINT ck_query_observation_instance_present;
  ALTER TABLE events.query_performance_observation
    ALTER COLUMN instance_id SET NOT NULL;

  FOREACH role_suffix IN ARRAY ARRAY['collector','server','migrator'] LOOP
    EXECUTE format('ALTER POLICY %I ON events.query_performance_observation USING (%s)',
      'query_performance_observation_' || role_suffix,direct_target_predicate);
    IF role_suffix <> 'server' THEN
      EXECUTE format('ALTER POLICY %I ON events.query_performance_observation WITH CHECK (%s)',
        'query_performance_observation_' || role_suffix,direct_target_predicate);
    END IF;
  END LOOP;
  RETURN true;
END;
$fn$;

REVOKE ALL ON FUNCTION control.finalize_query_observation_identity() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.finalize_query_observation_identity()
  TO sqlobserver_migrator;
