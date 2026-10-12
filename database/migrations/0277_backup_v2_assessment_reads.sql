-- Fixed-cutoff backup inventory, policy and time-qualification reads. Activation remains separate.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.get_backups_v2_header(p_instance_id uuid,p_at_utc timestamptz,p_revision bigint DEFAULT NULL)
RETURNS TABLE(run_id uuid,target_revision bigint,source_observed_at timestamptz,completed_at timestamptz,
 received_at timestamptz,source_zone_id text,history_lower_bound_local timestamp,coverage smallint,
 evidence_status text,source_rows integer,output_items integer,retained_items bigint,database_count integer,
 collector_enabled boolean,policy_history_count bigint,qualification_command_revision bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $header$
DECLARE v_revision bigint; v_enabled boolean; v_run record; v_scan record; v_retained bigint; v_status text; v_coverage smallint;
 v_policies bigint; v_qualification bigint;
BEGIN
 IF p_instance_id IS NULL OR p_at_utc IS NULL OR NOT isfinite(p_at_utc)
  OR p_revision<=0 THEN RAISE EXCEPTION 'Backup read bounds are invalid' USING ERRCODE='22023'; END IF;
 IF control.query_performance_target_claim(p_instance_id) IS DISTINCT FROM true
 THEN RAISE EXCEPTION 'Backup target scope is required' USING ERRCODE='42501'; END IF;
 SELECT coalesce(p_revision,target.revision) INTO v_revision FROM control.observation_target target
 WHERE target.instance_id=p_instance_id AND target.lifecycle_state='active'
  AND (p_revision IS NULL OR p_revision<=target.revision AND EXISTS(
   SELECT 1 FROM control.observation_target_revision_identity identity
   WHERE identity.instance_id=p_instance_id AND identity.target_revision=p_revision));
 IF NOT FOUND THEN RAISE EXCEPTION 'Backup target revision is unavailable' USING ERRCODE='42501'; END IF;
 SELECT schedule.enabled INTO v_enabled FROM control.collector_schedule schedule
 WHERE schedule.instance_id=p_instance_id AND schedule.target_revision=v_revision
  AND schedule.collector_id='backups.status' AND schedule.collector_version=2;
 -- Pin late-committing append-only administration as well as the run identity.
 SELECT count(*) INTO v_policies FROM control.backup_freshness_policy policy_row
 WHERE policy_row.instance_id=p_instance_id AND policy_row.target_revision=v_revision AND policy_row.recorded_at<=p_at_utc;
 SELECT coalesce(max(command.command_revision),0) INTO v_qualification FROM control.backup_zone_qualification_command command
 WHERE command.instance_id=p_instance_id AND command.target_revision=v_revision AND command.recorded_at<=p_at_utc;

 -- A failed or empty latest receipt must never fall back to an older success.
 -- Bound partition scans to seven days before the requested monitor cutoff.
 SELECT run.run_id,outcome.completed_at,outcome.outcome,outcome.source_row_count,outcome.output_item_count,
  outcome.loss_kind,outcome.truncated INTO v_run
 FROM telemetry.collection_run_v2 run JOIN telemetry.collection_run_outcome outcome ON outcome.run_id=run.run_id
 WHERE run.instance_id=p_instance_id AND run.target_revision=v_revision AND run.collector_id='backups.status'
  AND run.collector_version=2 AND run.output_schema_version=2
  AND run.started_at BETWEEN p_at_utc-interval '7 days' AND p_at_utc AND outcome.completed_at<=p_at_utc
 ORDER BY outcome.completed_at DESC,run.run_id DESC LIMIT 1;
 IF NOT FOUND THEN
  RETURN QUERY SELECT NULL::uuid,v_revision,NULL::timestamptz,NULL::timestamptz,NULL::timestamptz,
   NULL::text,NULL::timestamp,0::smallint,'not_collected'::text,0,0,0::bigint,0,coalesce(v_enabled,false),v_policies,v_qualification;
  RETURN;
 END IF;
 SELECT scan.* INTO v_scan FROM telemetry.backup_v2_scan scan WHERE scan.instance_id=p_instance_id
  AND scan.target_revision=v_revision AND scan.run_id=v_run.run_id
  AND scan.received_at BETWEEN p_at_utc-interval '7 days' AND p_at_utc;
 SELECT (SELECT count(*) FROM telemetry.backup_v2_database db WHERE db.instance_id=p_instance_id
  AND db.target_revision=v_revision AND db.run_id=v_run.run_id AND db.received_at=v_scan.received_at)
  +(SELECT count(*) FROM telemetry.backup_v2_sample sample WHERE sample.instance_id=p_instance_id
  AND sample.target_revision=v_revision AND sample.run_id=v_run.run_id AND sample.received_at=v_scan.received_at)
 INTO v_retained;
 v_status:=CASE WHEN v_run.outcome NOT IN ('succeeded','partial') THEN 'collection_failed'
  WHEN v_scan.run_id IS NULL OR v_retained<>v_run.output_item_count THEN 'evidence_not_retained'
  WHEN v_run.loss_kind IN ('source_row_limit','response_byte_limit') THEN 'withheld_by_limit'
  WHEN v_run.source_row_count=0 THEN 'no_data' ELSE 'retained' END;
 v_coverage:=CASE WHEN v_run.outcome='permission_denied' THEN 4 WHEN v_run.outcome='unsupported' THEN 6
  WHEN v_run.outcome NOT IN ('succeeded','partial') OR v_status='evidence_not_retained' THEN 5
  ELSE v_scan.coverage END;
 RETURN QUERY SELECT v_run.run_id,v_revision,v_scan.source_observed_at,v_run.completed_at,v_scan.received_at,
  v_scan.source_zone_id,v_scan.history_lower_bound_local,v_coverage,v_status,v_run.source_row_count,
  v_run.output_item_count,v_retained,coalesce(v_scan.database_count,0),coalesce(v_enabled,false),v_policies,v_qualification;
END $header$;
REVOKE ALL ON FUNCTION reporting.get_backups_v2_header(uuid,timestamptz,bigint)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_backups_v2_header(uuid,timestamptz,bigint) TO sqlobserver_server;

CREATE FUNCTION reporting.list_backups_v2_databases(p_instance_id uuid,p_at_utc timestamptz,
 p_revision bigint,p_run_id uuid,p_limit integer,p_after_ordinal integer DEFAULT 0)
RETURNS TABLE(database_ordinal smallint,database_fingerprint bytea,family_fingerprint bytea,
 recovery_fork_fingerprint bytea,group_fingerprint bytea,ag_database_fingerprint bytea,
 local_replica_fingerprint bytea,recovery_model smallint,catalog_database_state text,scope smallint,
 local_replica_role text,local_replica_preferred boolean,unclassified_backup_seen boolean,coverage smallint,
 samples jsonb,policy jsonb)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $page$
DECLARE header record;
BEGIN
 IF p_revision IS NULL OR p_revision<=0 OR p_run_id IS NULL OR p_run_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR p_after_ordinal IS NULL OR p_after_ordinal NOT BETWEEN 0 AND 512
 THEN RAISE EXCEPTION 'Backup page bounds are invalid' USING ERRCODE='22023'; END IF;
 SELECT * INTO header FROM reporting.get_backups_v2_header(p_instance_id,p_at_utc,p_revision);
 IF header.run_id IS DISTINCT FROM p_run_id
 THEN RAISE EXCEPTION 'Backup snapshot changed' USING ERRCODE='40001'; END IF;
 -- Withhold incomplete retained graphs rather than publishing plausible partial verdicts.
 IF header.evidence_status<>'retained' THEN RETURN; END IF;
 RETURN QUERY
 SELECT db.database_ordinal,db.database_fingerprint,db.family_fingerprint,db.recovery_fork_fingerprint,
  db.group_fingerprint,db.ag_database_fingerprint,db.local_replica_fingerprint,db.recovery_model,
  db.catalog_database_state,db.scope,db.local_replica_role,db.local_replica_preferred,
  db.unclassified_backup_seen,db.coverage,
  coalesce((SELECT jsonb_agg(jsonb_build_object(
   'kind',sample.kind,'copyOnly',sample.copy_only,'finishUtc',sample.finish_utc,
   'sourceLocalStart',sample.source_local_start,'sourceLocalFinish',sample.source_local_finish,
   'recordedStartOffsetMinutes',sample.recorded_start_offset_minutes,'sizeBytes',sample.size_bytes,
   'hasChecksum',sample.has_checksum,'isDamaged',sample.is_damaged,'metadataComplete',sample.metadata_complete,
   'firstFork',encode(sample.first_recovery_fork_fingerprint,'hex'),
   'lastFork',encode(sample.last_recovery_fork_fingerprint,'hex'),'timeState',sample.time_state,
   'qualificationRevision',sample.qualification_revision,
   'qualificationTrusted',coalesce(sample.qualification_revision IS NOT NULL
     AND sample.qualification_revision=qualification.qualification_revision,false))
   ORDER BY sample.kind,sample.copy_only)
   FROM telemetry.backup_v2_sample sample WHERE sample.instance_id=p_instance_id AND sample.target_revision=p_revision
    AND sample.received_at=header.received_at AND sample.run_id=p_run_id AND sample.database_ordinal=db.database_ordinal),'[]'::jsonb),
  (SELECT to_jsonb(policy_row) FROM control.read_backup_freshness_policy(p_instance_id,p_revision,db.database_fingerprint,p_at_utc) policy_row)
 FROM telemetry.backup_v2_database db
 LEFT JOIN LATERAL (SELECT command.qualification_revision FROM control.backup_zone_qualification_command command
  WHERE command.instance_id=p_instance_id AND command.target_revision=p_revision AND command.recorded_at<=p_at_utc
  ORDER BY command.command_revision DESC LIMIT 1) qualification ON true
 WHERE db.instance_id=p_instance_id AND db.target_revision=p_revision AND db.received_at=header.received_at
  AND db.run_id=p_run_id AND db.database_ordinal>p_after_ordinal
 ORDER BY db.database_ordinal LIMIT p_limit+1;
END $page$;
REVOKE ALL ON FUNCTION reporting.list_backups_v2_databases(uuid,timestamptz,bigint,uuid,integer,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_backups_v2_databases(uuid,timestamptz,bigint,uuid,integer,integer)
 TO sqlobserver_server;
