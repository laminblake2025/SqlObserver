-- Explicit historical configuration evidence; no qualification from a current zone ID.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE control.backup_zone_qualification_command
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 command_revision bigint NOT NULL CHECK(command_revision>0),
 qualification_revision bigint,
 operation_id uuid NOT NULL UNIQUE CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
 request_sha256 bytea NOT NULL CHECK(octet_length(request_sha256)=32),
 evidence_sha256 bytea CHECK(octet_length(evidence_sha256)=32),
 -- Keep the atomic audit receipt without preventing independent audit retention.
 audit_activity_id uuid NOT NULL,
 audit_occurred_at timestamptz NOT NULL CHECK(isfinite(audit_occurred_at)),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,target_revision,command_revision),
 FOREIGN KEY(instance_id,target_revision,qualification_revision)
  REFERENCES control.backup_historical_zone_qualification(instance_id,target_revision,qualification_revision),
 CHECK((qualification_revision IS NULL AND evidence_sha256 IS NULL)
   OR (qualification_revision IS NOT NULL AND qualification_revision=command_revision AND evidence_sha256 IS NOT NULL))
);
ALTER TABLE control.backup_zone_qualification_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.backup_zone_qualification_command FORCE ROW LEVEL SECURITY;
CREATE POLICY backup_zone_qualification_command_owner ON control.backup_zone_qualification_command
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER backup_zone_qualification_command_append_only BEFORE UPDATE OR DELETE
 ON control.backup_zone_qualification_command FOR EACH STATEMENT
 EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON control.backup_zone_qualification_command FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.set_backup_zone_qualification(
 p_instance uuid,p_target_revision bigint,p_expected_revision bigint,p_operation uuid,
 p_source_zone text,p_rules text,p_from timestamptz,p_to timestamptz,p_evidence bytea,
 p_actor text,p_correlation uuid)
RETURNS TABLE(result_status text,command_revision bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $command$
DECLARE target control.observation_target%ROWTYPE; current_revision bigint; next_revision bigint;
 digest bytea; replay control.backup_zone_qualification_command%ROWTYPE; audit_id uuid; audit_at timestamptz;
BEGIN
 IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000'
   OR p_target_revision IS NULL OR p_target_revision<1 OR p_expected_revision IS NULL
   OR p_expected_revision<0 OR p_expected_revision=9223372036854775807
   OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000'
   OR p_actor IS NULL OR p_correlation IS NULL
   OR NOT ((p_rules IS NULL AND p_source_zone IS NULL AND p_from IS NULL AND p_to IS NULL AND p_evidence IS NULL)
    OR (p_rules IS NOT NULL AND octet_length(p_rules) BETWEEN 1 AND 65536
     AND p_source_zone IS NOT NULL AND length(p_source_zone) BETWEEN 1 AND 128 AND p_source_zone !~ '[[:cntrl:]]'
     AND p_from IS NOT NULL AND isfinite(p_from) AND p_to IS NOT NULL AND isfinite(p_to) AND p_from<p_to
     AND p_evidence IS NOT NULL AND octet_length(p_evidence)=32))
 THEN RAISE EXCEPTION 'Backup zone qualification request is invalid' USING ERRCODE='22023'; END IF;

 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:backup-zone-operation:'||p_operation::text,0));
 -- The v2 commit wrapper takes this lock before its run/schedule locks too.
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:backup-zone-target:'||p_instance::text,0));
 SELECT * INTO target FROM control.observation_target WHERE instance_id=p_instance FOR UPDATE;
 IF NOT FOUND THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','failed','target_not_found');
  RETURN QUERY SELECT 'not_found'::text,NULL::bigint; RETURN;
 END IF;
 IF target.lifecycle_state<>'active' THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict',
   CASE WHEN target.lifecycle_state='retired' THEN 'target_retired' ELSE 'revision_conflict' END);
  RETURN QUERY SELECT 'inactive'::text,NULL::bigint; RETURN;
 END IF;
 IF target.revision<>p_target_revision THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict','revision_conflict');
  RETURN QUERY SELECT 'revision_conflict'::text,NULL::bigint; RETURN;
 END IF;
 SELECT coalesce(max(command.command_revision),0) INTO current_revision
 FROM control.backup_zone_qualification_command command WHERE instance_id=p_instance AND target_revision=p_target_revision;
 digest:=sha256(convert_to(jsonb_build_array(p_instance,p_target_revision,p_expected_revision,p_operation,
  p_source_zone,p_rules,p_from,p_to,encode(p_evidence,'hex'),p_actor)::text,'UTF8'));
 SELECT * INTO replay FROM control.backup_zone_qualification_command WHERE operation_id=p_operation;
 IF FOUND THEN
  IF replay.request_sha256=digest AND replay.instance_id=p_instance AND replay.target_revision=p_target_revision
    AND replay.command_revision=current_revision THEN
   RETURN QUERY SELECT 'replayed'::text,replay.command_revision; RETURN;
  END IF;
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict','revision_conflict');
  RETURN QUERY SELECT 'revision_conflict'::text,current_revision; RETURN;
 END IF;
 IF current_revision<>p_expected_revision THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict','revision_conflict');
  RETURN QUERY SELECT 'revision_conflict'::text,current_revision; RETURN;
 END IF;
 -- Existing owner-provisioned certificates are not implicitly trusted by this command/provider.
 SELECT greatest(current_revision,coalesce(max(qualification_revision),0))+1 INTO next_revision
 FROM control.backup_historical_zone_qualification WHERE instance_id=p_instance AND target_revision=p_target_revision;
 IF p_rules IS NOT NULL THEN
  INSERT INTO control.backup_historical_zone_qualification(instance_id,target_revision,qualification_revision,
   source_zone_id,serialized_zone_rules,rules_sha256,qualified_from,qualified_to)
  VALUES(p_instance,p_target_revision,next_revision,p_source_zone,p_rules,sha256(convert_to(p_rules,'UTF8')),p_from,p_to);
 END IF;
 SELECT audit_activity_id,repository_time INTO audit_id,audit_at FROM audit.append_administrative_activity(
  p_actor,p_correlation,'update_observation_target',p_instance,'granted','succeeded','completed');
 INSERT INTO control.backup_zone_qualification_command(instance_id,target_revision,command_revision,
  qualification_revision,operation_id,request_sha256,evidence_sha256,audit_activity_id,audit_occurred_at)
 VALUES(p_instance,p_target_revision,next_revision,CASE WHEN p_rules IS NULL THEN NULL ELSE next_revision END,
  p_operation,digest,p_evidence,audit_id,audit_at);
 RETURN QUERY SELECT 'applied'::text,next_revision;
END;
$command$;
REVOKE ALL ON FUNCTION control.set_backup_zone_qualification(uuid,bigint,bigint,uuid,text,text,timestamptz,timestamptz,bytea,text,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_backup_zone_qualification(uuid,bigint,bigint,uuid,text,text,timestamptz,timestamptz,bytea,text,uuid)
 TO sqlobserver_server;

CREATE FUNCTION control.read_backup_zone_qualification(p_instance uuid,p_target_revision bigint)
RETURNS TABLE(command_revision bigint,source_zone_id text,serialized_zone_rules text,rules_sha256 bytea,
 qualified_from timestamptz,qualified_to timestamptz,evidence_sha256 bytea)
LANGUAGE sql SECURITY DEFINER STABLE PARALLEL UNSAFE SET search_path=pg_catalog AS $read$
 SELECT command.command_revision,qualification.source_zone_id,qualification.serialized_zone_rules,
  qualification.rules_sha256,qualification.qualified_from,qualification.qualified_to,command.evidence_sha256
 FROM control.observation_target target
 JOIN LATERAL(SELECT * FROM control.backup_zone_qualification_command item
   WHERE item.instance_id=target.instance_id AND item.target_revision=target.revision
   ORDER BY item.command_revision DESC LIMIT 1) command ON true
 LEFT JOIN control.backup_historical_zone_qualification qualification
  ON qualification.instance_id=command.instance_id AND qualification.target_revision=command.target_revision
   AND qualification.qualification_revision=command.qualification_revision
 WHERE target.instance_id=p_instance AND target.revision=p_target_revision AND target.lifecycle_state='active';
$read$;
REVOKE ALL ON FUNCTION control.read_backup_zone_qualification(uuid,bigint) FROM PUBLIC,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.read_backup_zone_qualification(uuid,bigint) TO sqlobserver_server,sqlobserver_collector;

CREATE FUNCTION control.require_current_backup_zone_qualification()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog AS $fence$
DECLARE current_qualification bigint;
BEGIN
 IF NEW.qualification_revision IS NOT NULL THEN
  SELECT command.qualification_revision INTO current_qualification
  FROM control.backup_zone_qualification_command command
  WHERE command.instance_id=NEW.instance_id AND command.target_revision=NEW.target_revision
  ORDER BY command.command_revision DESC LIMIT 1;
  IF current_qualification IS DISTINCT FROM NEW.qualification_revision THEN
   RAISE EXCEPTION 'Backup historical qualification was revoked, replaced, or not explicitly provisioned' USING ERRCODE='22023';
  END IF;
 END IF;
 RETURN NEW;
END;
$fence$;
REVOKE ALL ON FUNCTION control.require_current_backup_zone_qualification()
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE TRIGGER backup_v2_sample_current_qualification BEFORE INSERT ON telemetry.backup_v2_sample
 FOR EACH ROW EXECUTE FUNCTION control.require_current_backup_zone_qualification();

ALTER FUNCTION control.commit_backups_v2_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) RENAME TO commit_backups_v2_collection_run_core;

CREATE FUNCTION control.commit_backups_v2_collection_run(
 p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_id text,
 p_collector_version integer,p_output_schema_version integer,p_schedule_revision bigint,
 p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
 p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,
 p_attempt_count integer,p_source_row_count integer,p_output_item_count integer,
 p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
 p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,
 p_next_consecutive_failures integer,p_payload jsonb,p_completion_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,
 rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $commit$
BEGIN
 -- Serialize qualification replacement/revocation before any schedule/lease row
 -- is locked. The insert trigger protects new samples; replay inserts nothing.
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:backup-zone-target:'||p_instance_id::text,0));
 RETURN QUERY SELECT * FROM control.commit_backups_v2_collection_run_core(
  p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,p_output_schema_version,
  p_schedule_revision,p_scheduled_at,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,
  p_outcome,p_reason_code,p_duration_ms,p_attempt_count,p_source_row_count,p_output_item_count,
  p_response_bytes,p_output_bytes,p_loss_kind,p_minimum_lost_items,p_loss_count_is_exact,
  p_minimum_lost_bytes,p_next_circuit_state,p_next_consecutive_failures,p_payload,p_completion_digest);
END;
$commit$;
REVOKE ALL ON FUNCTION control.commit_backups_v2_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
