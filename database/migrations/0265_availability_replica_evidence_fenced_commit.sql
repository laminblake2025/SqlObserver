-- Stage a private AG v2 commit. Collector execution remains ungranted until
-- partition maintenance, retention, and versioned catalog activation are ready.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.commit_availability_groups_v2_collection_run(
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
BEGIN
 is_failure := p_outcome NOT IN ('succeeded','partial');
 IF p_collector_id IS DISTINCT FROM 'availability-groups.health'
    OR p_collector_version IS DISTINCT FROM 2 OR p_output_schema_version IS DISTINCT FROM 2
    OR p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision IS NULL OR p_target_revision<=0
    OR p_schedule_revision IS NULL OR p_schedule_revision<=0 OR p_scheduled_at IS NULL
    OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token IS NULL OR p_fencing_token<=0
    OR octet_length(p_request_digest) IS DISTINCT FROM 32
    OR octet_length(p_completion_digest) IS DISTINCT FROM 32
    OR jsonb_typeof(p_payload) IS DISTINCT FROM 'object'
    OR octet_length(p_payload::text)>2097152
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
    OR p_source_row_count IS NULL OR p_source_row_count NOT BETWEEN 0 AND 2049
    OR p_output_item_count IS NULL OR p_output_item_count NOT BETWEEN 0 AND 2048
    OR p_response_bytes IS NULL OR p_response_bytes NOT BETWEEN 0 AND 2097152
    OR p_output_bytes IS NULL OR p_output_bytes NOT BETWEEN 0 AND 2097152
    OR (p_response_bytes<>0 AND p_output_bytes>p_response_bytes)
    OR p_minimum_lost_items IS NULL OR p_minimum_lost_items NOT BETWEEN 0 AND 2049
    OR p_minimum_lost_bytes IS NULL OR p_minimum_lost_bytes NOT BETWEEN 0 AND 2097152
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
 THEN RAISE EXCEPTION 'AG v2 lifecycle bounds rejected' USING ERRCODE='22023'; END IF;

 IF NOT p_payload ?& ARRAY['schemaVersion','targetId','targetRevision','runId','outcome','reason',
       'observedAtUtc','state','sourceRowsRead','truncated','replicas','databases']
    OR EXISTS (SELECT 1 FROM jsonb_object_keys(p_payload) AS field(name)
       WHERE field.name NOT IN ('schemaVersion','targetId','targetRevision','runId','outcome','reason',
          'observedAtUtc','state','sourceRowsRead','truncated','replicas','databases'))
    OR jsonb_typeof(p_payload->'schemaVersion') IS DISTINCT FROM 'number'
    OR p_payload->>'schemaVersion' IS DISTINCT FROM '2'
    OR jsonb_typeof(p_payload->'targetId') IS DISTINCT FROM 'string'
    OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
    OR jsonb_typeof(p_payload->'targetRevision') IS DISTINCT FROM 'number'
    OR p_payload->>'targetRevision' IS DISTINCT FROM p_target_revision::text
    OR jsonb_typeof(p_payload->'runId') IS DISTINCT FROM 'string'
    OR p_payload->>'runId' IS DISTINCT FROM p_run_id::text
    OR jsonb_typeof(p_payload->'outcome') IS DISTINCT FROM 'string'
    OR p_payload->>'outcome' IS DISTINCT FROM p_outcome
    OR jsonb_typeof(p_payload->'reason') IS DISTINCT FROM 'string'
    OR p_payload->>'reason' IS DISTINCT FROM p_reason_code
    OR jsonb_typeof(p_payload->'sourceRowsRead') IS DISTINCT FROM 'number'
    OR p_payload->>'sourceRowsRead' IS DISTINCT FROM p_source_row_count::text
    OR jsonb_typeof(p_payload->'state') IS DISTINCT FROM 'number'
    OR p_payload->>'state' NOT IN ('1','2','3','4','5','6')
    OR jsonb_typeof(p_payload->'truncated') IS DISTINCT FROM 'boolean'
    OR jsonb_typeof(p_payload->'replicas') IS DISTINCT FROM 'array'
    OR jsonb_typeof(p_payload->'databases') IS DISTINCT FROM 'array'
    OR jsonb_typeof(p_payload->'observedAtUtc') IS DISTINCT FROM 'string'
    OR p_payload->>'observedAtUtc' !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$'
    OR NOT isfinite((p_payload->>'observedAtUtc')::timestamptz)
 THEN RAISE EXCEPTION 'AG v2 envelope rejected' USING ERRCODE='22023'; END IF;
 item_count := jsonb_array_length(p_payload->'replicas')+jsonb_array_length(p_payload->'databases');
 IF item_count<>p_output_item_count OR item_count>p_source_row_count
    OR is_failure AND (item_count<>0 OR (p_payload->>'truncated')::boolean
       OR p_payload->>'state' IS DISTINCT FROM CASE p_outcome WHEN 'unsupported' THEN '4'
              WHEN 'permission_denied' THEN '5' ELSE '3' END)
 THEN RAISE EXCEPTION 'AG v2 item accounting rejected' USING ERRCODE='22023'; END IF;

 IF EXISTS (
   SELECT 1 FROM (
     SELECT value,0 AS kind FROM jsonb_array_elements(p_payload->'replicas')
     UNION ALL SELECT value,1 FROM jsonb_array_elements(p_payload->'databases')
   ) AS item
   WHERE jsonb_typeof(item.value) IS DISTINCT FROM 'object'
     OR NOT item.value ?& ARRAY['groupFingerprint','replicaFingerprint','observerReplicaFingerprint',
                                'replicaIsLocal','visibilityScope','stateObserved']
     OR jsonb_typeof(item.value->'groupFingerprint') IS DISTINCT FROM 'string'
     OR item.value->>'groupFingerprint' !~ '^[0-9a-f]{64}$'
     OR jsonb_typeof(item.value->'replicaFingerprint') IS DISTINCT FROM 'string'
     OR item.value->>'replicaFingerprint' !~ '^[0-9a-f]{64}$'
     OR jsonb_typeof(item.value->'visibilityScope') IS DISTINCT FROM 'number'
     OR item.value->>'visibilityScope' NOT IN ('1','2','3')
     OR jsonb_typeof(item.value->'stateObserved') IS DISTINCT FROM 'boolean'
     OR NOT ((item.value->'observerReplicaFingerprint'='null'::jsonb
             AND item.value->'replicaIsLocal'='null'::jsonb AND item.value->>'visibilityScope'='3')
         OR (jsonb_typeof(item.value->'observerReplicaFingerprint')='string'
             AND item.value->>'observerReplicaFingerprint' ~ '^[0-9a-f]{64}$'
             AND jsonb_typeof(item.value->'replicaIsLocal')='boolean'
             AND (item.value->>'replicaIsLocal')::boolean=
                 (item.value->>'observerReplicaFingerprint'=item.value->>'replicaFingerprint')))
     OR (item.kind=0 AND (
          NOT item.value ?& ARRAY['role','operationalState','connectedState']
          OR EXISTS (SELECT 1 FROM jsonb_object_keys(item.value) AS field(name)
             WHERE field.name NOT IN ('groupFingerprint','replicaFingerprint','observerReplicaFingerprint',
               'replicaIsLocal','visibilityScope','stateObserved','role','operationalState','connectedState'))
          OR jsonb_typeof(item.value->'role') NOT IN ('null','string')
          OR item.value->>'role' NOT IN ('PRIMARY','SECONDARY','RESOLVING')
          OR jsonb_typeof(item.value->'operationalState') NOT IN ('null','string')
          OR item.value->>'operationalState' NOT IN ('PENDING_FAILOVER','PENDING','ONLINE','OFFLINE','FAILED','FAILED_NO_QUORUM')
          OR jsonb_typeof(item.value->'connectedState') NOT IN ('null','string')
          OR item.value->>'connectedState' NOT IN ('CONNECTED','DISCONNECTED')
          OR item.value->>'stateObserved'='false' AND
             (item.value->'role'<>'null'::jsonb OR item.value->'operationalState'<>'null'::jsonb
               OR item.value->'connectedState'<>'null'::jsonb)))
     OR (item.kind=1 AND (
          NOT item.value ?& ARRAY['databaseFingerprint','synchronizationState','synchronizationHealth',
             'databaseState','isSuspended','logSendQueueKb','logSendRateKbPerSecond',
             'redoQueueKb','redoRateKbPerSecond','sourceLastCommitLocal']
          OR EXISTS (SELECT 1 FROM jsonb_object_keys(item.value) AS field(name)
             WHERE field.name NOT IN ('groupFingerprint','replicaFingerprint','observerReplicaFingerprint',
               'replicaIsLocal','visibilityScope','stateObserved','databaseFingerprint',
               'synchronizationState','synchronizationHealth','databaseState','isSuspended',
               'logSendQueueKb','logSendRateKbPerSecond','redoQueueKb','redoRateKbPerSecond','sourceLastCommitLocal'))
          OR jsonb_typeof(item.value->'databaseFingerprint') IS DISTINCT FROM 'string'
          OR item.value->>'databaseFingerprint' !~ '^[0-9a-f]{64}$'
          OR jsonb_typeof(item.value->'synchronizationState') NOT IN ('null','string')
          OR item.value->>'synchronizationState' NOT IN ('NOT SYNCHRONIZING','SYNCHRONIZING','SYNCHRONIZED','REVERTING','INITIALIZING')
          OR jsonb_typeof(item.value->'synchronizationHealth') NOT IN ('null','string')
          OR item.value->>'synchronizationHealth' NOT IN ('NOT_HEALTHY','PARTIALLY_HEALTHY','HEALTHY')
          OR jsonb_typeof(item.value->'databaseState') NOT IN ('null','string')
          OR item.value->>'databaseState' NOT IN ('ONLINE','RESTORING','RECOVERING','RECOVERY_PENDING','SUSPECT','EMERGENCY','OFFLINE')
          OR jsonb_typeof(item.value->'isSuspended') NOT IN ('null','boolean')
          OR EXISTS (SELECT 1 FROM jsonb_each(item.value) AS field(name,value)
             WHERE field.name IN ('logSendQueueKb','logSendRateKbPerSecond','redoQueueKb','redoRateKbPerSecond')
               AND field.value<>'null'::jsonb AND
                (jsonb_typeof(field.value)<>'number' OR field.value::text !~ '^(0|[1-9][0-9]{0,18})$'
                  OR field.value::text::numeric>9223372036854775807))
          OR item.value->'sourceLastCommitLocal'<>'null'::jsonb AND
             (jsonb_typeof(item.value->'sourceLastCommitLocal')<>'string'
              OR item.value->>'sourceLastCommitLocal' !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}$'
              OR NOT isfinite((item.value->>'sourceLastCommitLocal')::timestamp)
              OR (item.value->>'sourceLastCommitLocal')::timestamp<timestamp '1753-01-01'
              OR (item.value->>'sourceLastCommitLocal')::timestamp>=timestamp '10000-01-01')
          OR item.value->>'stateObserved'='false' AND EXISTS
             (SELECT 1 FROM jsonb_each(item.value) AS field(name,value)
              WHERE field.name IN ('synchronizationState','synchronizationHealth','databaseState','isSuspended',
                'logSendQueueKb','logSendRateKbPerSecond','redoQueueKb','redoRateKbPerSecond','sourceLastCommitLocal')
                AND field.value<>'null'::jsonb)))
 ) THEN RAISE EXCEPTION 'AG v2 row shape or state rejected' USING ERRCODE='22023'; END IF;

 IF (SELECT count(DISTINCT (value->>'groupFingerprint',value->>'replicaFingerprint'))
       FROM jsonb_array_elements(p_payload->'replicas'))<>jsonb_array_length(p_payload->'replicas')
    OR (SELECT count(DISTINCT (value->>'groupFingerprint',value->>'replicaFingerprint',value->>'databaseFingerprint'))
       FROM jsonb_array_elements(p_payload->'databases'))<>jsonb_array_length(p_payload->'databases')
    OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'replicas') AS item(value)
       GROUP BY item.value->>'groupFingerprint'
       HAVING count(DISTINCT (item.value->'observerReplicaFingerprint',item.value->'visibilityScope'))<>1)
    OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'databases') AS db(value)
       WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'replicas') AS replica(value)
          WHERE replica.value->>'groupFingerprint'=db.value->>'groupFingerprint'
            AND replica.value->>'replicaFingerprint'=db.value->>'replicaFingerprint'
            AND replica.value->'observerReplicaFingerprint'=db.value->'observerReplicaFingerprint'
            AND replica.value->'replicaIsLocal'=db.value->'replicaIsLocal'
            AND replica.value->'visibilityScope'=db.value->'visibilityScope'))
    OR NOT (p_payload->>'truncated')::boolean AND EXISTS (
       SELECT 1 FROM jsonb_array_elements(p_payload->'replicas') AS item(value)
       WHERE item.value->'observerReplicaFingerprint'<>'null'::jsonb AND NOT EXISTS (
          SELECT 1 FROM jsonb_array_elements(p_payload->'replicas') AS observer(value)
          WHERE observer.value->>'groupFingerprint'=item.value->>'groupFingerprint'
            AND observer.value->'replicaFingerprint'=item.value->'observerReplicaFingerprint'))
 THEN RAISE EXCEPTION 'AG v2 replica/database relationship rejected' USING ERRCODE='22023'; END IF;

 IF NOT is_failure THEN
  expected_state := CASE WHEN (p_payload->>'truncated')::boolean THEN 2
     WHEN p_source_row_count=0 THEN 6
     WHEN EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'replicas') AS item(value)
         WHERE item.value->>'visibilityScope'<>'1' OR item.value->>'stateObserved'<>'true'
           OR item.value->'connectedState'='null'::jsonb
           OR item.value->>'replicaIsLocal'='true' AND
              (item.value->>'role' IS DISTINCT FROM 'PRIMARY' OR item.value->'operationalState'='null'::jsonb))
       OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'databases') AS item(value)
         WHERE item.value->>'visibilityScope'<>'1' OR item.value->>'stateObserved'<>'true'
           OR item.value->'synchronizationState'='null'::jsonb OR item.value->'synchronizationHealth'='null'::jsonb
           OR item.value->>'replicaIsLocal'='true' AND item.value->'databaseState'='null'::jsonb)
     THEN 3 ELSE 1 END;
  IF (p_payload->>'state')::integer<>expected_state
     OR expected_state<>2 AND p_source_row_count<>item_count
     OR expected_state IN (1,6) AND (p_outcome<>'succeeded' OR p_reason_code<>'completed' OR p_loss_kind<>'none')
     OR expected_state=3 AND (p_outcome<>'partial' OR p_reason_code<>'visibility_incomplete'
        OR p_loss_kind<>'visibility_incomplete' OR p_minimum_lost_items<1)
     OR expected_state=2 AND (p_outcome<>'partial' OR p_minimum_lost_items<1 OR p_loss_count_is_exact
        OR NOT ((p_reason_code='source_row_limit' AND p_loss_kind='source_row_limit' AND p_source_row_count=item_count+1)
          OR (p_reason_code='response_byte_limit' AND p_loss_kind='response_byte_limit'
             AND item_count=0 AND p_source_row_count>=1 AND p_minimum_lost_items>=p_source_row_count)))
  THEN RAISE EXCEPTION 'AG v2 coverage or loss accounting rejected' USING ERRCODE='22023'; END IF;
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
 THEN RAISE EXCEPTION 'AG v2 run identity or fence mismatch' USING ERRCODE='55000'; END IF;
 SELECT * INTO contract_row FROM control.collector_contract
 WHERE collector_id='availability-groups.health' AND collector_version=2;
 IF NOT FOUND OR contract_row.output_schema_version<>2
 THEN RAISE EXCEPTION 'AG v2 immutable collector contract missing' USING ERRCODE='55000'; END IF;
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
  THEN RAISE EXCEPTION 'AG v2 divergent completion replay' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT 'replayed',0,0,0,0,prior.completed_at;
  RETURN;
 END IF;
 SELECT * INTO schedule_row FROM control.collector_schedule
 WHERE instance_id=p_instance_id AND collector_id=p_collector_id FOR UPDATE;
 IF NOT FOUND OR schedule_row.active_run_id IS DISTINCT FROM p_run_id
    OR schedule_row.schedule_revision<>p_schedule_revision
    OR schedule_row.target_revision<>p_target_revision
    OR schedule_row.next_due_at<>p_scheduled_at
 THEN RAISE EXCEPTION 'AG v2 schedule fence mismatch' USING ERRCODE='55000'; END IF;
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
 THEN RAISE EXCEPTION 'AG v2 circuit transition mismatch' USING ERRCODE='22023'; END IF;

 IF NOT is_failure THEN
  IF (p_payload->>'observedAtUtc')::timestamptz<run_row.started_at-interval '5 minutes'
     OR (p_payload->>'observedAtUtc')::timestamptz>now_utc+interval '5 minutes'
  THEN RAISE EXCEPTION 'AG v2 observation time outside run boundary' USING ERRCODE='22023'; END IF;
  INSERT INTO telemetry.availability_replica_evidence
   (instance_id,run_id,target_revision,observed_at,collected_at,group_fingerprint,
    replica_fingerprint,observer_replica_fingerprint,replica_is_local,visibility_scope,
    state_observed,role,operational_state,connected_state)
  SELECT p_instance_id,p_run_id,p_target_revision,(p_payload->>'observedAtUtc')::timestamptz,
    now_utc,decode(item.value->>'groupFingerprint','hex'),decode(item.value->>'replicaFingerprint','hex'),
    decode(item.value->>'observerReplicaFingerprint','hex'),(item.value->>'replicaIsLocal')::boolean,
    (item.value->>'visibilityScope')::smallint,(item.value->>'stateObserved')::boolean,
    item.value->>'role',item.value->>'operationalState',item.value->>'connectedState'
  FROM jsonb_array_elements(p_payload->'replicas') AS item(value);
  GET DIAGNOSTICS inserted = ROW_COUNT;
  INSERT INTO telemetry.availability_database_replica_evidence
   (instance_id,run_id,target_revision,observed_at,collected_at,group_fingerprint,
    replica_fingerprint,database_fingerprint,observer_replica_fingerprint,replica_is_local,
    visibility_scope,state_observed,synchronization_state,synchronization_health,database_state,
    is_suspended,log_send_queue_kb,log_send_rate_kb_per_second,redo_queue_kb,redo_rate_kb_per_second,
    source_last_commit_local)
  SELECT p_instance_id,p_run_id,p_target_revision,(p_payload->>'observedAtUtc')::timestamptz,
    now_utc,decode(item.value->>'groupFingerprint','hex'),decode(item.value->>'replicaFingerprint','hex'),
    decode(item.value->>'databaseFingerprint','hex'),decode(item.value->>'observerReplicaFingerprint','hex'),
    (item.value->>'replicaIsLocal')::boolean,(item.value->>'visibilityScope')::smallint,
    (item.value->>'stateObserved')::boolean,item.value->>'synchronizationState',
    item.value->>'synchronizationHealth',item.value->>'databaseState',(item.value->>'isSuspended')::boolean,
    (item.value->>'logSendQueueKb')::bigint,(item.value->>'logSendRateKbPerSecond')::bigint,
    (item.value->>'redoQueueKb')::bigint,(item.value->>'redoRateKbPerSecond')::bigint,
    (item.value->>'sourceLastCommitLocal')::timestamp
  FROM jsonb_array_elements(p_payload->'databases') AS item(value);
  GET DIAGNOSTICS database_count = ROW_COUNT;
  inserted := inserted+database_count;
  IF inserted<>p_output_item_count
  THEN RAISE EXCEPTION 'AG v2 persisted item accounting mismatch' USING ERRCODE='22023'; END IF;
  written_bytes := p_output_bytes;
 END IF;
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
END;
$fn$;
REVOKE ALL ON FUNCTION control.commit_availability_groups_v2_collection_run(
 uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
 text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,
 text,integer,jsonb,bytea) FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
