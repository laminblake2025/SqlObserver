-- Private staged writer. Collector EXECUTE and partitions await rollout/retention.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Exact keys and primitive types: SQL casts must never accept JSON strings as
-- numbers/booleans, silently ignore unknown fields, or leak conversion errors.
CREATE FUNCTION control.backup_v2_json_shape(p_value jsonb,p_spec jsonb)
RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $shape$
DECLARE field record; value jsonb; token text; literal text;
BEGIN
 IF jsonb_typeof(p_value) IS DISTINCT FROM 'object' OR
    (SELECT count(*) FROM jsonb_object_keys(p_value))<>(SELECT count(*) FROM jsonb_object_keys(p_spec))
 THEN RETURN false; END IF;
 FOR field IN SELECT * FROM jsonb_each_text(p_spec) LOOP
  IF NOT p_value ? field.key THEN RETURN false; END IF;
  value:=p_value->field.key; token:=field.value; literal:=p_value->>field.key;
  IF right(token,1)='?' THEN
   IF value='null'::jsonb THEN CONTINUE; END IF;
   token:=left(token,length(token)-1);
  END IF;
  IF token='hex' THEN
   IF jsonb_typeof(value) IS DISTINCT FROM 'string' OR literal !~ '^[0-9a-f]{64}$' THEN RETURN false; END IF;
  ELSIF token='uuid' THEN
   IF jsonb_typeof(value) IS DISTINCT FROM 'string' OR literal !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN RETURN false; END IF;
  ELSIF token IN ('int','long') THEN
   IF jsonb_typeof(value) IS DISTINCT FROM 'number' OR literal !~ '^-?(0|[1-9][0-9]{0,18})$' THEN RETURN false; END IF;
   IF token='int' AND literal::numeric NOT BETWEEN -2147483648 AND 2147483647 THEN RETURN false; END IF;
   IF token='long' AND literal::numeric NOT BETWEEN 0 AND 9223372036854775807 THEN RETURN false; END IF;
  ELSIF token IN ('utc','local') THEN
   IF jsonb_typeof(value) IS DISTINCT FROM 'string' THEN RETURN false; END IF;
   IF token='utc' THEN
    IF literal !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$'
       OR NOT isfinite(literal::timestamptz) OR literal::timestamptz<timestamptz '0001-01-01'
       OR literal::timestamptz>=timestamptz '10000-01-01' THEN RETURN false; END IF;
   ELSE
    IF literal !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}$'
       OR NOT isfinite(literal::timestamp) OR literal::timestamp<timestamp '1753-01-01'
       OR literal::timestamp>=timestamp '10000-01-01' THEN RETURN false; END IF;
   END IF;
  ELSIF token='zone' THEN
   IF jsonb_typeof(value) IS DISTINCT FROM 'string' OR length(literal) NOT BETWEEN 1 AND 128
      OR literal ~ '[[:cntrl:]]' THEN RETURN false; END IF;
  ELSIF jsonb_typeof(value) IS DISTINCT FROM token THEN RETURN false;
  END IF;
 END LOOP;
 RETURN true;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR numeric_value_out_of_range THEN
 RETURN false;
END;
$shape$;
REVOKE ALL ON FUNCTION control.backup_v2_json_shape(jsonb,jsonb)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;

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
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
DECLARE
 run_row telemetry.collection_run%ROWTYPE;
 schedule_row control.collector_schedule%ROWTYPE;
 contract_row control.collector_contract%ROWTYPE;
 prior telemetry.collection_run_outcome%ROWTYPE;
 now_utc timestamptz := clock_timestamp();
 is_failure boolean;
 inserted integer := 0;
 written_bytes integer := 0;
 database_count integer := 0;
 item_count integer;
 expected_state integer;
 db jsonb; sample jsonb; ordinal integer:=0; samples_count integer:=0; received timestamptz; qualification control.backup_historical_zone_qualification%ROWTYPE; start_utc timestamptz;
BEGIN
 is_failure := p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'backups.status'
    OR p_collector_version IS DISTINCT FROM 2 OR p_output_schema_version IS DISTINCT FROM 2
    OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<=0
    OR p_schedule_revision IS NULL OR p_schedule_revision<=0 OR p_scheduled_at IS NULL
    OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token<=0
    OR octet_length(p_request_digest) IS DISTINCT FROM 32
    OR octet_length(p_completion_digest) IS DISTINCT FROM 32
    OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object'
    OR octet_length(p_payload::text)>4194304
    OR p_completion_digest IS DISTINCT FROM sha256(convert_to(p_payload::text,'UTF8'))
    OR p_outcome IS NULL OR p_outcome NOT IN
       ('succeeded','partial','timed_out','transient_failure','permanent_failure',
        'permission_denied','unsupported','output_invalid','lease_lost','circuit_open')
    OR p_reason_code IS NULL OR p_reason_code NOT IN
       ('completed','source_row_limit','response_byte_limit','deadline_exceeded',
        'transient_target_failure','permanent_target_failure','required_permission_missing',
        'target_unsupported','output_validation_failed','lease_ownership_lost','circuit_currently_open',
        'capability_profile_missing','capability_profile_stale','capability_missing',
        'target_version_unsupported','target_platform_unsupported','target_edition_unsupported',
        'visibility_incomplete')
    OR p_attempt_count IS NULL OR p_attempt_count NOT BETWEEN 0 AND 2
    OR p_attempt_count=0 AND p_outcome<>'circuit_open'
    OR p_duration_ms IS NULL OR p_duration_ms NOT BETWEEN 0 AND 3600000
    OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 3073
    OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 3072
    OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 4194304
    OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 4194304
    OR (p_response_bytes<>0 AND p_output_bytes>p_response_bytes)
    OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 3073
    OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 4194304
    OR p_loss_count_is_exact IS NULL OR p_next_circuit_state IS NULL
    OR p_next_circuit_state NOT IN ('closed','open','half_open')
    OR p_next_consecutive_failures IS NULL OR p_next_consecutive_failures NOT BETWEEN 0 AND 1000000
    OR p_loss_kind IS NULL OR p_loss_kind NOT IN
       ('none','source_row_limit','response_byte_limit','output_validation_failure','visibility_incomplete')
    OR p_loss_kind='none' AND (p_minimum_lost_items<>0 OR p_minimum_lost_bytes<>0 OR NOT p_loss_count_is_exact)
    OR p_loss_kind<>'none' AND p_minimum_lost_items=0 AND p_minimum_lost_bytes=0
    OR (is_failure AND (p_output_item_count<>0 OR p_output_bytes<>0
        OR (p_outcome='output_invalid' AND (p_loss_kind<>'output_validation_failure' OR p_minimum_lost_items<1))
        OR (p_outcome<>'output_invalid' AND p_loss_kind<>'none')))
 THEN RAISE EXCEPTION 'Backup v2 lifecycle bounds rejected' USING ERRCODE='22023'; END IF;


 IF NOT control.backup_v2_json_shape(p_payload,
   '{"schemaVersion":"int","targetId":"uuid","targetRevision":"long","runId":"uuid",
     "outcome":"string","reason":"string","receivedAtUtc":"utc","sourceObservedAtUtc":"utc?",
     "sourceZoneId":"zone?","historyLowerBoundLocal":"local?","coverage":"int",
     "sourceRowsRead":"int","rejectedItems":"int","rejectedBytes":"int","databases":"array"}'::jsonb)
 THEN RAISE EXCEPTION 'Backup v2 envelope shape rejected' USING ERRCODE='22023'; END IF;
 received:=(p_payload->>'receivedAtUtc')::timestamptz;
 IF p_payload->>'schemaVersion'<>'2' OR p_payload->>'targetId'<>p_instance_id::text
    OR p_payload->>'targetRevision'<>p_target_revision::text OR p_payload->>'runId'<>p_run_id::text
    OR p_payload->>'outcome'<>p_outcome OR p_payload->>'reason'<>p_reason_code
    OR p_payload->>'sourceRowsRead'<>p_source_row_count::text
    OR (p_payload->>'coverage')::integer NOT BETWEEN 0 AND 6
    OR (p_payload->>'rejectedItems')::integer NOT BETWEEN 0 AND 3072
    OR (p_payload->>'rejectedBytes')::integer NOT BETWEEN 0 AND 4194304
 THEN RAISE EXCEPTION 'Backup v2 envelope identity rejected' USING ERRCODE='22023'; END IF;
 database_count:=jsonb_array_length(p_payload->'databases');
 item_count:=database_count;
 IF database_count>512 THEN RAISE EXCEPTION 'Backup v2 database bound rejected' USING ERRCODE='22023'; END IF;
 FOR db IN SELECT value FROM jsonb_array_elements(p_payload->'databases') LOOP
  ordinal:=ordinal+1;
  IF NOT control.backup_v2_json_shape(db,
    '{"ordinal":"int","databaseFingerprint":"hex?","familyFingerprint":"hex?",
      "recoveryForkFingerprint":"hex?","groupFingerprint":"hex?","agDatabaseFingerprint":"hex?",
      "localReplicaFingerprint":"hex?","recoveryModel":"int","catalogDatabaseState":"string?",
      "scope":"int","localReplicaRole":"string?","localReplicaPreferred":"boolean?",
      "unclassifiedBackupSeen":"boolean","coverage":"int","samples":"array"}'::jsonb)
  THEN RAISE EXCEPTION 'Backup v2 database shape rejected' USING ERRCODE='22023'; END IF;
  IF (db->>'ordinal')::integer<>ordinal OR (db->>'recoveryModel')::integer NOT BETWEEN 0 AND 3
     OR (db->>'scope')::integer NOT BETWEEN 0 AND 2 OR (db->>'coverage')::integer NOT IN (1,2)
     OR coalesce(db->>'catalogDatabaseState','ONLINE') NOT IN
       ('ONLINE','RESTORING','RECOVERING','RECOVERY_PENDING','SUSPECT','EMERGENCY','OFFLINE')
     OR coalesce(db->>'localReplicaRole','PRIMARY') NOT IN ('PRIMARY','SECONDARY','RESOLVING')
     OR NOT ((db->>'scope'='2' AND (db->>'groupFingerprint' IS NOT NULL
          OR db->>'agDatabaseFingerprint' IS NOT NULL OR db->>'localReplicaFingerprint' IS NOT NULL))
       OR (db->>'scope'<>'2' AND db->>'groupFingerprint' IS NULL AND db->>'agDatabaseFingerprint' IS NULL
          AND db->>'localReplicaFingerprint' IS NULL AND db->>'localReplicaRole' IS NULL AND db->>'localReplicaPreferred' IS NULL))
     OR db->>'localReplicaPreferred' IS NOT NULL AND (db->>'groupFingerprint' IS NULL
          OR db->>'agDatabaseFingerprint' IS NULL OR db->>'localReplicaFingerprint' IS NULL)
     OR (db->>'coverage'='1') IS DISTINCT FROM (db->>'databaseFingerprint' IS NOT NULL
       AND db->>'familyFingerprint' IS NOT NULL AND db->>'recoveryForkFingerprint' IS NOT NULL
       AND db->>'recoveryModel'<>'0' AND coalesce(db->>'catalogDatabaseState'='ONLINE',false)
       AND db->>'unclassifiedBackupSeen'='false' AND (db->>'scope'='1' OR db->>'scope'='2'
         AND db->>'groupFingerprint' IS NOT NULL AND db->>'agDatabaseFingerprint' IS NOT NULL
         AND db->>'localReplicaFingerprint' IS NOT NULL AND coalesce(db->>'localReplicaRole' IN ('PRIMARY','SECONDARY'),false)
         AND db->>'localReplicaPreferred' IS NOT NULL))
  THEN RAISE EXCEPTION 'Backup v2 database context rejected' USING ERRCODE='22023'; END IF;
  IF jsonb_array_length(db->'samples')>5 OR jsonb_array_length(db->'samples')>0 AND
       (db->>'databaseFingerprint' IS NULL OR db->>'familyFingerprint' IS NULL OR db->>'recoveryForkFingerprint' IS NULL)
  THEN RAISE EXCEPTION 'Backup v2 sample identity rejected' USING ERRCODE='22023'; END IF;
  FOR sample IN SELECT value FROM jsonb_array_elements(db->'samples') LOOP
   IF NOT control.backup_v2_json_shape(sample,
     '{"kind":"int","copyOnly":"boolean","finishUtc":"utc?","sourceLocalStart":"local?",
       "sourceLocalFinish":"local?","recordedStartOffsetMinutes":"int?","sizeBytes":"long?",
       "hasChecksum":"boolean?","isDamaged":"boolean?","metadataComplete":"boolean?",
       "firstRecoveryForkFingerprint":"hex?","lastRecoveryForkFingerprint":"hex",
       "timeState":"int","qualificationRevision":"long?"}'::jsonb)
   THEN RAISE EXCEPTION 'Backup v2 sample shape rejected' USING ERRCODE='22023'; END IF;
   IF (sample->>'kind')::integer NOT BETWEEN 1 AND 3 OR sample->>'kind'='2' AND sample->>'copyOnly'='true'
      OR (sample->>'timeState')::integer NOT BETWEEN 1 AND 11
      OR sample->>'recordedStartOffsetMinutes' IS NOT NULL AND
        ((sample->>'recordedStartOffsetMinutes')::integer NOT BETWEEN -720 AND 720
          OR (sample->>'recordedStartOffsetMinutes')::integer%15<>0)
      OR (sample->>'qualificationRevision')::bigint<=0
      OR sample->>'lastRecoveryForkFingerprint' IS DISTINCT FROM db->>'recoveryForkFingerprint'
      OR sample->>'metadataComplete'='true' AND sample->>'firstRecoveryForkFingerprint' IS NULL
      OR (sample->>'sourceLocalFinish')::timestamp<(p_payload->>'historyLowerBoundLocal')::timestamp
      OR sample->>'sourceLocalFinish' IS NULL AND
        (sample->>'sourceLocalStart')::timestamp<(p_payload->>'historyLowerBoundLocal')::timestamp
      OR (sample->>'timeState'='1') IS DISTINCT FROM (sample->>'finishUtc' IS NOT NULL)
      OR sample->>'timeState'='1' AND (sample->>'sourceLocalStart' IS NULL OR sample->>'sourceLocalFinish' IS NULL
        OR sample->>'recordedStartOffsetMinutes' IS NULL OR sample->>'qualificationRevision' IS NULL)
      OR sample->>'timeState'='2' AND sample->>'sourceLocalFinish' IS NOT NULL
      OR sample->>'timeState'='3' AND (sample->>'sourceLocalStart' IS NOT NULL OR sample->>'sourceLocalFinish' IS NULL)
      OR (sample->>'timeState')::integer BETWEEN 4 AND 11 AND
        (sample->>'sourceLocalStart' IS NULL OR sample->>'sourceLocalFinish' IS NULL)
      OR sample->>'timeState'='4' AND sample->>'qualificationRevision' IS NOT NULL
      OR sample->>'timeState'='5' AND (sample->>'recordedStartOffsetMinutes' IS NOT NULL OR sample->>'qualificationRevision' IS NULL)
      OR (sample->>'timeState')::integer BETWEEN 6 AND 11 AND
        (sample->>'qualificationRevision' IS NULL OR sample->>'recordedStartOffsetMinutes' IS NULL)
   THEN RAISE EXCEPTION 'Backup v2 sample lineage or time provenance rejected' USING ERRCODE='22023'; END IF;
   IF sample->>'qualificationRevision' IS NOT NULL THEN
    SELECT * INTO qualification FROM control.backup_historical_zone_qualification
      WHERE instance_id=p_instance_id AND target_revision=p_target_revision
        AND qualification_revision=(sample->>'qualificationRevision')::bigint;
    IF NOT FOUND THEN RAISE EXCEPTION 'Backup v2 historical qualification missing' USING ERRCODE='22023'; END IF;
    IF sample->>'timeState'='1' THEN
     start_utc:=(sample->>'sourceLocalStart')::timestamp AT TIME ZONE 'UTC'
       - make_interval(mins=>(sample->>'recordedStartOffsetMinutes')::integer);
     IF p_payload->>'sourceZoneId' IS NOT NULL AND p_payload->>'sourceZoneId'<>qualification.source_zone_id
       OR start_utc<qualification.qualified_from OR start_utc>=qualification.qualified_to
       OR (sample->>'finishUtc')::timestamptz<qualification.qualified_from
       OR (sample->>'finishUtc')::timestamptz>=qualification.qualified_to
       OR (sample->>'finishUtc')::timestamptz<start_utc
       OR abs(extract(epoch FROM ((sample->>'sourceLocalFinish')::timestamp AT TIME ZONE 'UTC'
           - (sample->>'finishUtc')::timestamptz)))>14*3600
       OR extract(epoch FROM ((sample->>'sourceLocalFinish')::timestamp AT TIME ZONE 'UTC'
           - (sample->>'finishUtc')::timestamptz))%60<>0
     THEN RAISE EXCEPTION 'Backup v2 historical qualification boundary rejected' USING ERRCODE='22023'; END IF;
    END IF;
   END IF;
  END LOOP;
  IF (SELECT count(DISTINCT (value->>'kind',value->>'copyOnly')) FROM jsonb_array_elements(db->'samples'))
      <>jsonb_array_length(db->'samples')
  THEN RAISE EXCEPTION 'Backup v2 duplicate sample slot rejected' USING ERRCODE='22023'; END IF;
  samples_count:=samples_count+jsonb_array_length(db->'samples');
 END LOOP;
 item_count:=item_count+samples_count;
 IF item_count<>p_output_item_count OR item_count>3072 OR item_count>p_source_row_count
    OR (SELECT count(DISTINCT value->>'databaseFingerprint') FROM jsonb_array_elements(p_payload->'databases'))
      <>(SELECT count(*) FROM jsonb_array_elements(p_payload->'databases') WHERE value->>'databaseFingerprint' IS NOT NULL)
 THEN RAISE EXCEPTION 'Backup v2 item accounting rejected' USING ERRCODE='22023'; END IF;
 IF is_failure THEN
  expected_state:=CASE p_outcome WHEN 'unsupported' THEN 6 WHEN 'permission_denied' THEN 4 WHEN 'circuit_open' THEN 0 ELSE 5 END;
  IF item_count<>0 OR (p_payload->>'coverage')::integer<>expected_state
     OR p_payload->>'sourceObservedAtUtc' IS NOT NULL OR p_payload->>'sourceZoneId' IS NOT NULL
     OR p_payload->>'historyLowerBoundLocal' IS NOT NULL
     OR p_outcome='output_invalid' AND ((p_payload->>'rejectedItems')::integer>p_minimum_lost_items
       OR p_minimum_lost_items<1 OR (p_payload->>'rejectedBytes')::integer<>p_minimum_lost_bytes)
     OR p_outcome<>'output_invalid' AND (p_payload->>'rejectedItems'<>'0' OR p_payload->>'rejectedBytes'<>'0')
  THEN RAISE EXCEPTION 'Backup v2 failed receipt rejected' USING ERRCODE='22023'; END IF;
 ELSE
  expected_state:=CASE WHEN p_payload->>'coverage'='3' THEN 3 WHEN database_count=0
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_payload->'databases') WHERE value->>'coverage'='2') THEN 2 ELSE 1 END;
  IF (p_payload->>'coverage')::integer<>expected_state OR p_payload->>'rejectedItems'<>'0' OR p_payload->>'rejectedBytes'<>'0'
     OR p_output_bytes<2048+database_count*2048+samples_count*1536
     OR p_source_row_count>0 AND (p_payload->>'sourceObservedAtUtc' IS NULL OR p_payload->>'historyLowerBoundLocal' IS NULL)
     OR p_source_row_count=0 AND (p_payload->>'sourceObservedAtUtc' IS NOT NULL OR p_payload->>'historyLowerBoundLocal' IS NOT NULL OR p_payload->>'sourceZoneId' IS NOT NULL)
     OR expected_state IN (1,2) AND p_source_row_count<>item_count
     OR expected_state=1 AND (p_outcome<>'succeeded' OR p_reason_code<>'completed' OR p_loss_kind<>'none')
     OR expected_state=2 AND (p_outcome<>'partial' OR p_reason_code<>'visibility_incomplete'
       OR p_loss_kind<>'visibility_incomplete' OR p_minimum_lost_items<>1 OR p_loss_count_is_exact OR p_minimum_lost_bytes<>0)
     OR expected_state=3 AND (item_count<>0 OR p_source_row_count<1 OR p_minimum_lost_items<>p_source_row_count OR p_minimum_lost_bytes<>0
       OR p_outcome<>'partial' OR p_loss_count_is_exact OR NOT
        (p_reason_code='source_row_limit' AND p_loss_kind='source_row_limit'
          OR p_reason_code='response_byte_limit' AND p_loss_kind='response_byte_limit'))
  THEN RAISE EXCEPTION 'Backup v2 coverage or loss accounting rejected' USING ERRCODE='22023'; END IF;
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
 THEN RAISE EXCEPTION 'Backup v2 run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='backups.status' AND collector_version=2;
 IF NOT FOUND OR contract_row.output_schema_version<>2
 THEN RAISE EXCEPTION 'Backup v2 immutable collector contract missing' USING ERRCODE='55000'; END IF;
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
  THEN RAISE EXCEPTION 'Backup v2 divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
    OR schedule_row.schedule_revision<>p_schedule_revision
    OR schedule_row.target_revision<>p_target_revision
    OR schedule_row.next_due_at<>p_scheduled_at
 THEN RAISE EXCEPTION 'Backup v2 schedule fence mismatch' USING ERRCODE='55000'; END IF;
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
 THEN RAISE EXCEPTION 'Backup v2 circuit transition mismatch' USING ERRCODE='22023'; END IF;


 IF received<run_row.started_at-interval '5 minutes' OR received>now_utc+interval '5 minutes'
 THEN RAISE EXCEPTION 'Backup v2 receipt time outside run boundary' USING ERRCODE='22023'; END IF;
 -- The receipt clock controls storage placement; the target clock is evidence.
 INSERT INTO telemetry.backup_v2_scan(instance_id,target_revision,run_id,received_at,
   source_observed_at,source_zone_id,history_lower_bound_local,coverage,source_rows_read,
   database_count,item_count,rejected_items,rejected_bytes)
 VALUES(p_instance_id,p_target_revision,p_run_id,received,(p_payload->>'sourceObservedAtUtc')::timestamptz,
   p_payload->>'sourceZoneId',(p_payload->>'historyLowerBoundLocal')::timestamp,
   (p_payload->>'coverage')::smallint,p_source_row_count,database_count,item_count,
   (p_payload->>'rejectedItems')::integer,(p_payload->>'rejectedBytes')::integer);
 INSERT INTO telemetry.backup_v2_database(instance_id,target_revision,received_at,run_id,database_ordinal,
   database_fingerprint,family_fingerprint,recovery_fork_fingerprint,group_fingerprint,
   ag_database_fingerprint,local_replica_fingerprint,recovery_model,catalog_database_state,
   scope,local_replica_role,local_replica_preferred,unclassified_backup_seen,coverage)
 SELECT p_instance_id,p_target_revision,received,p_run_id,(value->>'ordinal')::smallint,
   decode(value->>'databaseFingerprint','hex'),decode(value->>'familyFingerprint','hex'),
   decode(value->>'recoveryForkFingerprint','hex'),decode(value->>'groupFingerprint','hex'),
   decode(value->>'agDatabaseFingerprint','hex'),decode(value->>'localReplicaFingerprint','hex'),
   (value->>'recoveryModel')::smallint,value->>'catalogDatabaseState',(value->>'scope')::smallint,
   value->>'localReplicaRole',(value->>'localReplicaPreferred')::boolean,
   (value->>'unclassifiedBackupSeen')::boolean,(value->>'coverage')::smallint
 FROM jsonb_array_elements(p_payload->'databases');
 GET DIAGNOSTICS inserted=ROW_COUNT;
 INSERT INTO telemetry.backup_v2_sample(instance_id,target_revision,received_at,run_id,database_ordinal,
   kind,copy_only,finish_utc,source_local_start,source_local_finish,recorded_start_offset_minutes,
   size_bytes,has_checksum,is_damaged,metadata_complete,first_recovery_fork_fingerprint,
   last_recovery_fork_fingerprint,time_state,qualification_revision)
 SELECT p_instance_id,p_target_revision,received,p_run_id,(db.value->>'ordinal')::smallint,
   (sample.value->>'kind')::smallint,(sample.value->>'copyOnly')::boolean,
   (sample.value->>'finishUtc')::timestamptz,(sample.value->>'sourceLocalStart')::timestamp,
   (sample.value->>'sourceLocalFinish')::timestamp,(sample.value->>'recordedStartOffsetMinutes')::smallint,
   (sample.value->>'sizeBytes')::bigint,(sample.value->>'hasChecksum')::boolean,
   (sample.value->>'isDamaged')::boolean,(sample.value->>'metadataComplete')::boolean,
   decode(sample.value->>'firstRecoveryForkFingerprint','hex'),decode(sample.value->>'lastRecoveryForkFingerprint','hex'),
   (sample.value->>'timeState')::smallint,(sample.value->>'qualificationRevision')::bigint
 FROM jsonb_array_elements(p_payload->'databases') AS db(value)
 CROSS JOIN LATERAL jsonb_array_elements(db.value->'samples') AS sample(value);
 GET DIAGNOSTICS samples_count=ROW_COUNT;
 inserted:=inserted+samples_count;
 IF inserted<>p_output_item_count THEN RAISE EXCEPTION 'Backup v2 persisted accounting mismatch' USING ERRCODE='22023'; END IF;
 written_bytes:=CASE WHEN is_failure THEN 0 ELSE p_output_bytes END;
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
         (p_payload->>'coverage')::integer=3,p_loss_kind<>'none',p_loss_kind,
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
END;
$fn$;
REVOKE ALL ON FUNCTION control.commit_backups_v2_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
