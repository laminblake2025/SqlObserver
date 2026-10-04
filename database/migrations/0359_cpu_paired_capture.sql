-- Staged private CPU receipts. No source registration, schedule activation or metric admission.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.cpu_paired_capture_receipt
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 completed_at timestamptz NOT NULL CHECK(isfinite(completed_at)),
 source_schema_version integer NOT NULL CHECK(source_schema_version=1),
 outcome text NOT NULL,
 loss_kind text NOT NULL,
 has_capture boolean NOT NULL,
 native_qualification text NOT NULL DEFAULT 'absent' CHECK(native_qualification='absent'),
 target_comparison_admitted boolean NOT NULL DEFAULT false CHECK(NOT target_comparison_admitted),
 sql_process_percent numeric CHECK(sql_process_percent IS NULL),
 operating_system_percent numeric CHECK(operating_system_percent IS NULL),
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object' AND octet_length(payload::text)<=70000),
 completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32),
 PRIMARY KEY(instance_id,completed_at,run_id)
) PARTITION BY RANGE(completed_at);
CREATE INDEX ix_cpu_paired_capture_run ON telemetry.cpu_paired_capture_receipt(run_id);
CREATE INDEX ix_cpu_paired_capture_target_time ON telemetry.cpu_paired_capture_receipt(instance_id,completed_at DESC,run_id);
COMMENT ON TABLE telemetry.cpu_paired_capture_receipt IS
 'Private source-derived diagnostic candidates only. Discarded TCP endpoint matching is not independently replayable here. No target metric admission, public percentage, hardware identity certification or production source activation.';
ALTER TABLE telemetry.cpu_paired_capture_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.cpu_paired_capture_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY cpu_paired_capture_scope ON telemetry.cpu_paired_capture_receipt
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
 WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY cpu_paired_capture_migrator ON telemetry.cpu_paired_capture_receipt
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER cpu_paired_capture_append_only BEFORE UPDATE OR DELETE ON telemetry.cpu_paired_capture_receipt
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON telemetry.cpu_paired_capture_receipt FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.cpu_paired_capture_receipt TO sqlobserver_run_dependency_reader;

-- Recursive closed projection validation. Failed native counters are retained as
-- evidence; their status is not silently replaced with a successful value.
CREATE FUNCTION control.validate_cpu_capture_json(p_value jsonb,p_shape text,p_target uuid,p_revision bigint,p_run uuid)
RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog SET TimeZone='UTC'
AS $validate$
DECLARE v_spec jsonb; v_field record; v_value jsonb; v_type text; v_nullable boolean; v_n numeric; v_row jsonb;
BEGIN
 v_spec:=CASE p_shape
 WHEN 'capture' THEN '{"sourceSchemaVersion":"one","targetId":"target","targetRevision":"revision","runId":"run","hostBindingRevision":"positive64?","hostProfileRevision":"positive64?","state":"state","reason":"gap","candidateReason":"candidate","candidateLocalAssociation":"boolean","nativeQualification":"zero","targetComparisonAdmitted":"false","hardwareIdentityVerified":"false","sqlProcessPercent":"null","operatingSystemPercent":"null","sqlBefore":"sql?","sqlAfter":"sql?","tcpBefore":"tcp?","tcpAfter":"tcp?","cpuBefore":"cpu?","cpuAfter":"cpu?","idProcessBefore":"counter?","idProcessAfter":"counter?","frameBefore":"frame?","frameAfter":"frame?","executableFingerprintBefore":"fingerprint?","executableFingerprintAfter":"fingerprint?","processStatusBefore":"uint","processStatusAfter":"uint","osStatusBefore":"uint","osStatusAfter":"uint","numericalEligibility":"eligibility?","bytes":"bytes","tcpBeforeReadReason":"readreason?","cpuPairReadReason":"readreason?","tcpAfterReadReason":"readreason?","processReadReason":"readreason?"}'::jsonb
 WHEN 'sql' THEN '{"targetId":"target","targetRevision":"revision","runId":"run","connectionSessionId":"uuid","clientConnectionId":"uuid","ordinal":"ordinal","sourceObservedAtUtc":"utc","engine":"engine","physicalTransport":"transport","processUserTimeMs":"nonnegative64?","processKernelTimeMs":"nonnegative64?","sqlReportedLogicalProcessors":"positive32?","sourceSchemaVersion":"sqlschema","serverConnectionId":"uuid?","serverSessionId":"session?","collectorProcessId":"positive32?","socketCompleteness":"completeness?"}'::jsonb
 WHEN 'engine' THEN '{"physicalNodeFingerprint":"fingerprint?","logicalInstanceFingerprint":"fingerprint?","processId":"positive32?","sourceStartLocal":"local?","startMsTicks":"nonnegative64?","currentMsTicks":"nonnegative64?","startupIncarnationFingerprint":"fingerprint?","coverage":"coverage","hardwareIdentityVerified":"false","occurredAtUtc":"null"}'::jsonb
 WHEN 'binding' THEN '{"targetId":"target","targetRevision":"revision","runId":"run","expectedHostFingerprint":"fingerprint?","sqlProcessId":"positive32?","sqlProcessWitness":"zero"}'::jsonb
 WHEN 'host' THEN '{"actualHostFingerprint":"fingerprint?","machineFingerprint":"fingerprint?","bootFileTime100ns":"positive64?","witness":"hostwitness","hardwareIdentityVerified":"false"}'::jsonb
 WHEN 'process' THEN '{"creationBefore100ns":"positive64?","creationAfter100ns":"positive64?","exitAfter100ns":"int64?","apiStatus":"uint","waitResultAfter":"uint?"}'::jsonb
 WHEN 'tcp' THEN '{"binding":"binding","observedAtUtc":"utc","monotonic100ns":"nonnegative64","host":"host","sqlProcess":"process","collectorProcess":"process","af4Status":"uint","af6Status":"uint","rowCount4":"rows","rowCount6":"rows","matchingRows":"sockets"}'::jsonb
 WHEN 'socket' THEN '{"direction":"direction","state":"uint","ownerProcessId":"uint"}'::jsonb
 WHEN 'cpu' THEN '{"binding":"binding","queryId":"uuid","observedAtUtc":"utc","monotonicTicks":"nonnegative64","monotonicFrequency":"positive64","host":"host","sqlProcessCreationFileTime100ns":"positive64?","logicalProcessorCount":"positiveuint?","processCounter":"counter?","osCounter":"counter?"}'::jsonb
 WHEN 'counter' THEN '{"counterType":"uint","cStatus":"uint","firstValue":"int64","secondValue":"int64","collectedLocalFileTime100ns":"int64","multiCount":"uint","apiStatus":"uint","timeBase":"int64"}'::jsonb
 WHEN 'frame' THEN '{"processCreationBefore100ns":"positive64?","processCreationAfter100ns":"positive64?","processExitAfter100ns":"int64?","logicalProcessorsBefore":"uint","logicalProcessorsAfter":"uint","processInstances":"nonnegative32","processMappingAmbiguous":"boolean","processWaitResultAfter":"uint?"}'::jsonb
 WHEN 'eligibility' THEN '{"sqlProcessEligible":"boolean","operatingSystemEligible":"boolean","sameNativeInterval":"boolean","sqlProcessReason":"numericalgap","operatingSystemReason":"numericalgap","sqlFromNative100ns":"positive64?","sqlToNative100ns":"positive64?","osFromNative100ns":"positive64?","osToNative100ns":"positive64?","sqlProcessPercent":"null","operatingSystemPercent":"null"}'::jsonb
 WHEN 'bytes' THEN '{"tcpBeforeHelperBytes":"helperbytes?","cpuPairHelperBytes":"helperbytes?","tcpAfterHelperBytes":"helperbytes?","sqlLogicalResponseUpperBoundBytes":"sqlbytes"}'::jsonb
 ELSE NULL END;
 IF v_spec IS NULL OR jsonb_typeof(p_value) IS DISTINCT FROM 'object' OR
  NOT p_value ?& ARRAY(SELECT jsonb_object_keys(v_spec)) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(p_value) k WHERE NOT v_spec ? k) THEN
  RAISE EXCEPTION 'CPU closed projection rejected' USING ERRCODE='22023'; END IF;
 FOR v_field IN SELECT key,value #>> '{}' AS type FROM jsonb_each(v_spec) LOOP
  v_type:=v_field.type; v_nullable:=right(v_type,1)='?'; v_type:=rtrim(v_type,'?'); v_value:=p_value->v_field.key;
  IF v_value='null'::jsonb AND v_nullable THEN CONTINUE; END IF;
  IF v_type IN ('capture','sql','engine','binding','host','process','tcp','socket','cpu','counter','frame','eligibility','bytes') THEN
   PERFORM control.validate_cpu_capture_json(v_value,v_type,p_target,p_revision,p_run); CONTINUE;
  ELSIF v_type='sockets' THEN
   IF jsonb_typeof(v_value) IS DISTINCT FROM 'array' OR jsonb_array_length(v_value)>16 THEN
    RAISE EXCEPTION 'CPU matching row bound rejected' USING ERRCODE='22023'; END IF;
   FOR v_row IN SELECT value FROM jsonb_array_elements(v_value) LOOP
    PERFORM control.validate_cpu_capture_json(v_row,'socket',p_target,p_revision,p_run); END LOOP;
   CONTINUE;
  END IF;
  IF v_type='null' THEN IF v_value IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'CPU percentage or occurrence cannot be admitted' USING ERRCODE='22023'; END IF;
  ELSIF v_type='false' THEN IF v_value IS DISTINCT FROM 'false'::jsonb THEN RAISE EXCEPTION 'CPU qualification cannot be admitted' USING ERRCODE='22023'; END IF;
  ELSIF v_type='boolean' THEN IF jsonb_typeof(v_value) IS DISTINCT FROM 'boolean' THEN RAISE EXCEPTION 'CPU boolean rejected' USING ERRCODE='22023'; END IF;
  ELSIF v_type IN ('target','run','uuid','fingerprint','utc','local','readreason') THEN
   IF jsonb_typeof(v_value) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'CPU string type rejected' USING ERRCODE='22023'; END IF;
   IF v_type IN ('target','run','uuid') THEN
    IF v_value #>> '{}' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' OR
     (v_value #>> '{}')::uuid='00000000-0000-0000-0000-000000000000'::uuid OR
     v_type='target' AND (v_value #>> '{}')::uuid<>p_target OR v_type='run' AND (v_value #>> '{}')::uuid<>p_run THEN
     RAISE EXCEPTION 'CPU context UUID rejected' USING ERRCODE='22023'; END IF;
   ELSIF v_type='fingerprint' THEN IF v_value #>> '{}' !~ '^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'CPU fingerprint rejected' USING ERRCODE='22023'; END IF;
   ELSIF v_type='utc' THEN IF v_value #>> '{}' !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$' OR NOT isfinite((v_value #>> '{}')::timestamptz) THEN RAISE EXCEPTION 'CPU UTC receipt rejected' USING ERRCODE='22023'; END IF;
   ELSIF v_type='readreason' THEN IF v_value #>> '{}' NOT IN ('source_qualification_absent','timed_out','host_binding_unavailable','cpu_capability_unavailable','permission_denied','unsupported','invalid_source_output','provider_failure','host_identity_unavailable','wrong_host','sql_source_schema_unqualified','tcp_source_unavailable','unsupported_address','sql_process_unavailable','native_table_unavailable','native_process_denied','process_unavailable','process_exited','process_epoch_unavailable','process_epoch_changed','process_lifetime_unknown','socket_not_observed','process_mapping_ambiguous','process_race','process_missing','process_counter_unavailable') THEN RAISE EXCEPTION 'CPU source token rejected' USING ERRCODE='22023'; END IF;
   ELSE PERFORM control.server_lifecycle_local_ticks(v_value #>> '{}'); END IF;
  ELSE
   IF jsonb_typeof(v_value) IS DISTINCT FROM 'number' OR v_value::text !~ '^-?(0|[1-9][0-9]{0,18})$' THEN
    RAISE EXCEPTION 'CPU integer scalar rejected' USING ERRCODE='22023'; END IF;
   v_n:=v_value::text::numeric;
   IF v_n < (CASE WHEN v_type='int64' THEN -9223372036854775808::numeric WHEN v_type IN ('positive64','positive32','positiveuint','revision','one','ordinal','session','sqlschema','coverage') THEN 1 ELSE 0 END) OR
    v_n > (CASE v_type WHEN 'zero' THEN 0 WHEN 'one' THEN 1 WHEN 'state' THEN 1 WHEN 'gap' THEN 7 WHEN 'candidate' THEN 31 WHEN 'numericalgap' THEN 21
     WHEN 'ordinal' THEN 2 WHEN 'transport' THEN 3 WHEN 'completeness' THEN 2 WHEN 'hostwitness' THEN 1 WHEN 'sqlschema' THEN 2 WHEN 'coverage' THEN 3
     WHEN 'direction' THEN 2 WHEN 'session' THEN 32767 WHEN 'rows' THEN 4096 WHEN 'helperbytes' THEN 65536 WHEN 'sqlbytes' THEN 16384
     WHEN 'uint' THEN 4294967295::numeric WHEN 'positiveuint' THEN 4294967295::numeric
     WHEN 'positive32' THEN 2147483647 WHEN 'nonnegative32' THEN 2147483647 ELSE 9223372036854775807::numeric END) OR
    v_type='revision' AND v_n<>p_revision THEN RAISE EXCEPTION 'CPU scalar bound rejected' USING ERRCODE='22023'; END IF;
  END IF;
 END LOOP;
 IF p_shape='engine' THEN
  IF p_value->>'startupIncarnationFingerprint' IS DISTINCT FROM control.server_lifecycle_startup_fingerprint(p_value) OR
   p_value->>'coverage' IS DISTINCT FROM (CASE WHEN p_value->>'physicalNodeFingerprint' IS NULL OR p_value->>'logicalInstanceFingerprint' IS NULL OR
    p_value->>'processId' IS NULL OR p_value->>'sourceStartLocal' IS NULL OR p_value->>'startMsTicks' IS NULL OR p_value->>'currentMsTicks' IS NULL THEN 3
    WHEN (p_value->>'startMsTicks')::bigint>(p_value->>'currentMsTicks')::bigint THEN 2 ELSE 1 END)::text THEN
   RAISE EXCEPTION 'CPU engine seal rejected' USING ERRCODE='22023'; END IF;
 ELSIF p_shape='tcp' THEN
  IF (p_value->>'rowCount4')::integer+(p_value->>'rowCount6')::integer>4096 OR jsonb_array_length(p_value->'matchingRows')>
   (p_value->>'rowCount4')::integer+(p_value->>'rowCount6')::integer THEN RAISE EXCEPTION 'CPU enumeration accounting rejected' USING ERRCODE='22023'; END IF;
 ELSIF p_shape='eligibility' THEN
  IF (p_value->>'sqlProcessEligible')::boolean AND (p_value->>'sqlProcessReason'<>'0' OR p_value->>'sqlFromNative100ns' IS NULL OR p_value->>'sqlToNative100ns' IS NULL OR
    (p_value->>'sqlFromNative100ns')::bigint >= (p_value->>'sqlToNative100ns')::bigint) OR
   (p_value->>'operatingSystemEligible')::boolean AND (p_value->>'operatingSystemReason'<>'0' OR p_value->>'osFromNative100ns' IS NULL OR p_value->>'osToNative100ns' IS NULL OR
    (p_value->>'osFromNative100ns')::bigint >= (p_value->>'osToNative100ns')::bigint) OR
   p_value->'sqlProcessEligible'='false'::jsonb AND (p_value->>'sqlProcessReason'='0' OR p_value->'sqlFromNative100ns'<>'null'::jsonb OR p_value->'sqlToNative100ns'<>'null'::jsonb) OR
   p_value->'operatingSystemEligible'='false'::jsonb AND (p_value->>'operatingSystemReason'='0' OR p_value->'osFromNative100ns'<>'null'::jsonb OR p_value->'osToNative100ns'<>'null'::jsonb) OR
   p_value->'sameNativeInterval' IS DISTINCT FROM to_jsonb(p_value->'sqlProcessEligible'='true'::jsonb AND p_value->'operatingSystemEligible'='true'::jsonb AND
    p_value->'sqlFromNative100ns'=p_value->'osFromNative100ns' AND p_value->'sqlToNative100ns'=p_value->'osToNative100ns') THEN
   RAISE EXCEPTION 'CPU diagnostic interval rejected' USING ERRCODE='22023'; END IF;
 ELSIF p_shape='sql' THEN
  IF (p_value->'serverConnectionId'='null'::jsonb) IS DISTINCT FROM (p_value->'serverSessionId'='null'::jsonb) OR
   (p_value->'serverConnectionId'='null'::jsonb) IS DISTINCT FROM (p_value->'collectorProcessId'='null'::jsonb) OR
   (p_value->'serverConnectionId'='null'::jsonb) IS DISTINCT FROM (p_value->'socketCompleteness'='null'::jsonb) OR
   (p_value->>'sqlReportedLogicalProcessors')::integer>4096 THEN RAISE EXCEPTION 'CPU SQL witness shape rejected' USING ERRCODE='22023'; END IF;
 ELSIF p_shape='frame' THEN
  IF (p_value->>'processInstances')::integer>1024 THEN RAISE EXCEPTION 'CPU process inventory bound rejected' USING ERRCODE='22023'; END IF;
 END IF;
END $validate$;
REVOKE ALL ON FUNCTION control.validate_cpu_capture_json(jsonb,text,uuid,bigint,uuid) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.commit_cpu_paired_capture_collection_run(
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
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $fn$
DECLARE
 run_row telemetry.collection_run_v2%ROWTYPE;
 schedule_row control.collector_schedule%ROWTYPE;
 contract_row control.collector_contract%ROWTYPE;
 prior telemetry.collection_run_outcome%ROWTYPE;
 v_capture jsonb; v_frames integer;
 now_utc timestamptz; is_failure boolean;
 inserted integer:=0; written_bytes integer:=0;
BEGIN
 is_failure:=p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'cpu.paired-capture' OR p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1
  OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<=0
  OR p_schedule_revision IS NULL OR p_schedule_revision<=0 OR p_scheduled_at IS NULL OR NOT isfinite(p_scheduled_at)
  OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token<=0
  OR octet_length(p_request_digest) IS DISTINCT FROM 32 OR octet_length(p_completion_digest) IS DISTINCT FROM 32
  OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR octet_length(p_payload::text)>70000
  OR p_completion_digest IS DISTINCT FROM sha256(convert_to(p_payload::text,'UTF8'))
  OR p_outcome IS NULL OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
  OR p_reason_code IS NULL OR p_reason_code NOT IN ('completed','source_row_limit','response_byte_limit','deadline_exceeded','transient_target_failure','permanent_target_failure','required_permission_missing','target_unsupported','output_validation_failed','lease_ownership_lost','circuit_currently_open','capability_profile_missing','capability_profile_stale','capability_missing','target_version_unsupported','target_platform_unsupported','target_edition_unsupported','visibility_incomplete')
  OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 0 AND 2 OR p_attempt_count=0 AND p_outcome<>'circuit_open'
  OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
  OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 6 OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 1
  OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 212992 OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 65536
  OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 6 OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 212992
  OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL OR p_next_circuit_state NOT IN ('closed','open','half_open')
  OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
  OR p_loss_kind IS NULL OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit','output_validation_failure','visibility_incomplete')
  OR p_outcome='partial' OR p_outcome='succeeded' AND p_loss_kind<>'none'
  OR p_loss_kind='none' AND (p_minimum_lost_items<>0 OR p_minimum_lost_bytes<>0 OR NOT p_loss_count_is_exact)
  OR p_loss_kind<>'none' AND p_minimum_lost_items=0 AND p_minimum_lost_bytes=0
  OR is_failure AND (p_output_item_count<>0 OR p_output_bytes<>0 OR
       p_outcome='output_invalid' AND (p_loss_kind<>'output_validation_failure' OR p_minimum_lost_items<1) OR
       p_outcome<>'output_invalid' AND p_loss_kind<>'none')
 THEN RAISE EXCEPTION 'CPU completion bounds rejected' USING ERRCODE='22023'; END IF;
 IF p_run_id='00000000-0000-0000-0000-000000000000'::uuid OR p_instance_id='00000000-0000-0000-0000-000000000000'::uuid OR
  p_owner_execution_id='00000000-0000-0000-0000-000000000000'::uuid OR
  NOT p_payload ?& ARRAY['schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceFrames','truncated','capture'] OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(p_payload) k WHERE k NOT IN ('schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceFrames','truncated','capture')) OR
  p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text OR
  p_payload->'targetRevision' IS DISTINCT FROM to_jsonb(p_target_revision) OR p_payload->>'runId' IS DISTINCT FROM p_run_id::text OR
  p_payload->>'outcome' IS DISTINCT FROM p_outcome OR p_payload->>'reason' IS DISTINCT FROM p_reason_code OR
  p_payload->'sourceFrames' IS DISTINCT FROM to_jsonb(p_source_row_count) OR p_payload->'truncated' IS DISTINCT FROM 'false'::jsonb THEN
  RAISE EXCEPTION 'CPU execution envelope rejected' USING ERRCODE='22023'; END IF;
 v_capture:=p_payload->'capture';
 IF is_failure THEN
  IF v_capture IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'CPU failure contains fabricated source evidence' USING ERRCODE='22023'; END IF;
 ELSE
  PERFORM control.validate_cpu_capture_json(v_capture,'capture',p_instance_id,p_target_revision,p_run_id);
  SELECT count(*) INTO v_frames FROM jsonb_each(v_capture) f WHERE f.key IN ('sqlBefore','sqlAfter','tcpBefore','tcpAfter','cpuBefore','cpuAfter') AND f.value<>'null'::jsonb;
  IF p_source_row_count<>v_frames OR p_output_item_count<>1 OR p_output_bytes<1 OR
   p_response_bytes<>(coalesce((v_capture->'bytes'->>'tcpBeforeHelperBytes')::integer,0)+
    coalesce((v_capture->'bytes'->>'cpuPairHelperBytes')::integer,0)+coalesce((v_capture->'bytes'->>'tcpAfterHelperBytes')::integer,0)+
    (v_capture->'bytes'->>'sqlLogicalResponseUpperBoundBytes')::bigint) OR
   v_capture->'hostBindingRevision' IS DISTINCT FROM v_capture->'hostProfileRevision' OR
   v_capture->'state' IS DISTINCT FROM to_jsonb(CASE WHEN v_frames=6 THEN 1 ELSE 0 END) OR
   v_capture->'candidateLocalAssociation' IS DISTINCT FROM to_jsonb(v_capture->>'candidateReason'='0') OR
   v_capture->>'candidateReason'='0' AND v_frames<>6 OR
   v_capture->'numericalEligibility'<>'null'::jsonb AND v_capture->>'candidateReason'<>'0' OR
   v_capture->'sqlBefore'<>'null'::jsonb AND v_capture->'sqlBefore'->'ordinal'<>'1'::jsonb OR
   v_capture->'sqlAfter'<>'null'::jsonb AND v_capture->'sqlAfter'->'ordinal'<>'2'::jsonb THEN
   RAISE EXCEPTION 'CPU capture source/accounting rejected' USING ERRCODE='22023'; END IF;
 END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision
    OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version
    OR run_row.output_schema_version<>p_output_schema_version
    OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key
    OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token
    OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at
 THEN RAISE EXCEPTION 'CPU capture run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='cpu.paired-capture' AND collector_version=1;
 IF NOT FOUND OR contract_row.output_schema_version<>1
 THEN RAISE EXCEPTION 'CPU capture immutable collector contract missing' USING ERRCODE='55000'; END IF;
 PERFORM set_config('sqlobserver.target_scope',p_instance_id::text,true);

 -- A matching retry is valid after the schedule has advanced. A changed
 -- completion digest or accounting is an attempt to rewrite an immutable run.
 SELECT * INTO prior FROM telemetry.collection_run_outcome WHERE run_id=p_run_id;
 IF FOUND THEN
  IF prior.completion_digest<>p_completion_digest OR prior.outcome<>p_outcome
     OR prior.reason_code<>p_reason_code OR prior.attempt_count<>p_attempt_count
     OR prior.duration_ms<>p_duration_ms
     OR prior.source_row_count<>p_source_row_count
     OR prior.output_item_count <>
        (p_output_item_count + CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END)
     OR prior.response_bytes<>p_response_bytes OR prior.output_bytes<>p_output_bytes
     OR prior.loss_kind<>p_loss_kind OR prior.lost_row_count<>p_minimum_lost_items
     OR prior.lost_byte_count<>p_minimum_lost_bytes
     OR prior.loss_count_exact<>p_loss_count_is_exact
  THEN RAISE EXCEPTION 'CPU capture divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
    OR schedule_row.schedule_revision<>p_schedule_revision
    OR schedule_row.target_revision<>p_target_revision
    OR schedule_row.next_due_at<>p_scheduled_at
 THEN RAISE EXCEPTION 'CPU capture schedule fence mismatch' USING ERRCODE='55000'; END IF;
 IF (p_outcome IN ('succeeded','partial')
       AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
    OR (p_outcome IN ('timed_out','transient_failure')
       AND (p_next_consecutive_failures<>schedule_row.consecutive_failure_count+1
         OR p_next_circuit_state<>CASE WHEN schedule_row.circuit_state='half_open'
                OR schedule_row.consecutive_failure_count+1>=contract_row.circuit_failure_threshold
              THEN 'open' ELSE 'closed' END))
    OR (p_outcome='circuit_open' AND (p_next_circuit_state<>'open'
       OR p_next_consecutive_failures<>schedule_row.consecutive_failure_count))
    OR (p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','circuit_open')
       AND (p_next_circuit_state<>'closed' OR p_next_consecutive_failures<>0))
 THEN RAISE EXCEPTION 'CPU capture circuit transition mismatch' USING ERRCODE='22023'; END IF;


 -- The target trigger updates this locked schedule in the same transaction as
 -- target configuration changes. Reading target state here preserves that fence
 -- without acquiring a reversed target-row lock after the run lock.
 IF NOT EXISTS(SELECT 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id AND
  t.revision=p_target_revision AND t.lifecycle_state='active' AND t.host_name IS NOT NULL) THEN
  RAISE EXCEPTION 'CPU current target fence mismatch' USING ERRCODE='55000'; END IF;
 now_utc:=clock_timestamp();
 INSERT INTO telemetry.cpu_paired_capture_receipt(instance_id,run_id,target_revision,completed_at,source_schema_version,
  outcome,loss_kind,has_capture,payload,completion_digest)
 VALUES(p_instance_id,p_run_id,p_target_revision,now_utc,1,p_outcome,p_loss_kind,NOT is_failure,p_payload,p_completion_digest);
 IF NOT is_failure THEN inserted:=p_output_item_count; written_bytes:=p_output_bytes; END IF;
 IF p_outcome<>'succeeded' OR p_loss_kind<>'none' THEN
  INSERT INTO telemetry.visibility_gap
   (gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,
    lost_row_count,lost_byte_count,count_is_exact,recorded_at)
  VALUES (gen_random_uuid(),p_run_id,p_instance_id,p_collector_id,p_reason_code,
          p_scheduled_at,now_utc,greatest(p_minimum_lost_items,1),
          p_minimum_lost_bytes,p_loss_count_is_exact,now_utc)
  ON CONFLICT(run_id) DO NOTHING;
 END IF;
 INSERT INTO telemetry.collection_run_outcome
  (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,
   output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,
   response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
   loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
 VALUES (p_run_id,p_outcome,p_reason_code,p_attempt_count,greatest(p_attempt_count-1,0),
         p_duration_ms,p_source_row_count,
         p_output_item_count+CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
         inserted,0,CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
         p_response_bytes,p_output_bytes,written_bytes,
         coalesce((p_payload->>'truncated')::boolean,false),p_loss_kind<>'none',p_loss_kind,
         p_loss_count_is_exact,p_minimum_lost_items,p_minimum_lost_bytes,p_completion_digest,now_utc);
 UPDATE control.collector_schedule
 SET active_run_id=NULL,
     next_due_at=CASE WHEN p_next_circuit_state='open'
       THEN now_utc+contract_row.circuit_open_interval ELSE now_utc+collection_interval END,
     circuit_state=p_next_circuit_state,consecutive_failure_count=p_next_consecutive_failures,
     circuit_open_until=CASE WHEN p_next_circuit_state='open'
       THEN now_utc+contract_row.circuit_open_interval ELSE NULL END,
     last_completed_at=now_utc,
     last_succeeded_at=CASE WHEN p_outcome='succeeded' THEN now_utc ELSE last_succeeded_at END,
     last_outcome=p_outcome,updated_at=now_utc
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 'committed',inserted,0,
   CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
   written_bytes,now_utc;

EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow THEN
 RAISE EXCEPTION 'CPU scalar shape rejected' USING ERRCODE='22023';
END $fn$;
REVOKE ALL ON FUNCTION control.commit_cpu_paired_capture_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_cpu_paired_capture_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea) TO sqlobserver_collector;


-- Partition and canonical retirement admission; policies remain disabled.
ALTER TABLE system.partition_registry DROP CONSTRAINT ck_partition_registry_parent;
ALTER TABLE system.partition_registry ADD CONSTRAINT ck_partition_registry_parent CHECK
  (parent_schema IN ('telemetry'::name,'events'::name,'analytics'::name,'alerting'::name)
   AND parent_table IN
  ('raw_metric_sample','diagnostic_event','activity_session_snapshot','activity_request_snapshot','server_wait_snapshot','blocking_edge',
   'database_inventory_snapshot','database_file_snapshot','host_metric_snapshot_v2','replication_snapshot_v2',
   'backup_status_snapshot','sql_agent_failure_scan_snapshot','sql_agent_failure_occurrence','tempdb_snapshot','tempdb_file_snapshot',
   'availability_group_replica_snapshot','availability_group_database_snapshot','query_performance_observation',
   'deadlock_summary','deadlock_participant','deadlock_relation','state_history','delivery_attempt','delivery_outbox',
   'metric_rollup_v2','evidence_packet_v2',
   'activity_session_snapshot_v2','activity_request_snapshot_v2','server_wait_snapshot_v2','blocking_edge_v2',
   'database_inventory_snapshot_v2','database_file_snapshot_v2','query_performance_observation_v2','query_performance_observation_v3',
   'deadlock_summary_v2','deadlock_participant_v2','deadlock_relation_v2','state_history_v2','delivery_attempt_v2','delivery_outbox_v2',
   'backup_status_snapshot_v2','sql_agent_failure_scan_snapshot_v2','sql_agent_failure_occurrence_v2','tempdb_snapshot_v2','tempdb_file_snapshot_v2',
   'availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2','sql_volume_snapshot',
   'query_store_group_watermark','query_store_group_delta','collection_run_v2',
   'availability_replica_evidence','availability_database_replica_evidence',
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline','deadlock_read_receipt','plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link','server_lifecycle_receipt','server_lifecycle_event','cpu_paired_capture_receipt'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy','collection_run_canonical',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links','server_lifecycle_receipts','server_lifecycle_events','cpu_paired_captures'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry' AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events' AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events' AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR (data_class='m7_query_performance' AND parent_schema='events' AND parent_table='query_performance_observation_v3' AND partition_granularity='day')
   OR (data_class='collection_run_canonical' AND parent_schema='telemetry' AND parent_table='collection_run_identity' AND partition_granularity='none')
   OR (data_class='collection_run_original_copy' AND parent_schema='telemetry' AND parent_table='collection_run' AND partition_granularity='none')
   OR (data_class='m9_ag_replica_evidence' AND parent_schema='telemetry' AND parent_table='availability_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_ag_database_evidence' AND parent_schema='telemetry' AND parent_table='availability_database_replica_evidence' AND partition_granularity='day')
   OR (data_class='m9_backup_evidence' AND parent_schema='telemetry' AND parent_table='backup_v2_scan' AND partition_granularity='day')
   OR (data_class='sql_metric_baselines' AND parent_schema='analytics' AND parent_table='sql_metric_baseline' AND partition_granularity='day')
   OR (data_class='m6_deadlock_read_receipts' AND parent_schema='events' AND parent_table='deadlock_read_receipt' AND partition_granularity='month')
   OR (data_class='m7_cache_watermarks' AND parent_schema='events' AND parent_table='plan_cache_runtime_watermark' AND partition_granularity='day')
   OR (data_class='m7_cache_deltas' AND parent_schema='events' AND parent_table='plan_cache_runtime_delta' AND partition_granularity='day')
   OR (data_class='m7_cache_receipts' AND parent_schema='events' AND parent_table='plan_cache_runtime_receipt' AND partition_granularity='day')
   OR (data_class='m7_cache_content_receipts' AND parent_schema='events' AND parent_table='plan_cache_content_receipt' AND partition_granularity='day')
   OR (data_class='m7_cache_content_links' AND parent_schema='events' AND parent_table='plan_cache_content_link' AND partition_granularity='day')
   OR (data_class='cpu_paired_captures' AND parent_schema='telemetry' AND parent_table='cpu_paired_capture_receipt' AND partition_granularity='day')
   OR (data_class='server_lifecycle_receipts' AND parent_schema='telemetry' AND parent_table='server_lifecycle_receipt' AND partition_granularity='day')
   OR (data_class='server_lifecycle_events' AND parent_schema='events' AND parent_table='server_lifecycle_event' AND partition_granularity='day')
   OR data_class IN ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking','m6_deadlocks',
     'm8_alert_history','m8_alert_delivery','m9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases','m10_host_metrics','m10_replication','m10_rollups','m10_evidence')));


CREATE OR REPLACE FUNCTION system.collection_run_reviewed_references()
RETURNS TABLE(child_relation regclass,column_name name,parent_relation regclass,delete_action "char")
LANGUAGE sql STABLE PARALLEL SAFE SET search_path=pg_catalog
AS $inventory$
 VALUES
 ('control.collector_schedule'::regclass,'active_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.blocking_edge'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.deadlock_summary'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.query_performance_run'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.activity_request_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.activity_session_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_group_database_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_group_replica_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.backup_status_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.collection_run_outcome'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.database_file_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.database_inventory_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.raw_metric_sample'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.server_wait_snapshot'::regclass,'collection_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure_occurrence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_agent_failure_scan_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.sql_volume_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.tempdb_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.visibility_gap'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.server_configuration_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.backup_v2_scan'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_replica_evidence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.availability_database_replica_evidence'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('alerting.deadlock_source_snapshot'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'c'::"char"),
 ('control.deadlock_read_checkpoint'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'c'::"char"),
 ('events.plan_cache_runtime_watermark'::regclass,'last_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_runtime_delta'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_runtime_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_content_link'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.plan_cache_content_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.deadlock_read_receipt'::regclass,'run_id'::name,'telemetry.collection_run_outcome'::regclass,'a'::"char"),
 ('telemetry.server_lifecycle_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('events.server_lifecycle_event'::regclass,'after_run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('control.server_lifecycle_checkpoint'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.cpu_paired_capture_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char");
$inventory$;
REVOKE ALL ON FUNCTION system.collection_run_reviewed_references() FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.server_lifecycle_receipt,events.server_lifecycle_event,control.server_lifecycle_checkpoint
 TO sqlobserver_run_dependency_reader;

INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('cpu_paired_captures','telemetry','cpu_paired_capture_receipt','day',false,NULL,3,'migration-0359');
CREATE FUNCTION control.ensure_cpu_paired_capture_partitions(p_anchor date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $partitions$
DECLARE v_day date; v_parent_name text; v_schema text; v_name text; v_parent oid; v_child oid; v_created integer:=0;
BEGIN
 IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'CPU capture partition anchor must be current UTC date' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:cpu-paired-capture:partition-set',0));
 FOREACH v_parent_name IN ARRAY ARRAY['cpu_paired_capture_receipt'] LOOP
  v_schema:='telemetry';
  v_parent:=format('%I.%I',v_schema,v_parent_name)::regclass;
  IF NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.oid=v_parent AND c.relkind='p' AND
   c.relowner='sqlobserver_migrator'::regrole AND c.relrowsecurity AND c.relforcerowsecurity) THEN
   RAISE EXCEPTION 'CPU capture partition parent security unverified' USING ERRCODE='55000'; END IF;
  FOR v_day IN SELECT p_anchor+i FROM generate_series(-1,1) days(i) LOOP
   v_name:=v_parent_name||'_p'||to_char(v_day,'YYYYMMDD');
   v_child:=to_regclass(format('%I.%I',v_schema,v_name));
   IF v_child IS NULL THEN
    EXECUTE format('CREATE TABLE %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',
     v_schema,v_name,v_schema,v_parent_name,v_day::timestamptz,(v_day+1)::timestamptz);
    v_created:=v_created+1;
    EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',v_schema,v_name);
    EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',v_schema,v_name);
    EXECUTE format('CREATE POLICY %I ON %I.%I USING(instance_id::text=current_setting(''sqlobserver.target_scope'',true)) WITH CHECK(instance_id::text=current_setting(''sqlobserver.target_scope'',true))',v_name||'_scope',v_schema,v_name);
    EXECUTE format('CREATE POLICY %I ON %I.%I FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true)',v_name||'_migrator',v_schema,v_name);
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON %I.%I FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation()',v_name||'_append_only',v_schema,v_name);
    v_child:=to_regclass(format('%I.%I',v_schema,v_name));
   END IF;
   IF NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=v_child AND inhparent=v_parent) OR
    NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.oid=v_child AND c.relkind='r' AND c.relowner='sqlobserver_migrator'::regrole AND
     c.relrowsecurity AND c.relforcerowsecurity AND pg_get_expr(c.relpartbound,c.oid)=format('FOR VALUES FROM (%L) TO (%L)',v_day::timestamptz,(v_day+1)::timestamptz)) OR
    NOT EXISTS(SELECT 1 FROM pg_trigger t WHERE t.tgrelid=v_child AND t.tgname=v_name||'_append_only' AND
     t.tgfoid='control.reject_collector_history_mutation()'::regprocedure AND t.tgenabled='O') THEN
    RAISE EXCEPTION 'CPU capture partition identity or immutability unverified' USING ERRCODE='55000'; END IF;
   EXECUTE format('REVOKE ALL ON %I.%I FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor',v_schema,v_name);
   EXECUTE format('GRANT SELECT ON %I.%I TO sqlobserver_run_dependency_reader',v_schema,v_name);
   INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
   VALUES(v_schema,v_parent_name,v_schema,v_name,'day',v_day::timestamptz,(v_day+1)::timestamptz) ON CONFLICT DO NOTHING;
   IF NOT EXISTS(SELECT 1 FROM system.partition_registry r WHERE r.parent_schema=v_schema AND r.parent_table=v_parent_name AND
    r.partition_schema=v_schema AND r.partition_name=v_name AND r.partition_granularity='day' AND r.lifecycle_state='attached' AND
    r.range_start=v_day::timestamptz AND r.range_end=(v_day+1)::timestamptz) THEN
    RAISE EXCEPTION 'CPU capture partition registry unverified' USING ERRCODE='55000'; END IF;
  END LOOP;
 END LOOP;
 RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_cpu_paired_capture_partitions(date) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.ensure_cpu_paired_capture_partition_set(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $fenced$
DECLARE v_created integer;
BEGIN
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 v_created:=control.ensure_cpu_paired_capture_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 RETURN v_created;
END $fenced$;
REVOKE ALL ON FUNCTION control.ensure_cpu_paired_capture_partition_set(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_cpu_paired_capture_partition_set(uuid,bigint) TO sqlobserver_collector;
-- Bootstrap does not activate a source or a retention policy.
SELECT control.ensure_cpu_paired_capture_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);

CREATE OR REPLACE FUNCTION system.update_m10_retention_policy
 (p_data_class text,p_enabled boolean,p_retain_for interval,p_minimum_partitions integer,
  p_expected_revision bigint,p_changed_by text,p_change_reason text)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system
AS $policy_update$
DECLARE next_revision bigint;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' THEN
  RAISE EXCEPTION 'global retention mutation requires SecurityAdministrator authorization context'
   USING ERRCODE='42501';
 END IF;
 IF p_data_class IS NULL OR p_data_class NOT IN
    ('raw_metric_samples','diagnostic_events',
     'm5_activity','m5_requests','m5_waits','m5_blocking',
     'm9_backup_status','m9_agent_history','m9_agent_occurrences',
     'm9_ag_replicas','m9_ag_databases','m9_tempdb',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence',
     'sql_volume_capacity','m7_group_watermarks','m7_group_deltas',
     'm7_query_performance','collection_run_original_copy','collection_run_canonical',
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links','server_lifecycle_receipts','server_lifecycle_events','cpu_paired_captures')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
    OR (p_data_class='m6_deadlock_read_receipts' AND (p_minimum_partitions<2 OR (p_enabled AND p_retain_for<interval '30 days')))
    OR (p_data_class='m7_cache_watermarks' AND p_enabled AND p_retain_for<interval '8 days')
    OR (p_data_class IN ('m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links') AND p_enabled AND p_retain_for<interval '30 days')
    OR (p_data_class='collection_run_canonical' AND (p_minimum_partitions<>1
        OR (p_retain_for IS NOT NULL AND (p_retain_for<interval '90 days' OR p_retain_for>interval '3650 days'))))
    OR (p_data_class IN ('server_lifecycle_receipts','server_lifecycle_events','cpu_paired_captures')
        AND (p_enabled IS NULL OR p_minimum_partitions IS NULL OR p_minimum_partitions NOT BETWEEN 3 AND 10000
          OR (p_retain_for IS NOT NULL AND (p_retain_for<interval '90 days' OR p_retain_for>interval '3650 days'
            OR extract(year FROM p_retain_for)<>0 OR extract(month FROM p_retain_for)<>0))))
    OR (p_data_class='collection_run_original_copy'
        AND (p_minimum_partitions<>1 OR (p_enabled
          AND p_retain_for<interval '30 days'))) THEN
  RAISE EXCEPTION 'retention policy bounds rejected' USING ERRCODE='22023';
 END IF;
 PERFORM set_config('sqlobserver.retention_change_reason',p_change_reason,true);
 PERFORM set_config('sqlobserver.retention_changed_by',p_changed_by,true);
 UPDATE system.retention_policy
 SET enabled=p_enabled,retain_for=p_retain_for,
     minimum_partitions_to_keep=p_minimum_partitions,
     policy_revision=policy_revision+1,updated_by=p_changed_by,
     updated_at=clock_timestamp()
 WHERE data_class=p_data_class AND policy_revision=p_expected_revision
 RETURNING policy_revision INTO next_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'retention policy revision conflict' USING ERRCODE='40001';
 END IF;
 RETURN next_revision;
END $policy_update$;
