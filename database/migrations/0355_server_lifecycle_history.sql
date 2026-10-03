-- Staged lifecycle source receipts and sampled detections. No production collector activation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE telemetry.server_lifecycle_receipt
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 completed_at timestamptz NOT NULL CHECK(isfinite(completed_at)),
 source_observed_at timestamptz CHECK(isfinite(source_observed_at)),
 source_schema_version integer NOT NULL CHECK(source_schema_version=1),
 identity_version integer NOT NULL CHECK(identity_version=1),
 previous_run_id uuid,
 outcome text NOT NULL,
 loss_kind text NOT NULL,
 engine_coverage smallint NOT NULL CHECK(engine_coverage BETWEEN 1 AND 3),
 availability_coverage smallint NOT NULL CHECK(availability_coverage BETWEEN 1 AND 4),
 comparison_boundary_reason text NOT NULL CHECK(comparison_boundary_reason IN
  ('initial','canonical_attempt_gap','retained_predecessor_unavailable','retirement_boundary',
   'revision_boundary','schema_boundary','collection_gap','identity_unavailable','identity_boundary',
   'source_clock_boundary','startup_identity_unqualified','counter_epoch_unqualified','comparable')),
 source_consistency_verified boolean NOT NULL CHECK(NOT source_consistency_verified),
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object' AND octet_length(payload::text)<=1048576),
 completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32),
 PRIMARY KEY(instance_id,completed_at,run_id)
) PARTITION BY RANGE(completed_at);
CREATE INDEX ix_server_lifecycle_receipt_run ON telemetry.server_lifecycle_receipt(run_id);
CREATE INDEX ix_server_lifecycle_receipt_target_time ON telemetry.server_lifecycle_receipt(instance_id,completed_at DESC,run_id);

CREATE TABLE events.server_lifecycle_event
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 event_id uuid NOT NULL,
 sample_target_revision bigint NOT NULL CHECK(sample_target_revision>0),
 before_run_id uuid NOT NULL,
 after_run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 kind text NOT NULL CHECK(kind IN ('restart_detected','responding_node_changed')),
 affected_identity_fingerprint bytea NOT NULL CHECK(octet_length(affected_identity_fingerprint)=32),
 before_observed_at timestamptz NOT NULL CHECK(isfinite(before_observed_at)),
 after_observed_at timestamptz NOT NULL CHECK(isfinite(after_observed_at)),
 before_completed_at timestamptz NOT NULL CHECK(isfinite(before_completed_at)),
 detected_at timestamptz NOT NULL CHECK(isfinite(detected_at)),
 occurred_at timestamptz CHECK(occurred_at IS NULL),
 source_schema_version integer NOT NULL CHECK(source_schema_version=1),
 identity_version integer NOT NULL CHECK(identity_version=1),
 source_consistency_verified boolean NOT NULL CHECK(NOT source_consistency_verified),
 before_engine jsonb NOT NULL CHECK(jsonb_typeof(before_engine)='object'),
 after_engine jsonb NOT NULL CHECK(jsonb_typeof(after_engine)='object'),
 before_completion_digest bytea NOT NULL CHECK(octet_length(before_completion_digest)=32),
 after_completion_digest bytea NOT NULL CHECK(octet_length(after_completion_digest)=32),
 PRIMARY KEY(instance_id,detected_at,event_id),
 CHECK(before_run_id<>after_run_id AND before_observed_at<after_observed_at AND
       before_observed_at<=before_completed_at AND after_observed_at<=detected_at AND before_completed_at<detected_at)
) PARTITION BY RANGE(detected_at);
CREATE INDEX ix_server_lifecycle_event_run ON events.server_lifecycle_event(after_run_id);
CREATE INDEX ix_server_lifecycle_event_target_time ON events.server_lifecycle_event(instance_id,detected_at DESC,event_id DESC);
COMMENT ON TABLE events.server_lifecycle_event IS
 'Sampled detection only. SQL-reported responding names are not hardware UUIDs. Physical occurrence UTC, cause and intervening transition count are unknown. Schema 1 cannot prove AG failovers.';

CREATE TABLE control.server_lifecycle_checkpoint
(
 instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 completed_at timestamptz NOT NULL CHECK(isfinite(completed_at)),
 completion_digest bytea NOT NULL CHECK(octet_length(completion_digest)=32)
);
CREATE INDEX ix_server_lifecycle_checkpoint_run ON control.server_lifecycle_checkpoint(run_id);
DO $security$
DECLARE v_name text; v_schema text;
BEGIN
 FOREACH v_name IN ARRAY ARRAY['server_lifecycle_receipt','server_lifecycle_event','server_lifecycle_checkpoint'] LOOP
  v_schema:=CASE v_name WHEN 'server_lifecycle_receipt' THEN 'telemetry' WHEN 'server_lifecycle_event' THEN 'events' ELSE 'control' END;
  EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',v_schema,v_name);
  EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',v_schema,v_name);
  EXECUTE format('CREATE POLICY %I ON %I.%I USING(instance_id::text=current_setting(''sqlobserver.target_scope'',true)) WITH CHECK(instance_id::text=current_setting(''sqlobserver.target_scope'',true))',v_name||'_scope',v_schema,v_name);
  EXECUTE format('CREATE POLICY %I ON %I.%I FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true)',v_name||'_migrator',v_schema,v_name);
  EXECUTE format('REVOKE ALL ON %I.%I FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor',v_schema,v_name);
  IF v_name<>'server_lifecycle_checkpoint' THEN
   EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON %I.%I FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation()',v_name||'_append_only',v_schema,v_name);
  END IF;
 END LOOP;
END $security$;

-- Exact .NET offset-free 100ns ticks; PostgreSQL timestamps themselves have microsecond precision.
CREATE FUNCTION control.server_lifecycle_local_ticks(p_local text)
RETURNS numeric LANGUAGE plpgsql IMMUTABLE STRICT SET search_path=pg_catalog
AS $ticks$
DECLARE v_time timestamp; v_ticks numeric;
BEGIN
 IF p_local !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}$' THEN
  RAISE EXCEPTION 'lifecycle local startup shape rejected' USING ERRCODE='22023'; END IF;
 v_time:=left(p_local,length(p_local)-1)::timestamp;
 IF NOT isfinite(v_time) OR v_time<timestamp '1753-01-01' OR v_time>=timestamp '10000-01-01'
  OR to_char(v_time,'YYYY-MM-DD"T"HH24:MI:SS.US')<>left(p_local,length(p_local)-1) THEN
  RAISE EXCEPTION 'lifecycle local startup calendar rejected' USING ERRCODE='22023'; END IF;
 v_ticks:=extract(epoch FROM (v_time-timestamp '0001-01-01'))*10000000+right(p_local,1)::integer;
 RETURN trunc(v_ticks);
END $ticks$;
REVOKE ALL ON FUNCTION control.server_lifecycle_local_ticks(text) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.server_lifecycle_startup_fingerprint(p_engine jsonb)
RETURNS text LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog
AS $startup$
DECLARE v_input text;
BEGIN
 IF p_engine->>'physicalNodeFingerprint' IS NULL OR p_engine->>'logicalInstanceFingerprint' IS NULL OR
    p_engine->>'processId' IS NULL OR p_engine->>'sourceStartLocal' IS NULL OR p_engine->>'startMsTicks' IS NULL THEN RETURN NULL; END IF;
 v_input:=concat_ws('|',p_engine->>'physicalNodeFingerprint',p_engine->>'logicalInstanceFingerprint',
  p_engine->>'processId',control.server_lifecycle_local_ticks(p_engine->>'sourceStartLocal')::text,p_engine->>'startMsTicks');
 RETURN encode(sha256(convert_to('sqlobserver-lifecycle-startup-v1','UTF8')||decode('00','hex')||convert_to(v_input,'UTF8')),'hex');
END $startup$;
REVOKE ALL ON FUNCTION control.server_lifecycle_startup_fingerprint(jsonb) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.server_lifecycle_event_id(p_target uuid,p_revision bigint,p_schema integer,p_kind integer,p_before uuid,p_after uuid,p_identity text)
RETURNS uuid LANGUAGE plpgsql IMMUTABLE STRICT SET search_path=pg_catalog
AS $identity$
DECLARE v_hash bytea;
BEGIN
 v_hash:=sha256(convert_to(concat_ws('|','sqlobserver-lifecycle-event-v1',p_target::text,p_revision::text,p_schema::text,p_kind::text,p_before::text,p_after::text,p_identity),'UTF8'));
 v_hash:=set_byte(v_hash,6,(get_byte(v_hash,6)&15)|128);
 v_hash:=set_byte(v_hash,8,(get_byte(v_hash,8)&63)|128);
 RETURN encode(substring(v_hash FROM 1 FOR 16),'hex')::uuid;
END $identity$;
REVOKE ALL ON FUNCTION control.server_lifecycle_event_id(uuid,bigint,integer,integer,uuid,uuid,text) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.commit_server_lifecycle_collection_run(
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
 run_row telemetry.collection_run%ROWTYPE;
 schedule_row control.collector_schedule%ROWTYPE;
 contract_row control.collector_contract%ROWTYPE;
 prior telemetry.collection_run_outcome%ROWTYPE;
 v_previous_run telemetry.collection_run_v2%ROWTYPE;
 v_previous_receipt telemetry.server_lifecycle_receipt%ROWTYPE;
 v_checkpoint control.server_lifecycle_checkpoint%ROWTYPE;
 v_previous_outcome telemetry.collection_run_outcome%ROWTYPE;
 v_engine jsonb; v_old_engine jsonb; v_member jsonb;
 v_engine_coverage integer; v_ag_coverage integer; v_count integer;
 v_observed timestamptz; v_previous_id uuid; v_boundary text:='initial';
 v_kind text; v_kind_number integer; v_identity text; v_engine_qualified boolean; v_old_qualified boolean;
 now_utc timestamptz:=clock_timestamp(); is_failure boolean;
 inserted integer:=0; written_bytes integer:=0;
BEGIN
 is_failure:=p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'server.lifecycle' OR p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1
  OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<=0
  OR p_schedule_revision IS NULL OR p_schedule_revision<=0 OR p_scheduled_at IS NULL OR NOT isfinite(p_scheduled_at)
  OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token<=0
  OR octet_length(p_request_digest) IS DISTINCT FROM 32 OR octet_length(p_completion_digest) IS DISTINCT FROM 32
  OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR octet_length(p_payload::text)>1048576
  OR p_completion_digest IS DISTINCT FROM sha256(convert_to(p_payload::text,'UTF8'))
  OR p_outcome IS NULL OR p_outcome NOT IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
  OR p_reason_code IS NULL OR p_reason_code NOT IN ('completed','source_row_limit','response_byte_limit','deadline_exceeded','transient_target_failure','permanent_target_failure','required_permission_missing','target_unsupported','output_validation_failed','lease_ownership_lost','circuit_currently_open','capability_profile_missing','capability_profile_stale','capability_missing','target_version_unsupported','target_platform_unsupported','target_edition_unsupported','visibility_incomplete')
  OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 0 AND 2 OR p_attempt_count=0 AND p_outcome<>'circuit_open'
  OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
  OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 1025 OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 1024
  OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 1048576 OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 1048576
  OR p_output_bytes>p_response_bytes
  OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 1025 OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 1048576
  OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL OR p_next_circuit_state NOT IN ('closed','open','half_open')
  OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
  OR p_loss_kind IS NULL OR p_loss_kind NOT IN ('none','source_row_limit','response_byte_limit','output_validation_failure','visibility_incomplete')
  OR p_loss_kind='none' AND (p_minimum_lost_items<>0 OR p_minimum_lost_bytes<>0 OR NOT p_loss_count_is_exact)
  OR p_loss_kind<>'none' AND p_minimum_lost_items=0 AND p_minimum_lost_bytes=0
  OR is_failure AND (p_output_item_count<>0 OR p_output_bytes<>0 OR
       p_outcome='output_invalid' AND (p_loss_kind<>'output_validation_failure' OR p_minimum_lost_items<1) OR
       p_outcome<>'output_invalid' AND p_loss_kind<>'none')
 THEN RAISE EXCEPTION 'lifecycle completion bounds rejected' USING ERRCODE='22023'; END IF;
 IF NOT p_payload ?& ARRAY['schemaVersion','identityVersion','targetId','targetRevision','runId','outcome','reason','sourceObservedAtUtc','sourceRowsRead','truncated','engineCoverage','availabilityCoverage','engine','isHadrEnabled','members','sourceConsistencyVerified']
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_payload) f(name) WHERE f.name NOT IN ('schemaVersion','identityVersion','targetId','targetRevision','runId','outcome','reason','sourceObservedAtUtc','sourceRowsRead','truncated','engineCoverage','availabilityCoverage','engine','isHadrEnabled','members','sourceConsistencyVerified'))
  OR p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb OR p_payload->'identityVersion' IS DISTINCT FROM '1'::jsonb
  OR jsonb_typeof(p_payload->'targetId') IS DISTINCT FROM 'string' OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
  OR p_payload->'targetRevision' IS DISTINCT FROM to_jsonb(p_target_revision)
  OR jsonb_typeof(p_payload->'runId') IS DISTINCT FROM 'string' OR p_payload->>'runId' IS DISTINCT FROM p_run_id::text
  OR jsonb_typeof(p_payload->'outcome') IS DISTINCT FROM 'string' OR p_payload->>'outcome' IS DISTINCT FROM p_outcome
  OR jsonb_typeof(p_payload->'reason') IS DISTINCT FROM 'string' OR p_payload->>'reason' IS DISTINCT FROM p_reason_code
  OR p_payload->'sourceRowsRead' IS DISTINCT FROM to_jsonb(p_source_row_count)
  OR jsonb_typeof(p_payload->'truncated') IS DISTINCT FROM 'boolean' OR jsonb_typeof(p_payload->'members') IS DISTINCT FROM 'array'
  OR p_payload->'sourceConsistencyVerified' IS DISTINCT FROM 'false'::jsonb
  OR jsonb_typeof(p_payload->'engineCoverage') IS DISTINCT FROM 'number' OR p_payload->'engineCoverage' NOT IN ('1'::jsonb,'2'::jsonb,'3'::jsonb)
  OR jsonb_typeof(p_payload->'availabilityCoverage') IS DISTINCT FROM 'number' OR p_payload->'availabilityCoverage' NOT IN ('1'::jsonb,'2'::jsonb,'3'::jsonb,'4'::jsonb)
  OR jsonb_typeof(p_payload->'isHadrEnabled') NOT IN ('null','boolean')
 THEN RAISE EXCEPTION 'lifecycle closed envelope rejected' USING ERRCODE='22023'; END IF;
 v_count:=jsonb_array_length(p_payload->'members');
 IF v_count>1023 THEN RAISE EXCEPTION 'lifecycle membership exceeds source bound' USING ERRCODE='22023'; END IF;
 v_engine:=p_payload->'engine';
 IF is_failure THEN
  IF v_engine IS DISTINCT FROM 'null'::jsonb OR p_payload->'sourceObservedAtUtc' IS DISTINCT FROM 'null'::jsonb OR
   p_payload->'isHadrEnabled' IS DISTINCT FROM 'null'::jsonb OR v_count<>0 OR p_payload->'truncated' IS DISTINCT FROM 'false'::jsonb OR
   p_payload->'engineCoverage' IS DISTINCT FROM '3'::jsonb OR p_payload->'availabilityCoverage' IS DISTINCT FROM '3'::jsonb THEN
   RAISE EXCEPTION 'lifecycle failure contains fabricated source evidence' USING ERRCODE='22023'; END IF;
  v_engine_coverage:=3; v_ag_coverage:=3;
 ELSE
  IF jsonb_typeof(p_payload->'sourceObservedAtUtc') IS DISTINCT FROM 'string' OR
    p_payload->>'sourceObservedAtUtc' !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$' OR
    NOT isfinite((p_payload->>'sourceObservedAtUtc')::timestamptz) OR
    jsonb_typeof(v_engine) IS DISTINCT FROM 'object' OR
    NOT v_engine ?& ARRAY['physicalNodeFingerprint','logicalInstanceFingerprint','processId','sourceStartLocal','startMsTicks','currentMsTicks','startupIncarnationFingerprint','coverage','hardwareIdentityVerified','occurredAtUtc'] OR
    EXISTS(SELECT 1 FROM jsonb_object_keys(v_engine) f(name) WHERE f.name NOT IN ('physicalNodeFingerprint','logicalInstanceFingerprint','processId','sourceStartLocal','startMsTicks','currentMsTicks','startupIncarnationFingerprint','coverage','hardwareIdentityVerified','occurredAtUtc')) OR
    v_engine->'hardwareIdentityVerified' IS DISTINCT FROM 'false'::jsonb OR v_engine->'occurredAtUtc' IS DISTINCT FROM 'null'::jsonb
   THEN RAISE EXCEPTION 'lifecycle engine envelope rejected' USING ERRCODE='22023'; END IF;
  IF EXISTS(SELECT 1 FROM jsonb_each(v_engine) f(name,value)
    WHERE f.name IN ('physicalNodeFingerprint','logicalInstanceFingerprint','startupIncarnationFingerprint') AND
     jsonb_typeof(f.value) NOT IN ('null','string') OR
     f.name IN ('physicalNodeFingerprint','logicalInstanceFingerprint','startupIncarnationFingerprint') AND
     f.value<>'null'::jsonb AND f.value #>> '{}' !~ '^[0-9a-f]{64}$') OR
   EXISTS(SELECT 1 FROM jsonb_each(v_engine) f(name,value) WHERE f.name IN ('processId','startMsTicks','currentMsTicks') AND
    f.value<>'null'::jsonb AND (jsonb_typeof(f.value)<>'number' OR f.value::text !~ '^(0|[1-9][0-9]{0,18})$' OR
     f.value::text::numeric>CASE f.name WHEN 'processId' THEN 2147483647::numeric ELSE 9223372036854775807::numeric END)) OR
   v_engine->>'processId'='0' OR jsonb_typeof(v_engine->'sourceStartLocal') NOT IN ('null','string') THEN
   RAISE EXCEPTION 'lifecycle engine identity or counters rejected' USING ERRCODE='22023'; END IF;
  IF v_engine->>'sourceStartLocal' IS NOT NULL THEN PERFORM control.server_lifecycle_local_ticks(v_engine->>'sourceStartLocal'); END IF;
  IF v_engine->>'startupIncarnationFingerprint' IS DISTINCT FROM control.server_lifecycle_startup_fingerprint(v_engine) THEN
   RAISE EXCEPTION 'lifecycle startup incarnation seal rejected' USING ERRCODE='22023'; END IF;
  v_engine_coverage:=CASE WHEN v_engine->>'physicalNodeFingerprint' IS NULL OR v_engine->>'logicalInstanceFingerprint' IS NULL OR
   v_engine->>'processId' IS NULL OR v_engine->>'sourceStartLocal' IS NULL OR v_engine->>'startMsTicks' IS NULL OR v_engine->>'currentMsTicks' IS NULL THEN 3
   WHEN (v_engine->>'startMsTicks')::bigint>(v_engine->>'currentMsTicks')::bigint THEN 2 ELSE 1 END;
  IF v_engine->'coverage' IS DISTINCT FROM to_jsonb(v_engine_coverage) OR p_payload->'engineCoverage' IS DISTINCT FROM to_jsonb(v_engine_coverage) THEN
   RAISE EXCEPTION 'lifecycle engine coverage rejected' USING ERRCODE='22023'; END IF;
  FOR v_member IN SELECT value FROM jsonb_array_elements(p_payload->'members') LOOP
   IF jsonb_typeof(v_member) IS DISTINCT FROM 'object' OR NOT v_member ?& ARRAY['groupFingerprint','replicaFingerprint','observerReplicaFingerprint','role','observerRole','stateObserved','expectedGroupMembers'] OR
    EXISTS(SELECT 1 FROM jsonb_object_keys(v_member) f(name) WHERE f.name NOT IN ('groupFingerprint','replicaFingerprint','observerReplicaFingerprint','role','observerRole','stateObserved','expectedGroupMembers')) OR
    jsonb_typeof(v_member->'groupFingerprint') IS DISTINCT FROM 'string' OR v_member->>'groupFingerprint' !~ '^[0-9a-f]{64}$' OR
    jsonb_typeof(v_member->'replicaFingerprint') IS DISTINCT FROM 'string' OR v_member->>'replicaFingerprint' !~ '^[0-9a-f]{64}$' OR
    jsonb_typeof(v_member->'observerReplicaFingerprint') NOT IN ('null','string') OR v_member->>'observerReplicaFingerprint' !~ '^[0-9a-f]{64}$' OR
    jsonb_typeof(v_member->'role') NOT IN ('null','string') OR v_member->>'role' NOT IN ('PRIMARY','SECONDARY','RESOLVING') OR
    jsonb_typeof(v_member->'observerRole') NOT IN ('null','string') OR v_member->>'observerRole' NOT IN ('PRIMARY','SECONDARY','RESOLVING') OR
    jsonb_typeof(v_member->'stateObserved') IS DISTINCT FROM 'boolean' OR
    v_member->'stateObserved'='false'::jsonb AND v_member->'role'<>'null'::jsonb OR
    v_member->'expectedGroupMembers'<>'null'::jsonb AND (jsonb_typeof(v_member->'expectedGroupMembers')<>'number' OR
     v_member->>'expectedGroupMembers' !~ '^[1-9][0-9]{0,9}$' OR (v_member->>'expectedGroupMembers')::numeric>2147483647)
    THEN RAISE EXCEPTION 'lifecycle AG row rejected' USING ERRCODE='22023'; END IF;
  END LOOP;
  IF (SELECT count(DISTINCT (r->>'groupFingerprint',r->>'replicaFingerprint')) FROM jsonb_array_elements(p_payload->'members') r)<>v_count OR
   p_payload->'isHadrEnabled'='false'::jsonb AND v_count<>0 THEN RAISE EXCEPTION 'lifecycle AG membership rejected' USING ERRCODE='22023'; END IF;
  v_ag_coverage:=CASE WHEN p_payload->'isHadrEnabled'='false'::jsonb THEN 4 WHEN p_payload->'isHadrEnabled'='null'::jsonb THEN 3
   WHEN p_payload->'truncated'='true'::jsonb THEN 2 WHEN v_count=0 THEN 3
   WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(p_payload->'members') r GROUP BY r->>'groupFingerprint'
    HAVING count(DISTINCT (r->'observerReplicaFingerprint',r->'observerRole',r->'expectedGroupMembers'))<>1 OR
     min(r->>'observerReplicaFingerprint') IS NULL OR min(r->>'observerRole') IS DISTINCT FROM 'PRIMARY' OR
     min(r->>'expectedGroupMembers')::integer IS DISTINCT FROM count(*) OR
     count(*) FILTER(WHERE r->>'stateObserved'='true' AND r->>'role' IN ('PRIMARY','SECONDARY'))<>count(*) OR
     count(*) FILTER(WHERE r->>'role'='PRIMARY')<>1 OR
     count(*) FILTER(WHERE r->>'replicaFingerprint'=r->>'observerReplicaFingerprint' AND r->>'role'='PRIMARY')<>1)
    THEN 2 ELSE 1 END;
  IF p_payload->'availabilityCoverage' IS DISTINCT FROM to_jsonb(v_ag_coverage) OR p_output_item_count<>v_count+1 OR
   p_source_row_count<p_output_item_count OR p_payload->'truncated'='false'::jsonb AND p_source_row_count<>p_output_item_count OR
   p_output_bytes<>512+v_count*512 OR
   p_outcome='succeeded' AND (v_engine_coverage<>1 OR v_ag_coverage NOT IN (1,4) OR p_reason_code<>'completed' OR p_loss_kind<>'none') OR
   p_outcome='partial' AND NOT (
    p_reason_code='visibility_incomplete' AND p_loss_kind='visibility_incomplete' AND p_minimum_lost_items>=1 AND NOT p_loss_count_is_exact AND p_payload->'truncated'='false'::jsonb AND (v_engine_coverage<>1 OR v_ag_coverage IN (2,3)) OR
    p_reason_code='source_row_limit' AND p_loss_kind='source_row_limit' AND p_payload->'truncated'='true'::jsonb AND p_source_row_count=p_output_item_count+1 AND p_minimum_lost_items>=1 AND NOT p_loss_count_is_exact OR
    p_reason_code='response_byte_limit' AND p_loss_kind='response_byte_limit' AND p_payload->'truncated'='true'::jsonb AND v_count=0 AND p_source_row_count>=2 AND p_minimum_lost_items>=p_source_row_count-1 AND NOT p_loss_count_is_exact)
   THEN RAISE EXCEPTION 'lifecycle source coverage/accounting rejected' USING ERRCODE='22023'; END IF;
  v_observed:=(p_payload->>'sourceObservedAtUtc')::timestamptz;
 END IF;
 -- The lifecycle and inserts below are independently fenced and atomic.
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO run_row FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR run_row.instance_id<>p_instance_id OR run_row.target_revision<>p_target_revision
    OR run_row.collector_id<>p_collector_id OR run_row.collector_version<>p_collector_version
    OR run_row.output_schema_version<>p_output_schema_version
    OR run_row.schedule_revision<>p_schedule_revision OR run_row.work_key<>p_work_key
    OR run_row.owner_execution_id<>p_owner_execution_id OR run_row.fencing_token<>p_fencing_token
    OR run_row.request_digest<>p_request_digest OR run_row.scheduled_for<>p_scheduled_at
 THEN RAISE EXCEPTION 'Server lifecycle run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='server.lifecycle' AND collector_version=1;
 IF NOT FOUND OR contract_row.output_schema_version<>1
 THEN RAISE EXCEPTION 'Server lifecycle immutable collector contract missing' USING ERRCODE='55000'; END IF;
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
  THEN RAISE EXCEPTION 'Server lifecycle divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
    OR schedule_row.schedule_revision<>p_schedule_revision
    OR schedule_row.target_revision<>p_target_revision
    OR schedule_row.next_due_at<>p_scheduled_at
 THEN RAISE EXCEPTION 'Server lifecycle schedule fence mismatch' USING ERRCODE='55000'; END IF;
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
 THEN RAISE EXCEPTION 'Server lifecycle circuit transition mismatch' USING ERRCODE='22023'; END IF;


 -- The target trigger updates this locked schedule in the same transaction as
 -- target configuration changes. Reading target state here preserves that fence
 -- without acquiring a reversed target-row lock after the run lock.
 IF NOT EXISTS(SELECT 1 FROM control.observation_target t WHERE t.instance_id=p_instance_id AND
  t.revision=p_target_revision AND t.lifecycle_state='active' AND t.host_name IS NOT NULL) THEN
  RAISE EXCEPTION 'lifecycle current target fence mismatch' USING ERRCODE='55000'; END IF;
 now_utc:=clock_timestamp();
 SELECT * INTO v_checkpoint FROM control.server_lifecycle_checkpoint c WHERE c.instance_id=p_instance_id FOR UPDATE;
 -- Inspect every canonical attempt, not merely the last successful source receipt.
 -- Abandoned runs completed by begin_collection_run have no lifecycle receipt.
 SELECT r.* INTO v_previous_run FROM telemetry.collection_run_v2 r
 JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
 WHERE r.instance_id=p_instance_id AND r.collector_id='server.lifecycle' AND r.run_id<>p_run_id
 ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1;
 v_previous_id:=v_previous_run.run_id;
 IF v_previous_id IS NULL THEN
  v_boundary:=CASE WHEN v_checkpoint.run_id IS NOT NULL THEN 'retained_predecessor_unavailable'
    WHEN EXISTS(SELECT 1 FROM system.collection_run_retirement r WHERE r.instance_id=p_instance_id) THEN 'retirement_boundary' ELSE 'initial' END;
 ELSE
  SELECT * INTO v_previous_outcome FROM telemetry.collection_run_outcome o WHERE o.run_id=v_previous_id;
  SELECT * INTO v_previous_receipt FROM telemetry.server_lifecycle_receipt r WHERE r.instance_id=p_instance_id AND r.run_id=v_previous_id;
  v_old_engine:=v_previous_receipt.payload->'engine';
  v_engine_qualified:=NOT is_failure AND v_engine_coverage=1 AND p_payload->'truncated'='false'::jsonb AND
   (p_outcome='succeeded' AND p_loss_kind='none' OR p_outcome='partial' AND p_reason_code='visibility_incomplete' AND p_loss_kind='visibility_incomplete');
  v_old_qualified:=v_previous_receipt.engine_coverage=1 AND v_previous_receipt.payload->'truncated'='false'::jsonb AND
   (v_previous_receipt.outcome='succeeded' AND v_previous_receipt.loss_kind='none' OR
    v_previous_receipt.outcome='partial' AND v_previous_receipt.payload->>'reason'='visibility_incomplete' AND v_previous_receipt.loss_kind='visibility_incomplete');
  v_boundary:=CASE
   WHEN v_previous_run.started_at>=run_row.started_at OR v_previous_outcome.run_id IS NULL THEN 'canonical_attempt_gap'
   WHEN v_previous_receipt.run_id IS NULL THEN 'retained_predecessor_unavailable'
   WHEN v_checkpoint.run_id IS DISTINCT FROM v_previous_id OR v_checkpoint.completion_digest IS DISTINCT FROM v_previous_receipt.completion_digest OR
    v_previous_receipt.completion_digest IS DISTINCT FROM v_previous_outcome.completion_digest THEN 'canonical_attempt_gap'
   WHEN v_previous_receipt.target_revision<>p_target_revision THEN 'revision_boundary'
   WHEN v_previous_run.collector_version<>1 OR v_previous_run.output_schema_version<>1 OR v_previous_receipt.source_schema_version<>1 OR v_previous_receipt.identity_version<>1 THEN 'schema_boundary'
   WHEN EXISTS(SELECT 1 FROM system.collection_run_retirement r WHERE r.instance_id=p_instance_id AND r.retired_at>v_previous_receipt.completed_at AND r.retired_at<=now_utc) THEN 'retirement_boundary'
   WHEN is_failure OR v_previous_receipt.outcome NOT IN ('succeeded','partial') OR p_loss_kind IN ('source_row_limit','response_byte_limit','output_validation_failure') OR v_previous_receipt.loss_kind IN ('source_row_limit','response_byte_limit','output_validation_failure') THEN 'collection_gap'
   WHEN v_engine_coverage<>1 OR v_previous_receipt.engine_coverage<>1 THEN 'identity_unavailable'
   WHEN NOT v_engine_qualified OR NOT v_old_qualified THEN 'collection_gap'
   WHEN v_observed<=v_previous_receipt.source_observed_at OR v_observed>now_utc OR
     v_previous_receipt.source_observed_at>v_previous_receipt.completed_at OR v_previous_receipt.completed_at>=now_utc OR
     v_observed<run_row.started_at-interval '5 minutes' OR abs(extract(epoch FROM v_previous_receipt.source_observed_at-v_previous_receipt.completed_at))>300 THEN 'source_clock_boundary'
   WHEN v_engine->>'logicalInstanceFingerprint' IS DISTINCT FROM v_old_engine->>'logicalInstanceFingerprint' THEN 'identity_boundary'
   WHEN v_engine->>'physicalNodeFingerprint'=v_old_engine->>'physicalNodeFingerprint' AND
    v_engine->>'startupIncarnationFingerprint'=v_old_engine->>'startupIncarnationFingerprint' AND
    (v_engine->>'currentMsTicks')::bigint<(v_old_engine->>'currentMsTicks')::bigint THEN 'counter_epoch_unqualified'
   WHEN v_engine->>'physicalNodeFingerprint'=v_old_engine->>'physicalNodeFingerprint' AND
    v_engine->>'startupIncarnationFingerprint'<>v_old_engine->>'startupIncarnationFingerprint' AND
    NOT ((v_engine->>'processId'<>v_old_engine->>'processId' OR v_engine->>'startMsTicks'<>v_old_engine->>'startMsTicks') AND
     v_engine->>'sourceStartLocal'<>v_old_engine->>'sourceStartLocal') THEN 'startup_identity_unqualified'
   ELSE 'comparable' END;
  IF v_boundary='comparable' THEN
   IF v_engine->>'physicalNodeFingerprint'<>v_old_engine->>'physicalNodeFingerprint' THEN
    v_kind:='responding_node_changed'; v_kind_number:=2; v_identity:=v_engine->>'logicalInstanceFingerprint';
   ELSIF v_engine->>'startupIncarnationFingerprint'<>v_old_engine->>'startupIncarnationFingerprint' THEN
    v_kind:='restart_detected'; v_kind_number:=1; v_identity:=v_engine->>'physicalNodeFingerprint';
   END IF;
  END IF;
 END IF;
 INSERT INTO telemetry.server_lifecycle_receipt(instance_id,run_id,target_revision,completed_at,source_observed_at,
  source_schema_version,identity_version,previous_run_id,outcome,loss_kind,engine_coverage,availability_coverage,
  comparison_boundary_reason,source_consistency_verified,payload,completion_digest)
 VALUES(p_instance_id,p_run_id,p_target_revision,now_utc,v_observed,1,1,v_previous_id,p_outcome,p_loss_kind,
  v_engine_coverage,v_ag_coverage,v_boundary,false,p_payload,p_completion_digest);
 IF v_kind IS NOT NULL THEN
  INSERT INTO events.server_lifecycle_event(instance_id,event_id,sample_target_revision,before_run_id,after_run_id,
   kind,affected_identity_fingerprint,before_observed_at,after_observed_at,before_completed_at,detected_at,occurred_at,
   source_schema_version,identity_version,source_consistency_verified,before_engine,after_engine,before_completion_digest,after_completion_digest)
  VALUES(p_instance_id,control.server_lifecycle_event_id(p_instance_id,p_target_revision,1,v_kind_number,v_previous_id,p_run_id,v_identity),
   p_target_revision,v_previous_id,p_run_id,v_kind,decode(v_identity,'hex'),v_previous_receipt.source_observed_at,v_observed,
   v_previous_receipt.completed_at,now_utc,NULL,1,1,false,v_old_engine,v_engine,v_previous_receipt.completion_digest,p_completion_digest);
 END IF;
 INSERT INTO control.server_lifecycle_checkpoint(instance_id,run_id,completed_at,completion_digest)
 VALUES(p_instance_id,p_run_id,now_utc,p_completion_digest)
 ON CONFLICT(instance_id) DO UPDATE SET run_id=EXCLUDED.run_id,completed_at=EXCLUDED.completed_at,completion_digest=EXCLUDED.completion_digest;
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
 RAISE EXCEPTION 'lifecycle scalar shape rejected' USING ERRCODE='22023';
END $fn$;
REVOKE ALL ON FUNCTION control.commit_server_lifecycle_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
-- Deliberately no collector grant until staged writer qualification and explicit activation.

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
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline','deadlock_read_receipt','plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link','server_lifecycle_receipt','server_lifecycle_event'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy','collection_run_canonical',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links','server_lifecycle_receipts','server_lifecycle_events'));
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
 ('control.server_lifecycle_checkpoint'::regclass,'run_id'::name,'telemetry.collection_run_identity'::regclass,'a'::"char");
$inventory$;
REVOKE ALL ON FUNCTION system.collection_run_reviewed_references() FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.server_lifecycle_receipt,events.server_lifecycle_event,control.server_lifecycle_checkpoint
 TO sqlobserver_run_dependency_reader;
INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('server_lifecycle_receipts','telemetry','server_lifecycle_receipt','day',false,NULL,3,'migration-0355'),
      ('server_lifecycle_events','events','server_lifecycle_event','day',false,NULL,3,'migration-0355');

CREATE FUNCTION control.ensure_server_lifecycle_partitions(p_anchor date)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $partitions$
DECLARE v_day date; v_parent_name text; v_schema text; v_name text; v_parent oid; v_child oid; v_created integer:=0;
BEGIN
 IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'lifecycle partition anchor must be current UTC date' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:server-lifecycle:partition-set',0));
 FOREACH v_parent_name IN ARRAY ARRAY['server_lifecycle_receipt','server_lifecycle_event'] LOOP
  v_schema:=CASE v_parent_name WHEN 'server_lifecycle_receipt' THEN 'telemetry' ELSE 'events' END;
  v_parent:=format('%I.%I',v_schema,v_parent_name)::regclass;
  IF NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.oid=v_parent AND c.relkind='p' AND
   c.relowner='sqlobserver_migrator'::regrole AND c.relrowsecurity AND c.relforcerowsecurity) THEN
   RAISE EXCEPTION 'lifecycle partition parent security unverified' USING ERRCODE='55000'; END IF;
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
    RAISE EXCEPTION 'lifecycle partition identity or immutability unverified' USING ERRCODE='55000'; END IF;
   EXECUTE format('REVOKE ALL ON %I.%I FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor',v_schema,v_name);
   EXECUTE format('GRANT SELECT ON %I.%I TO sqlobserver_run_dependency_reader',v_schema,v_name);
   INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
   VALUES(v_schema,v_parent_name,v_schema,v_name,'day',v_day::timestamptz,(v_day+1)::timestamptz) ON CONFLICT DO NOTHING;
   IF NOT EXISTS(SELECT 1 FROM system.partition_registry r WHERE r.parent_schema=v_schema AND r.parent_table=v_parent_name AND
    r.partition_schema=v_schema AND r.partition_name=v_name AND r.partition_granularity='day' AND r.lifecycle_state='attached' AND
    r.range_start=v_day::timestamptz AND r.range_end=(v_day+1)::timestamptz) THEN
    RAISE EXCEPTION 'lifecycle partition registry unverified' USING ERRCODE='55000'; END IF;
  END LOOP;
 END LOOP;
 RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_server_lifecycle_partitions(date) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

CREATE FUNCTION control.ensure_server_lifecycle_partition_set(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC'
AS $fenced$
DECLARE v_created integer;
BEGIN
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 v_created:=control.ensure_server_lifecycle_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);
 PERFORM control.assert_worker_lease('collector/catalog/reconcile',p_owner_execution_id,p_fencing_token);
 RETURN v_created;
END $fenced$;
REVOKE ALL ON FUNCTION control.ensure_server_lifecycle_partition_set(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
-- Owner-only partition bootstrap. The collector wrapper remains staged/ungranted.
SELECT control.ensure_server_lifecycle_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date);
