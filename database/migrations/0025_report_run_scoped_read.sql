-- Keep runtime table access revoked; expose only one unexpired, target-scoped run.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE FUNCTION reporting.read_report_run(p_target_id uuid, p_run_id uuid)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text,report_kind text,parameter_digest_hex text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
 SELECT run.run_id,run.target_revision,run.definition_version,run.snapshot_utc,
        run.expires_at_utc,run.state,run.report_kind,encode(run.parameter_digest,'hex')
 FROM reporting.report_run AS run
 WHERE p_target_id::text=current_setting('sqlobserver.target_scope',true)
   AND run.target_id=p_target_id AND run.run_id=p_run_id
   AND run.expires_at_utc>clock_timestamp();
$$;
REVOKE ALL ON FUNCTION reporting.read_report_run(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reporting.read_report_run(uuid,uuid) TO sqlobserver_server;
