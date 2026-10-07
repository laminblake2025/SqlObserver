-- Independent SQL Agent activity observations with immutable run identity and bounded native evidence.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.sql_agent_activity_receipt
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 completed_at timestamptz NOT NULL CHECK(isfinite(completed_at)),
 source_schema_version integer NOT NULL CHECK(source_schema_version=1),
 outcome text NOT NULL,
 loss_kind text NOT NULL,
 has_capture boolean NOT NULL,
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object' AND octet_length(payload::text)<=530000),
 completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32),
 PRIMARY KEY(instance_id,completed_at,run_id)
) PARTITION BY RANGE(completed_at);
CREATE INDEX ix_sql_agent_activity_run ON telemetry.sql_agent_activity_receipt(run_id);
CREATE INDEX ix_sql_agent_activity_target_time ON telemetry.sql_agent_activity_receipt(instance_id,completed_at DESC,run_id);
COMMENT ON TABLE telemetry.sql_agent_activity_receipt IS 'Bounded enrolled SQL Agent source observations. Running association, physical elapsed time, and current step remain unqualified.';
ALTER TABLE telemetry.sql_agent_activity_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.sql_agent_activity_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY sql_agent_activity_scope ON telemetry.sql_agent_activity_receipt
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
 WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY sql_agent_activity_migrator ON telemetry.sql_agent_activity_receipt
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER sql_agent_activity_append_only BEFORE UPDATE OR DELETE ON telemetry.sql_agent_activity_receipt
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON telemetry.sql_agent_activity_receipt FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.sql_agent_activity_receipt TO sqlobserver_run_dependency_reader;


-- Closed JSON shapes reject private enrollment pins and unsupported source fields.
CREATE FUNCTION control.agent_activity_object_shape(p_value jsonb,p_keys text[])
RETURNS boolean LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $shape$
 SELECT CASE WHEN jsonb_typeof(p_value) IS DISTINCT FROM 'object' THEN false ELSE
  (SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(p_value) k) IS NOT DISTINCT FROM
  (SELECT array_agg(k ORDER BY k) FROM unnest(p_keys) k) END;
$shape$;
CREATE FUNCTION control.agent_activity_date(p_value jsonb,p_local boolean)
RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $date$
DECLARE value text; local_at timestamp; utc_at timestamptz;
BEGIN
 IF p_value='null'::jsonb THEN RETURN true; END IF;
 IF jsonb_typeof(p_value) IS DISTINCT FROM 'string' THEN RETURN false; END IF;
 value:=p_value#>>'{}';
 IF p_local THEN
  IF value !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}$' THEN RETURN false; END IF;
  local_at:=left(value,26)::timestamp;
  RETURN isfinite(local_at) AND local_at>=timestamp '1753-01-01' AND local_at<timestamp '10000-01-01'
   AND to_char(local_at,'YYYY-MM-DD"T"HH24:MI:SS.US')=left(value,26);
 ELSE
  IF value !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$' THEN RETURN false; END IF;
  utc_at:=(left(value,26)||'Z')::timestamptz;
  RETURN isfinite(utc_at) AND utc_at>=timestamptz '1753-01-01Z' AND utc_at<timestamptz '10000-01-01Z'
   AND to_char(utc_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')=left(value,26);
 END IF;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format THEN RETURN false;
END $date$;
-- PostgreSQL timestamps retain six fractional digits; keep the seventh source
-- digit separately so 100ns source brackets cannot round across boundaries.
CREATE FUNCTION control.agent_activity_utc_ticks(p_value text)
RETURNS numeric LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog AS $ticks$
 SELECT extract(epoch FROM (left(p_value,26)||'Z')::timestamptz)*10000000+substring(p_value,27,1)::numeric;
$ticks$;
CREATE FUNCTION control.agent_activity_authorization_ticks(p_value text)
RETURNS numeric LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $ticks$
DECLARE fraction text[];
BEGIN
 fraction:=regexp_match(p_value,'\.([0-9]+)(Z|\+00:00)$');
 IF fraction IS NULL THEN RETURN extract(epoch FROM p_value::timestamptz)*10000000; END IF;
 --0369accepts ISO fractions independently of source seven-digit formatting.
 --Strip the fraction before parsing seconds and restore it as exact numeric ticks.
 RETURN extract(epoch FROM regexp_replace(p_value,'\.[0-9]+(Z|\+00:00)$','Z')::timestamptz)*10000000
  + ('0.'||fraction[1])::numeric*10000000;
END $ticks$;
CREATE FUNCTION control.agent_activity_integer(p_value jsonb,p_min bigint,p_max bigint,p_nullable boolean DEFAULT false)
RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $integer$
BEGIN
 IF p_nullable AND p_value='null'::jsonb THEN RETURN true; END IF;
 IF jsonb_typeof(p_value) IS DISTINCT FROM 'number' OR p_value#>>'{}' !~ '^(0|[1-9][0-9]*)$' THEN RETURN false; END IF;
 RETURN (p_value#>>'{}')::numeric BETWEEN p_min AND p_max;
END $integer$;
CREATE FUNCTION control.validate_sql_agent_activity_observation(p_value jsonb,p_target uuid,p_revision bigint,p_run uuid)
RETURNS void LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $validate$
DECLARE h jsonb; s jsonb; session_value jsonb; j jsonb; k text; previous_id text; source_count integer; job_count integer;
BEGIN
 IF NOT control.agent_activity_object_shape(p_value,ARRAY['sourceSchemaVersion','enrollmentRevision','enrollmentId','evidenceSha256',
   'before','after','jobs','sourceRowsRead','truncated','productionAdmitted','serviceSessionAssociationQualified','nativeQualification'])
  OR octet_length(p_value::text)>524288 OR p_value->'sourceSchemaVersion' IS DISTINCT FROM '1'::jsonb
  OR NOT control.agent_activity_integer(p_value->'enrollmentRevision',1,9007199254740991)
  OR jsonb_typeof(p_value->'enrollmentId') IS DISTINCT FROM 'string'
  OR p_value->>'enrollmentId' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  OR p_value->>'enrollmentId'='00000000-0000-0000-0000-000000000000'
  OR jsonb_typeof(p_value->'evidenceSha256') IS DISTINCT FROM 'string' OR p_value->>'evidenceSha256' !~ '^[0-9a-f]{64}$'
  OR p_value->>'evidenceSha256'=repeat('0',64)
  OR p_value->'productionAdmitted' IS DISTINCT FROM 'false'::jsonb OR p_value->'serviceSessionAssociationQualified' IS DISTINCT FROM 'false'::jsonb
  OR p_value->'nativeQualification' IS DISTINCT FROM '"Absent"'::jsonb
  OR jsonb_typeof(p_value->'jobs') IS DISTINCT FROM 'array' OR jsonb_typeof(p_value->'truncated') IS DISTINCT FROM 'boolean'
  OR NOT control.agent_activity_integer(p_value->'sourceRowsRead',0,513)
 THEN RAISE EXCEPTION 'Agent activity observation shape rejected' USING ERRCODE='22023'; END IF;
 source_count:=(p_value->>'sourceRowsRead')::integer; job_count:=jsonb_array_length(p_value->'jobs');
 IF job_count<>least(source_count,512) OR p_value->'truncated' IS DISTINCT FROM to_jsonb(source_count>512) THEN
  RAISE EXCEPTION 'Agent activity source accounting rejected' USING ERRCODE='22023'; END IF;
 FOREACH k IN ARRAY ARRAY['before','after'] LOOP
  h:=p_value->k;
  IF NOT control.agent_activity_object_shape(h,ARRAY['sourceObservedAtUtc','service','session'])
   OR h->'sourceObservedAtUtc'='null'::jsonb OR NOT control.agent_activity_date(h->'sourceObservedAtUtc',false) THEN
   RAISE EXCEPTION 'Agent activity header shape rejected' USING ERRCODE='22023'; END IF;
  s:=h->'service'; session_value:=h->'session';
  IF NOT control.agent_activity_object_shape(s,ARRAY['matchingServiceCount','status','processId','startupAtUtc'])
   OR NOT control.agent_activity_integer(s->'matchingServiceCount',0,2147483647)
   OR NOT control.agent_activity_integer(s->'status',1,7,true) OR NOT control.agent_activity_integer(s->'processId',0,2147483647,true)
   OR NOT control.agent_activity_date(s->'startupAtUtc',false)
   OR NOT control.agent_activity_object_shape(session_value,ARRAY['sessionId','agentStartLocal'])
   OR NOT control.agent_activity_integer(session_value->'sessionId',1,2147483647,true)
   OR NOT control.agent_activity_date(session_value->'agentStartLocal',true) THEN
   RAISE EXCEPTION 'Agent activity service/session shape rejected' USING ERRCODE='22023'; END IF;
 END LOOP;
 IF control.agent_activity_utc_ticks(p_value->'after'->>'sourceObservedAtUtc')<control.agent_activity_utc_ticks(p_value->'before'->>'sourceObservedAtUtc')
  OR control.agent_activity_utc_ticks(p_value->'after'->>'sourceObservedAtUtc')-control.agent_activity_utc_ticks(p_value->'before'->>'sourceObservedAtUtc')>100000000 THEN
  RAISE EXCEPTION 'Agent activity bracket rejected' USING ERRCODE='22023'; END IF;
 FOR j IN SELECT value FROM jsonb_array_elements(p_value->'jobs') LOOP
  IF NOT control.agent_activity_object_shape(j,ARRAY['jobId','sessionId','requestedLocal','queuedLocal','startExecutionLocal','stopExecutionLocal','lastExecutedStepId','lastExecutedStepLocal'])
   OR jsonb_typeof(j->'jobId') IS DISTINCT FROM 'string' OR j->>'jobId' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
   OR j->>'jobId'='00000000-0000-0000-0000-000000000000'
   OR (previous_id IS NOT NULL AND (j->>'jobId') COLLATE "C"<=previous_id COLLATE "C")
   OR NOT control.agent_activity_integer(j->'sessionId',1,2147483647)
   OR j->'sessionId' IS DISTINCT FROM p_value->'before'->'session'->'sessionId'
   OR NOT control.agent_activity_integer(j->'lastExecutedStepId',0,2147483647,true) THEN
   RAISE EXCEPTION 'Agent activity job identity rejected' USING ERRCODE='22023'; END IF;
  previous_id:=j->>'jobId';
  FOREACH k IN ARRAY ARRAY['requestedLocal','queuedLocal','startExecutionLocal','stopExecutionLocal','lastExecutedStepLocal'] LOOP
   IF NOT control.agent_activity_date(j->k,true) THEN RAISE EXCEPTION 'Agent activity local date rejected' USING ERRCODE='22023'; END IF;
  END LOOP;
 END LOOP;
END $validate$;
REVOKE ALL ON FUNCTION control.agent_activity_object_shape(jsonb,text[]),control.agent_activity_date(jsonb,boolean),
 control.agent_activity_utc_ticks(text),control.agent_activity_authorization_ticks(text),
 control.agent_activity_integer(jsonb,bigint,bigint,boolean),control.validate_sql_agent_activity_observation(jsonb,uuid,bigint,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
CREATE FUNCTION control.commit_sql_agent_activity_collection_run(
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
 v_capture jsonb; enrollment_row control.sql_agent_module_enrollment_command%ROWTYPE; draining_failure boolean:=false;
 now_utc timestamptz; is_failure boolean;
 inserted integer:=0; written_bytes integer:=0;
BEGIN
 is_failure:=p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'sql-agent.activity-observations' OR p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1
  OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<=0
  OR p_schedule_revision IS NULL OR p_schedule_revision<=0 OR p_scheduled_at IS NULL OR NOT isfinite(p_scheduled_at)
  OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token<=0
  OR octet_length(p_request_digest) IS DISTINCT FROM 32 OR octet_length(p_completion_digest) IS DISTINCT FROM 32
  OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR octet_length(p_payload::text)>530000
  OR p_completion_digest IS DISTINCT FROM sha256(convert_to(p_payload::text,'UTF8'))
  OR p_outcome IS NULL OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
  OR p_reason_code IS NULL OR p_reason_code NOT IN ('completed','source_row_limit','response_byte_limit','deadline_exceeded','transient_target_failure','permanent_target_failure','required_permission_missing','target_unsupported','output_validation_failed','lease_ownership_lost','circuit_currently_open','capability_profile_missing','capability_profile_stale','capability_missing','target_version_unsupported','target_platform_unsupported','target_edition_unsupported','visibility_incomplete')
  OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 0 AND 1 OR p_attempt_count=0 AND p_outcome<>'circuit_open'
  OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
  OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 513 OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 1
  OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 524288 OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 524288
  OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 513 OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 524288
  OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL OR p_next_circuit_state NOT IN ('closed','open','half_open')
  OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
  OR p_loss_kind IS NULL OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit','output_validation_failure','visibility_incomplete')
  OR p_outcome='succeeded' AND p_loss_kind<>'none'
  OR p_outcome='succeeded' AND p_reason_code<>'completed' OR p_outcome='partial' AND p_reason_code<>'source_row_limit'
  OR p_outcome='partial' AND (p_loss_kind<>'source_row_limit' OR p_minimum_lost_items<>1 OR p_loss_count_is_exact OR p_minimum_lost_bytes<>0)
  OR p_loss_kind='none' AND (p_minimum_lost_items<>0 OR p_minimum_lost_bytes<>0 OR NOT p_loss_count_is_exact)
  OR p_loss_kind<>'none' AND p_minimum_lost_items=0 AND p_minimum_lost_bytes=0
  OR is_failure AND (p_source_row_count<>0 OR p_response_bytes<>0 OR p_output_item_count<>0 OR p_output_bytes<>0 OR
       p_outcome='output_invalid' AND (p_loss_kind<>'output_validation_failure' OR p_minimum_lost_items<>1 OR p_loss_count_is_exact OR p_minimum_lost_bytes<>0) OR
       p_outcome<>'output_invalid' AND p_loss_kind<>'none')
 THEN RAISE EXCEPTION 'Agent activity completion bounds rejected' USING ERRCODE='22023'; END IF;
 IF p_run_id='00000000-0000-0000-0000-000000000000'::uuid OR p_instance_id='00000000-0000-0000-0000-000000000000'::uuid OR
  p_owner_execution_id='00000000-0000-0000-0000-000000000000'::uuid OR
  NOT p_payload ?& ARRAY['schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceRowsRead','truncated','observation'] OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(p_payload) k WHERE k NOT IN ('schemaVersion','targetId','targetRevision','runId','outcome','reason','sourceRowsRead','truncated','observation')) OR
  p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text OR
  p_payload->'targetRevision' IS DISTINCT FROM to_jsonb(p_target_revision) OR p_payload->>'runId' IS DISTINCT FROM p_run_id::text OR
  p_payload->>'outcome' IS DISTINCT FROM p_outcome OR p_payload->>'reason' IS DISTINCT FROM p_reason_code OR
  p_payload->'sourceRowsRead' IS DISTINCT FROM to_jsonb(p_source_row_count) OR p_payload->'truncated' IS DISTINCT FROM to_jsonb(p_outcome='partial') THEN
  RAISE EXCEPTION 'Agent activity execution envelope rejected' USING ERRCODE='22023'; END IF;
 v_capture:=p_payload->'observation';
 IF is_failure THEN
  IF v_capture IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'Agent activity failure contains fabricated source evidence' USING ERRCODE='22023'; END IF;
 ELSE
  PERFORM control.validate_sql_agent_activity_observation(v_capture,p_instance_id,p_target_revision,p_run_id);
  IF p_source_row_count<>(v_capture->>'sourceRowsRead')::integer OR p_output_item_count<>1 OR
   p_output_bytes<>4096+jsonb_array_length(v_capture->'jobs')*768 OR p_response_bytes<>4096+p_source_row_count*768
   OR v_capture->'truncated' IS DISTINCT FROM to_jsonb(p_outcome='partial') THEN
   RAISE EXCEPTION 'Agent activity source accounting rejected' USING ERRCODE='22023'; END IF;
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-target:'||p_instance_id::text,0));
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision
    OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version
    OR run_row.output_schema_version<>p_output_schema_version
    OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key
    OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token
    OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at
 THEN RAISE EXCEPTION 'Agent activity capture run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='sql-agent.activity-observations' AND collector_version=1;
 IF NOT FOUND OR contract_row.output_schema_version<>1
 THEN RAISE EXCEPTION 'Agent activity capture immutable collector contract missing' USING ERRCODE='55000'; END IF;
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
  THEN RAISE EXCEPTION 'Agent activity capture divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
 THEN RAISE EXCEPTION 'Agent activity capture schedule ownership mismatch' USING ERRCODE='55000'; END IF;
 draining_failure:=is_failure AND schedule_row.schedule_revision>p_schedule_revision;
 IF NOT draining_failure AND (schedule_row.target_revision<>p_target_revision OR schedule_row.schedule_revision<>p_schedule_revision OR schedule_row.next_due_at<>p_scheduled_at) THEN
  RAISE EXCEPTION 'Agent activity capture schedule fence mismatch' USING ERRCODE='55000'; END IF;
 IF NOT draining_failure AND ((p_outcome IN ('succeeded','partial')
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
) THEN RAISE EXCEPTION 'Agent activity capture circuit transition mismatch' USING ERRCODE='22023'; END IF;


 -- The target trigger updates this locked schedule in the same transaction as
 -- target configuration changes. Reading target state here preserves that fence
 -- without acquiring a reversed target-row lock after the run lock.
 IF NOT draining_failure AND NOT EXISTS(SELECT 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id AND
  t.revision=p_target_revision AND t.lifecycle_state='active' AND t.host_name IS NOT NULL) THEN
  RAISE EXCEPTION 'Agent activity current target fence mismatch' USING ERRCODE='55000'; END IF;
 now_utc:=clock_timestamp();
 IF NOT is_failure THEN
  SELECT * INTO enrollment_row FROM control.sql_agent_module_enrollment_command c
  WHERE c.instance_id=p_instance_id AND c.target_revision=p_target_revision ORDER BY c.command_revision DESC LIMIT 1;
  IF NOT FOUND OR enrollment_row.enrollment IS NULL
   OR enrollment_row.command_revision<>(v_capture->>'enrollmentRevision')::bigint
   OR enrollment_row.enrollment->>'enrollmentId' IS DISTINCT FROM v_capture->>'enrollmentId'
   OR enrollment_row.enrollment->>'evidenceSha256' IS DISTINCT FROM v_capture->>'evidenceSha256'
   OR extract(epoch FROM now_utc)*10000000<control.agent_activity_authorization_ticks(enrollment_row.enrollment->>'notBeforeUtc')
   OR extract(epoch FROM now_utc)*10000000>=control.agent_activity_authorization_ticks(enrollment_row.enrollment->>'expiresAtUtc')
   OR control.agent_activity_utc_ticks(v_capture->'before'->>'sourceObservedAtUtc')<control.agent_activity_authorization_ticks(enrollment_row.enrollment->>'notBeforeUtc')
   OR control.agent_activity_utc_ticks(v_capture->'after'->>'sourceObservedAtUtc')>=control.agent_activity_authorization_ticks(enrollment_row.enrollment->>'expiresAtUtc')
   OR control.agent_activity_utc_ticks(v_capture->'before'->>'sourceObservedAtUtc')<extract(epoch FROM run_row.started_at-interval '5 minutes')*10000000
   OR control.agent_activity_utc_ticks(v_capture->'after'->>'sourceObservedAtUtc')>extract(epoch FROM now_utc+interval '5 minutes')*10000000
  THEN RAISE EXCEPTION 'Agent activity current enrollment fence mismatch' USING ERRCODE='55000'; END IF;
 END IF;
 INSERT INTO telemetry.sql_agent_activity_receipt(instance_id,run_id,target_revision,completed_at,source_schema_version,
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
 IF draining_failure THEN
  -- Administration advanced this schedule. Complete only the owned run;
  -- do not rewrite its new settings, revision, next due time, or circuit.
  UPDATE control.collector_schedule SET active_run_id=NULL,updated_at=now_utc
  WHERE instance_id=p_instance_id AND collector_id=p_collector_id AND active_run_id=p_run_id;
 ELSE
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
 END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 'committed',inserted,0,
   CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,
   written_bytes,now_utc;

EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow THEN
 RAISE EXCEPTION 'Agent activity scalar shape rejected' USING ERRCODE='22023';
END $fn$;
REVOKE ALL ON FUNCTION control.commit_sql_agent_activity_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_sql_agent_activity_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea) TO sqlobserver_collector;



-- Historical0369command stays immutable; only this fenced entrypoint is callable.
ALTER FUNCTION control.set_sql_agent_module_enrollment(uuid,bigint,bigint,uuid,jsonb,text,uuid)
 RENAME TO set_sql_agent_module_enrollment_unfenced_v1;
REVOKE ALL ON FUNCTION control.set_sql_agent_module_enrollment_unfenced_v1(uuid,bigint,bigint,uuid,jsonb,text,uuid)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION control.set_sql_agent_module_enrollment(p_instance uuid,p_target_revision bigint,p_expected_revision bigint,
 p_operation uuid,p_enrollment jsonb,p_actor text,p_correlation uuid)
RETURNS TABLE(result_status text,command_revision bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $fenced_command$
DECLARE result record;
BEGIN
 -- The historical setter validates malformed/null arguments. No null lock key
 -- grants access; valid operations use its original operation→target lock order.
 IF p_operation IS NOT NULL THEN
  PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-operation:'||p_operation::text,0));
 END IF;
 IF p_instance IS NOT NULL THEN
  PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-target:'||p_instance::text,0));
 END IF;
 SELECT * INTO result FROM control.set_sql_agent_module_enrollment_unfenced_v1(
  p_instance,p_target_revision,p_expected_revision,p_operation,p_enrollment,p_actor,p_correlation);
 IF result.result_status='applied' THEN
  IF EXISTS(SELECT 1 FROM control.collector_schedule WHERE instance_id=p_instance
   AND collector_id='sql-agent.activity-observations' AND schedule_revision>=9007199254740991) THEN
   RAISE EXCEPTION 'Agent activity schedule revision exhausted' USING ERRCODE='22023'; END IF;
  UPDATE control.collector_schedule SET enabled=false,schedule_revision=schedule_revision+1,updated_at=clock_timestamp()
  WHERE instance_id=p_instance AND collector_id='sql-agent.activity-observations';
 END IF;
 RETURN QUERY SELECT result.result_status::text,result.command_revision::bigint;
END $fenced_command$;
REVOKE ALL ON FUNCTION control.set_sql_agent_module_enrollment(uuid,bigint,bigint,uuid,jsonb,text,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_sql_agent_module_enrollment(uuid,bigint,bigint,uuid,jsonb,text,uuid) TO sqlobserver_server;
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
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline','deadlock_read_receipt','plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link','server_lifecycle_receipt','server_lifecycle_event','cpu_paired_capture_receipt','cpu_component_receipt','sql_agent_activity_receipt'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy','collection_run_canonical',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links','server_lifecycle_receipts','server_lifecycle_events','cpu_paired_captures','cpu_components','sql_agent_activity_observations'));
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
   OR (data_class='sql_agent_activity_observations' AND parent_schema='telemetry' AND parent_table='sql_agent_activity_receipt' AND partition_granularity='day')
   OR (data_class='cpu_components' AND parent_schema='telemetry' AND parent_table='cpu_component_receipt' AND partition_granularity='day')
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
 ('telemetry.sql_agent_activity_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.cpu_component_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.cpu_paired_capture_receipt'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char"),
 ('telemetry.replication_snapshot_v2'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char");
$inventory$;
REVOKE ALL ON FUNCTION system.collection_run_reviewed_references() FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.server_lifecycle_receipt,events.server_lifecycle_event,control.server_lifecycle_checkpoint
 TO sqlobserver_run_dependency_reader;

INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('sql_agent_activity_observations','telemetry','sql_agent_activity_receipt','day',false,NULL,3,'migration-0370');
CREATE FUNCTION control.ensure_sql_agent_activity_partitions(p_anchor date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $partitions$
DECLARE v_day date; v_parent_name text; v_schema text; v_name text; v_parent oid; v_child oid; v_created integer:=0;
BEGIN
 IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'Agent activity partition anchor must be current UTC date' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-activity:partition-set',0));
 FOREACH v_parent_name IN ARRAY ARRAY['sql_agent_activity_receipt'] LOOP
  v_schema:='telemetry';
  v_parent:=format('%I.%I',v_schema,v_parent_name)::regclass;
  IF NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.oid=v_parent AND c.relkind='p' AND
   c.relowner='sqlobserver_migrator'::regrole AND c.relrowsecurity AND c.relforcerowsecurity) THEN
   RAISE EXCEPTION 'Agent activity partition parent security unverified' USING ERRCODE='55000'; END IF;
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
    RAISE EXCEPTION 'Agent activity partition identity or immutability unverified' USING ERRCODE='55000'; END IF;
   EXECUTE format('REVOKE ALL ON %I.%I FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor',v_schema,v_name);
   EXECUTE format('GRANT SELECT ON %I.%I TO sqlobserver_run_dependency_reader',v_schema,v_name);
   INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
   VALUES(v_schema,v_parent_name,v_schema,v_name,'day',v_day::timestamptz,(v_day+1)::timestamptz) ON CONFLICT DO NOTHING;
   IF NOT EXISTS(SELECT 1 FROM system.partition_registry r WHERE r.parent_schema=v_schema AND r.parent_table=v_parent_name AND
    r.partition_schema=v_schema AND r.partition_name=v_name AND r.partition_granularity='day' AND r.lifecycle_state='attached' AND
    r.range_start=v_day::timestamptz AND r.range_end=(v_day+1)::timestamptz) THEN
    RAISE EXCEPTION 'Agent activity partition registry unverified' USING ERRCODE='55000'; END IF;
  END LOOP;
 END LOOP;
 RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_sql_agent_activity_partitions(date) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.ensure_sql_agent_activity_partition_set(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $fenced$
DECLARE v_created integer;
BEGIN
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 v_created:=control.ensure_sql_agent_activity_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 RETURN v_created;
END $fenced$;
REVOKE ALL ON FUNCTION control.ensure_sql_agent_activity_partition_set(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_sql_agent_activity_partition_set(uuid,bigint) TO sqlobserver_collector;
-- Bootstrap does not activate a source or a retention policy.
SELECT control.ensure_sql_agent_activity_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);

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
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links','server_lifecycle_receipts','server_lifecycle_events','cpu_paired_captures','cpu_components','sql_agent_activity_observations')
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
    OR (p_data_class IN ('server_lifecycle_receipts','server_lifecycle_events','cpu_paired_captures','cpu_components','sql_agent_activity_observations')
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
