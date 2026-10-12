-- Collector-only, fenced reads for application-side backup summary materialization.
-- Server detail-read functions retain their existing private privileges.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.assert_overview_backup_read_target(p_instance_id uuid,p_revision bigint,
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $assert$
BEGIN
 IF p_instance_id IS NULL OR p_revision IS NULL OR p_revision<=0
  OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
  OR NOT EXISTS(SELECT 1 FROM control.observation_target target WHERE target.instance_id=p_instance_id
   AND target.revision=p_revision AND target.lifecycle_state='active')
 THEN RAISE EXCEPTION 'Overview backup target scope or current revision is required' USING ERRCODE='42501'; END IF;
 PERFORM control.assert_worker_lease('overview/latest-status',p_owner_execution_id,p_fencing_token);
END $assert$;
REVOKE ALL ON FUNCTION reporting.assert_overview_backup_read_target(uuid,bigint,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.read_overview_backup_header(p_instance_id uuid,p_revision bigint,
 p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS TABLE(assessed_at timestamptz,run_id uuid,target_revision bigint,source_observed_at timestamptz,
 completed_at timestamptz,received_at timestamptz,source_zone_id text,history_lower_bound_local timestamp,
 coverage smallint,evidence_status text,source_rows integer,output_items integer,retained_items bigint,
 database_count integer,collector_enabled boolean,policy_history_count bigint,qualification_command_revision bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $header$
DECLARE v_at timestamptz;
BEGIN
 PERFORM reporting.assert_overview_backup_read_target(p_instance_id,p_revision,p_owner_execution_id,p_fencing_token);
 v_at:=clock_timestamp();
 RETURN QUERY SELECT v_at,header.* FROM reporting.get_backups_v2_header(p_instance_id,v_at,p_revision) header;
 PERFORM reporting.assert_overview_backup_read_target(p_instance_id,p_revision,p_owner_execution_id,p_fencing_token);
END $header$;
REVOKE ALL ON FUNCTION reporting.read_overview_backup_header(uuid,bigint,uuid,bigint)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_overview_backup_header(uuid,bigint,uuid,bigint) TO sqlobserver_collector;

CREATE FUNCTION reporting.read_overview_backup_databases(p_instance_id uuid,p_revision bigint,p_at_utc timestamptz,
 p_run_id uuid,p_owner_execution_id uuid,p_fencing_token bigint,p_after_ordinal integer)
RETURNS TABLE(database_ordinal smallint,database_fingerprint bytea,family_fingerprint bytea,
 recovery_fork_fingerprint bytea,group_fingerprint bytea,ag_database_fingerprint bytea,
 local_replica_fingerprint bytea,recovery_model smallint,catalog_database_state text,scope smallint,
 local_replica_role text,local_replica_preferred boolean,unclassified_backup_seen boolean,coverage smallint,
 samples jsonb,policy jsonb)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $page$
BEGIN
 PERFORM reporting.assert_overview_backup_read_target(p_instance_id,p_revision,p_owner_execution_id,p_fencing_token);
 IF p_at_utc IS NULL OR NOT isfinite(p_at_utc) OR p_at_utc>clock_timestamp()
  OR p_at_utc<clock_timestamp()-interval '10 seconds'
  OR p_after_ordinal IS NULL OR p_after_ordinal NOT BETWEEN 0 AND 511
 THEN RAISE EXCEPTION 'Overview backup cutoff or page is invalid' USING ERRCODE='22023'; END IF;
 RETURN QUERY SELECT page.* FROM reporting.list_backups_v2_databases(p_instance_id,p_at_utc,p_revision,p_run_id,100,p_after_ordinal) page;
 PERFORM reporting.assert_overview_backup_read_target(p_instance_id,p_revision,p_owner_execution_id,p_fencing_token);
END $page$;
REVOKE ALL ON FUNCTION reporting.read_overview_backup_databases(uuid,bigint,timestamptz,uuid,uuid,bigint,integer)
 FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_overview_backup_databases(uuid,bigint,timestamptz,uuid,uuid,bigint,integer)
 TO sqlobserver_collector;
