-- Staged qualified cache content. No v3 catalog, schedule, runtime grant or
-- public content projection is activated by this migration.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE events.plan_cache_content_receipt
(
 committed_at timestamptz NOT NULL CHECK(isfinite(committed_at)),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 instance_id uuid NOT NULL,target_revision bigint NOT NULL CHECK(target_revision>0),
 payload_digest bytea NOT NULL CHECK(octet_length(payload_digest)=32),
 source_epoch bytea NOT NULL CHECK(octet_length(source_epoch)=32),
 source_read_completed_at timestamptz NOT NULL CHECK(isfinite(source_read_completed_at)),
 capture_state text NOT NULL CHECK(capture_state IN ('enabled','disabled','protection_unavailable')),
 content_limit integer NOT NULL CHECK(content_limit BETWEEN 0 AND 8),
 attempted_entries integer NOT NULL CHECK(attempted_entries BETWEEN 0 AND content_limit),
 unqualified_entries integer NOT NULL CHECK(unqualified_entries BETWEEN 0 AND attempted_entries),
 retained_entries integer NOT NULL CHECK(retained_entries BETWEEN 0 AND 8),
 source_content_bytes integer NOT NULL CHECK(source_content_bytes BETWEEN 0 AND 2097152 AND source_content_bytes%2=0),
 ciphertext_bytes integer NOT NULL CHECK(ciphertext_bytes BETWEEN 0 AND 2097152),
 PRIMARY KEY(committed_at,run_id),
 CHECK(retained_entries+unqualified_entries=attempted_entries
  AND (capture_state='enabled')=(content_limit>0)
  AND (attempted_entries>0 OR source_content_bytes=0)
  AND ciphertext_bytes::bigint*2<=source_content_bytes::bigint*3)
) PARTITION BY RANGE(committed_at);
CREATE INDEX ix_plan_cache_content_receipt_run ON events.plan_cache_content_receipt(run_id);
CREATE INDEX ix_plan_cache_content_receipt_target ON events.plan_cache_content_receipt(instance_id,run_id);

CREATE TABLE events.plan_cache_content_link
(
 committed_at timestamptz NOT NULL CHECK(isfinite(committed_at)),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 instance_id uuid NOT NULL,target_revision bigint NOT NULL CHECK(target_revision>0),
 database_id integer NOT NULL CHECK(database_id BETWEEN 5 AND 32766),
 database_incarnation bytea NOT NULL CHECK(octet_length(database_incarnation)=32),
 source_epoch bytea NOT NULL CHECK(octet_length(source_epoch)=32),
 query_fingerprint bytea NOT NULL CHECK(octet_length(query_fingerprint)=32),
 plan_fingerprint bytea NOT NULL CHECK(octet_length(plan_fingerprint)=32),
 cache_entry_fingerprint bytea NOT NULL CHECK(octet_length(cache_entry_fingerprint)=32),
 source_read_completed_at timestamptz NOT NULL CHECK(isfinite(source_read_completed_at)),
 payload_kind text NOT NULL CHECK(payload_kind IN ('query_text','execution_plan')),
 content_state text NOT NULL CHECK(content_state IN
  ('available','unavailable','oversized','response_byte_limit','protection_unavailable','protection_failed')),
 content_reference uuid,payload_fingerprint bytea,ciphertext_bytes integer,
 PRIMARY KEY(committed_at,run_id,database_id,cache_entry_fingerprint,payload_kind),
 FOREIGN KEY(content_reference,instance_id) REFERENCES security.protected_diagnostic_payload(payload_id,instance_id),
 CHECK((content_state='available' AND content_reference IS NOT NULL AND payload_fingerprint IS NOT NULL
    AND octet_length(payload_fingerprint)=32 AND ciphertext_bytes IS NOT NULL
    AND ciphertext_bytes BETWEEN 1 AND CASE WHEN payload_kind='query_text' THEN 16384 ELSE 1048576 END)
  OR (content_state<>'available' AND content_reference IS NULL AND payload_fingerprint IS NULL AND ciphertext_bytes IS NULL))
) PARTITION BY RANGE(committed_at);
CREATE INDEX ix_plan_cache_content_link_run ON events.plan_cache_content_link(run_id);
CREATE INDEX ix_plan_cache_content_link_payload ON events.plan_cache_content_link(content_reference) WHERE content_reference IS NOT NULL;
CREATE INDEX ix_plan_cache_content_link_lookup ON events.plan_cache_content_link
 (instance_id,run_id,database_id,database_incarnation,cache_entry_fingerprint,payload_kind);

ALTER TABLE events.plan_cache_content_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.plan_cache_content_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY cache_content_receipt_scope ON events.plan_cache_content_receipt FOR ALL TO sqlobserver_migrator
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
 WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
ALTER TABLE events.plan_cache_content_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE events.plan_cache_content_link FORCE ROW LEVEL SECURITY;
CREATE POLICY cache_content_link_scope ON events.plan_cache_content_link FOR ALL TO sqlobserver_migrator
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
 WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
REVOKE ALL ON events.plan_cache_content_receipt,events.plan_cache_content_link FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER cache_content_receipt_append_only BEFORE UPDATE OR DELETE ON events.plan_cache_content_receipt
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER cache_content_link_append_only BEFORE UPDATE OR DELETE ON events.plan_cache_content_link
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE FUNCTION events.apply_plan_cache_content_batch(p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_payload jsonb)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $content$
DECLARE v_run telemetry.collection_run%ROWTYPE; v_cache events.plan_cache_runtime_receipt%ROWTYPE;
 v_prior events.plan_cache_content_receipt%ROWTYPE; v_digest bytea; v_row jsonb; v_value jsonb;
 v_kind text; v_name text; v_state text; v_payload uuid;
 v_database integer; v_bytes integer:=0; v_retained integer; v_keys text[];
BEGIN
 IF p_run_id IS NULL OR p_run_id='00000000-0000-0000-0000-000000000000'::uuid OR p_instance_id IS NULL
  OR p_target_revision IS NULL OR p_target_revision<=0
  OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN
  RAISE EXCEPTION 'Cache content requires exact run and target scope' USING ERRCODE='42501'; END IF;
 IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR octet_length(p_payload::text)>32768 THEN
  RAISE EXCEPTION 'Cache content envelope is outside its bound' USING ERRCODE='22023'; END IF;
 SELECT array_agg(key ORDER BY key) INTO v_keys FROM jsonb_object_keys(p_payload) key;
 IF v_keys IS DISTINCT FROM ARRAY['attemptedEntries','captureState','contentLimit','items','readCompletedUtc',
    'schemaVersion','sourceContentBytes','sourceEpoch','targetId','targetRevision','unqualifiedEntries']
  OR p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb
  OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
  OR jsonb_typeof(p_payload->'targetRevision') IS DISTINCT FROM 'number'
  OR p_payload->>'targetRevision' !~ '^[1-9][0-9]{0,18}$' OR (p_payload->>'targetRevision')::bigint<>p_target_revision
  OR jsonb_typeof(p_payload->'sourceEpoch') IS DISTINCT FROM 'string' OR p_payload->>'sourceEpoch' !~ '^[0-9a-f]{64}$'
  OR jsonb_typeof(p_payload->'readCompletedUtc') IS DISTINCT FROM 'string'
  OR p_payload->>'readCompletedUtc' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6}|\.[0-9]{6}0)?Z$' OR NOT isfinite((p_payload->>'readCompletedUtc')::timestamptz)
  OR jsonb_typeof(p_payload->'captureState') IS DISTINCT FROM 'string'
  OR p_payload->>'captureState' NOT IN ('enabled','disabled','protection_unavailable')
  OR jsonb_typeof(p_payload->'items') IS DISTINCT FROM 'array' THEN
  RAISE EXCEPTION 'Cache content header is invalid' USING ERRCODE='22023'; END IF;
 FOREACH v_name IN ARRAY ARRAY['contentLimit','attemptedEntries','unqualifiedEntries','sourceContentBytes'] LOOP
  IF jsonb_typeof(p_payload->v_name) IS DISTINCT FROM 'number' OR p_payload->>v_name !~ '^(0|[1-9][0-9]{0,6})$' THEN
   RAISE EXCEPTION 'Cache content accounting is invalid' USING ERRCODE='22023'; END IF;
 END LOOP;
 v_retained:=jsonb_array_length(p_payload->'items');
 IF (p_payload->>'contentLimit')::integer NOT BETWEEN 0 AND 8
  OR (p_payload->>'attemptedEntries')::integer NOT BETWEEN 0 AND (p_payload->>'contentLimit')::integer
  OR (p_payload->>'unqualifiedEntries')::integer<0
  OR v_retained+(p_payload->>'unqualifiedEntries')::integer<>(p_payload->>'attemptedEntries')::integer
  OR (p_payload->>'sourceContentBytes')::integer NOT BETWEEN 0 AND 2097152 OR (p_payload->>'sourceContentBytes')::integer%2<>0
  OR ((p_payload->>'captureState')='enabled')<>((p_payload->>'contentLimit')::integer>0)
  OR ((p_payload->>'attemptedEntries')::integer=0 AND (p_payload->>'sourceContentBytes')::integer<>0) THEN
  RAISE EXCEPTION 'Cache content accounting differs' USING ERRCODE='22023'; END IF;
 SELECT * INTO v_run FROM telemetry.lock_collection_run_runtime(p_run_id);
 IF NOT FOUND OR v_run.instance_id<>p_instance_id OR v_run.target_revision<>p_target_revision
  OR v_run.collector_id<>'queries.performance' OR v_run.collector_version<>3 OR v_run.output_schema_version<>3
  OR v_run.work_key IS DISTINCT FROM 'collector/run/queries.performance/'||replace(p_instance_id::text,'-','')
  OR EXISTS(SELECT 1 FROM telemetry.collection_run_outcome WHERE run_id=p_run_id) THEN
  RAISE EXCEPTION 'Cache content requires an unfinished v3 canonical run' USING ERRCODE='55000'; END IF;
 PERFORM control.assert_worker_lease(v_run.work_key,v_run.owner_execution_id,v_run.fencing_token);
 PERFORM 1 FROM control.observation_target WHERE instance_id=p_instance_id AND revision=p_target_revision AND lifecycle_state='active' FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Cache content target revision is unavailable' USING ERRCODE='55000'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:cache-runtime:target:'||p_instance_id::text,0));
 SELECT * INTO v_cache FROM events.plan_cache_runtime_receipt WHERE instance_id=p_instance_id AND run_id=p_run_id;
 IF NOT FOUND OR v_cache.target_revision<>p_target_revision OR v_cache.source_epoch<>decode(p_payload->>'sourceEpoch','hex')
  OR v_cache.source_read_completed_at<>(p_payload->>'readCompletedUtc')::timestamptz
  OR (p_payload->>'contentLimit')::integer>v_cache.candidate_limit
  OR (p_payload->>'unqualifiedEntries')::integer>v_cache.unqualified_rows
  OR v_retained>v_cache.statement_rows OR (p_payload->>'attemptedEntries')::integer>least(v_cache.emitted_rows,v_cache.candidate_limit) THEN
  RAISE EXCEPTION 'Cache content requires its exact qualified counter receipt' USING ERRCODE='23503'; END IF;
 v_digest:=sha256(convert_to(p_payload::text,'UTF8'));
 SELECT * INTO v_prior FROM events.plan_cache_content_receipt WHERE instance_id=p_instance_id AND run_id=p_run_id;
 IF FOUND THEN
  IF v_prior.payload_digest<>v_digest THEN RAISE EXCEPTION 'Cache content replay differs' USING ERRCODE='40001'; END IF;
  RETURN 0;
 END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_content_link WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN
  RAISE EXCEPTION 'Cache content links have no receipt' USING ERRCODE='55000'; END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(p_payload->'items') item(value)
  GROUP BY item.value->>'cacheEntryFingerprint' HAVING count(*)>1) THEN
  RAISE EXCEPTION 'Cache content repeated an entry' USING ERRCODE='22023'; END IF;
 FOR v_row IN SELECT value FROM jsonb_array_elements(p_payload->'items') LOOP
  IF jsonb_typeof(v_row) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'Cache content item is invalid' USING ERRCODE='22023'; END IF;
  SELECT array_agg(key ORDER BY key) INTO v_keys FROM jsonb_object_keys(v_row) key;
  IF v_keys IS DISTINCT FROM ARRAY['cacheEntryFingerprint','databaseId','databaseIncarnation','plan','planFingerprint','queryFingerprint','text']
   OR jsonb_typeof(v_row->'databaseId') IS DISTINCT FROM 'number' OR v_row->>'databaseId' !~ '^[0-9]{1,5}$'
   OR (v_row->>'databaseId')::integer NOT BETWEEN 5 AND 32766 THEN
   RAISE EXCEPTION 'Cache content item identity is invalid' USING ERRCODE='22023'; END IF;
  FOREACH v_name IN ARRAY ARRAY['databaseIncarnation','queryFingerprint','planFingerprint','cacheEntryFingerprint'] LOOP
   IF jsonb_typeof(v_row->v_name) IS DISTINCT FROM 'string' OR v_row->>v_name !~ '^[0-9a-f]{64}$' THEN
    RAISE EXCEPTION 'Cache content item fingerprint is invalid' USING ERRCODE='22023'; END IF;
  END LOOP;
  v_database:=(v_row->>'databaseId')::integer;
  IF NOT EXISTS(SELECT 1 FROM events.plan_cache_runtime_delta delta
   WHERE delta.instance_id=p_instance_id AND delta.run_id=p_run_id AND delta.committed_at=v_cache.committed_at
    AND delta.target_revision=p_target_revision AND delta.source_epoch=v_cache.source_epoch
    AND delta.source_read_completed_at=v_cache.source_read_completed_at AND delta.database_id=v_database
    AND delta.database_incarnation=decode(v_row->>'databaseIncarnation','hex') AND delta.query_fingerprint=decode(v_row->>'queryFingerprint','hex')
    AND delta.plan_fingerprint=decode(v_row->>'planFingerprint','hex') AND delta.cache_entry_fingerprint=decode(v_row->>'cacheEntryFingerprint','hex')) THEN
   RAISE EXCEPTION 'Cache content has no exact qualified statement' USING ERRCODE='23503'; END IF;
  FOREACH v_name IN ARRAY ARRAY['text','plan'] LOOP
   v_value:=v_row->v_name; v_payload:=NULL;
   v_kind:=CASE WHEN v_name='text' THEN 'query_text' ELSE 'execution_plan' END;
   IF jsonb_typeof(v_value) IS DISTINCT FROM 'object' OR jsonb_typeof(v_value->'state') IS DISTINCT FROM 'string' THEN
    RAISE EXCEPTION 'Cache content value is invalid' USING ERRCODE='22023'; END IF;
   v_state:=v_value->>'state';
   SELECT array_agg(key ORDER BY key) INTO v_keys FROM jsonb_object_keys(v_value) key;
   IF v_state='available' THEN
    IF v_keys IS DISTINCT FROM ARRAY['ciphertextBytes','fingerprint','payloadId','state']
     OR jsonb_typeof(v_value->'payloadId') IS DISTINCT FROM 'string'
     OR v_value->>'payloadId' !~ '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'
     OR jsonb_typeof(v_value->'fingerprint') IS DISTINCT FROM 'string' OR v_value->>'fingerprint' !~ '^[0-9a-f]{64}$'
     OR jsonb_typeof(v_value->'ciphertextBytes') IS DISTINCT FROM 'number' OR v_value->>'ciphertextBytes' !~ '^[1-9][0-9]{0,6}$' THEN
     RAISE EXCEPTION 'Cache content reference is invalid' USING ERRCODE='22023'; END IF;
    v_payload:=(v_value->>'payloadId')::uuid;
    IF (v_value->>'ciphertextBytes')::integer NOT BETWEEN 1 AND (CASE WHEN v_kind='query_text' THEN 16384 ELSE 1048576 END)
     OR NOT EXISTS(SELECT 1 FROM security.protected_diagnostic_payload payload WHERE payload.payload_id=v_payload
      AND payload.instance_id=p_instance_id AND payload.payload_kind=v_kind AND payload.fingerprint=decode(v_value->>'fingerprint','hex')
      AND octet_length(payload.ciphertext)=(v_value->>'ciphertextBytes')::integer) THEN
     RAISE EXCEPTION 'Cache content reference does not match its exact target, kind or bytes' USING ERRCODE='23503'; END IF;
    v_bytes:=v_bytes+(v_value->>'ciphertextBytes')::integer;
   ELSIF v_state NOT IN ('unavailable','oversized','response_byte_limit','protection_unavailable','protection_failed')
     OR v_keys IS DISTINCT FROM ARRAY['state'] THEN
    RAISE EXCEPTION 'Unavailable cache content cannot carry a reference' USING ERRCODE='22023'; END IF;
   INSERT INTO events.plan_cache_content_link(committed_at,run_id,instance_id,target_revision,database_id,database_incarnation,
    source_epoch,query_fingerprint,plan_fingerprint,cache_entry_fingerprint,source_read_completed_at,payload_kind,content_state,
    content_reference,payload_fingerprint,ciphertext_bytes)
   VALUES(v_cache.committed_at,p_run_id,p_instance_id,p_target_revision,v_database,decode(v_row->>'databaseIncarnation','hex'),
    v_cache.source_epoch,decode(v_row->>'queryFingerprint','hex'),decode(v_row->>'planFingerprint','hex'),decode(v_row->>'cacheEntryFingerprint','hex'),
    v_cache.source_read_completed_at,v_kind,v_state,v_payload,
    CASE WHEN v_payload IS NOT NULL THEN decode(v_value->>'fingerprint','hex') END,
    CASE WHEN v_payload IS NOT NULL THEN (v_value->>'ciphertextBytes')::integer END);
  END LOOP;
 END LOOP;
 IF v_bytes>2097152 OR v_bytes::bigint*2>(p_payload->>'sourceContentBytes')::bigint*3 THEN
  RAISE EXCEPTION 'Cache content ciphertext budget exceeded' USING ERRCODE='22023'; END IF;
 INSERT INTO events.plan_cache_content_receipt VALUES(v_cache.committed_at,p_run_id,p_instance_id,p_target_revision,v_digest,
  v_cache.source_epoch,v_cache.source_read_completed_at,p_payload->>'captureState',(p_payload->>'contentLimit')::integer,
  (p_payload->>'attemptedEntries')::integer,(p_payload->>'unqualifiedEntries')::integer,v_retained,(p_payload->>'sourceContentBytes')::integer,v_bytes);
 PERFORM control.assert_worker_lease(v_run.work_key,v_run.owner_execution_id,v_run.fencing_token);
 RETURN v_retained;
END $content$;
REVOKE ALL ON FUNCTION events.apply_plan_cache_content_batch(uuid,uuid,bigint,jsonb) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- The shared maintenance entry point extends content partitions with counters.
CREATE OR REPLACE FUNCTION control.ensure_plan_cache_runtime_partitions(p_anchor date DEFAULT current_date)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET row_security=on
AS $partitions$
DECLARE v_parent text; v_day date; v_start timestamptz; v_end timestamptz;
    v_name text; v_child oid; v_created integer:=0;
BEGIN
    IF p_anchor IS NULL OR p_anchor<>(clock_timestamp() AT TIME ZONE 'UTC')::date THEN
        RAISE EXCEPTION 'Cache partition anchor must be the current UTC date' USING ERRCODE='22023'; END IF;
    FOREACH v_parent IN ARRAY ARRAY['plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link'] LOOP
        FOR v_day IN SELECT p_anchor+s.i FROM generate_series(-8,1) AS s(i) LOOP
            v_start:=v_day::timestamptz; v_end:=(v_day+1)::timestamptz;
            v_name:=v_parent||'_p'||to_char(v_day,'YYYYMMDD');
            v_child:=to_regclass(format('events.%I',v_name));
            IF v_child IS NULL THEN
                PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:cache-runtime:partition:'||v_name,0));
                IF to_regclass(format('events.%I',v_name)) IS NULL THEN
                    EXECUTE format('CREATE TABLE events.%I PARTITION OF events.%I FOR VALUES FROM (%L) TO (%L)',
                        v_name,v_parent,v_start,v_end);
                    EXECUTE format('ALTER TABLE events.%I ENABLE ROW LEVEL SECURITY',v_name);
                    EXECUTE format('ALTER TABLE events.%I FORCE ROW LEVEL SECURITY',v_name);
                    EXECUTE format('CREATE POLICY cache_runtime_scope ON events.%I FOR ALL TO sqlobserver_migrator
                        USING(instance_id::text=current_setting(%L,true)) WITH CHECK(instance_id::text=current_setting(%L,true))',
                        v_name,'sqlobserver.target_scope','sqlobserver.target_scope');
                    EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',v_name);
                    IF v_parent='plan_cache_content_link' THEN
                        EXECUTE format('GRANT SELECT ON events.%I TO sqlobserver_payload_expirer',v_name);
                    END IF;
                    INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,
                        partition_granularity,range_start,range_end)
                    VALUES('events',v_parent,'events',v_name,'day',v_start,v_end);
                    v_created:=v_created+1;
                END IF;
                v_child:=to_regclass(format('events.%I',v_name));
            END IF;
            IF NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=v_child AND inhparent=format('events.%I',v_parent)::regclass)
                OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid=v_child AND relkind='r' AND relrowsecurity AND relforcerowsecurity
                    AND pg_get_userbyid(relowner)='sqlobserver_migrator'
                    AND pg_get_expr(relpartbound,oid)=format('FOR VALUES FROM (%L) TO (%L)',v_start,v_end))
                OR NOT EXISTS(SELECT 1 FROM system.partition_registry WHERE parent_schema='events' AND parent_table=v_parent
                    AND partition_schema='events' AND partition_name=v_name AND partition_granularity='day'
                    AND lifecycle_state='attached' AND range_start=v_start AND range_end=v_end)
                OR (SELECT count(*) FROM pg_policy WHERE polrelid=v_child)<>1
                OR NOT EXISTS(SELECT 1 FROM pg_policy child JOIN pg_policy parent
                    ON parent.polrelid=format('events.%I',v_parent)::regclass
                    WHERE child.polrelid=v_child AND child.polname='cache_runtime_scope'
                        AND child.polcmd='*' AND child.polpermissive
                        AND child.polroles=ARRAY['sqlobserver_migrator'::regrole::oid]
                        AND pg_get_expr(child.polqual,child.polrelid)=pg_get_expr(parent.polqual,parent.polrelid)
                        AND pg_get_expr(child.polwithcheck,child.polrelid)=pg_get_expr(parent.polwithcheck,parent.polrelid)) THEN
                RAISE EXCEPTION 'Cache partition identity or registry differs' USING ERRCODE='55000'; END IF;
        END LOOP;
    END LOOP;
    RETURN v_created;
END $partitions$;
REVOKE ALL ON FUNCTION control.ensure_plan_cache_runtime_partitions(date)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

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
   'backup_v2_scan','backup_v2_database','backup_v2_sample','sql_metric_baseline','deadlock_read_receipt','plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link'));

ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_requests','m5_waits','m5_blocking',
  'm6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
  'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence','sql_volume_capacity',
  'm7_group_watermarks','m7_group_deltas','collection_run_original_copy',
  'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines',
  'm6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links'));
ALTER TABLE system.retention_policy DROP CONSTRAINT ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND
  ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event')
   OR (data_class='sql_volume_capacity' AND parent_schema='telemetry' AND parent_table='sql_volume_snapshot' AND partition_granularity='day')
   OR (data_class='m7_group_watermarks' AND parent_schema='events' AND parent_table='query_store_group_watermark' AND partition_granularity='day')
   OR (data_class='m7_group_deltas' AND parent_schema='events' AND parent_table='query_store_group_delta' AND partition_granularity='day')
   OR (data_class='m7_query_performance' AND parent_schema='events' AND parent_table='query_performance_observation_v3' AND partition_granularity='day')
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
   OR data_class IN ('raw_metric_samples','m5_activity','m5_requests','m5_waits','m5_blocking','m6_deadlocks',
     'm8_alert_history','m8_alert_delivery','m9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences',
     'm9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases','m10_host_metrics','m10_replication','m10_rollups','m10_evidence')));

INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep,updated_by)
VALUES('m7_cache_content_receipts','events','plan_cache_content_receipt','day',true,interval '30 days',3,'migration-0322'),
('m7_cache_content_links','events','plan_cache_content_link','day',true,interval '30 days',3,'migration-0322');

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
     'm7_query_performance','collection_run_original_copy',
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
    OR (p_data_class='m6_deadlock_read_receipts' AND (p_minimum_partitions<2 OR (p_enabled AND p_retain_for<interval '30 days')))
    OR (p_data_class='m7_cache_watermarks' AND p_enabled AND p_retain_for<interval '8 days')
    OR (p_data_class IN ('m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links') AND p_enabled AND p_retain_for<interval '30 days')
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

SELECT control.ensure_plan_cache_runtime_partitions(current_date);

-- Canonical run cleanup must retain attached and detached content evidence.
CREATE OR REPLACE FUNCTION system.plan_cache_runtime_run_retention_holds(p_instance_id uuid,p_run_id uuid)
RETURNS TABLE(hold_code text) LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,system,events,telemetry SET row_security=on SET TimeZone='UTC'
AS $cache_holds$
DECLARE v_partition record; v_relation oid; v_column name; v_present boolean;
BEGIN
 IF p_instance_id IS NULL OR p_run_id IS NULL
    OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    OR NOT pg_has_role(current_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'Cache run retention preview requires migrator and target scope' USING ERRCODE='42501'; END IF;
 -- Stabilize both attached and detached registry rows before querying data.
 -- A registered detach/drop needs its exclusive row lock in this transaction.
 PERFORM 1 FROM system.partition_registry registry WHERE registry.parent_schema='events'
    AND registry.parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link')
    AND registry.lifecycle_state IN ('attached','detached') FOR SHARE;
 IF EXISTS(SELECT 1 FROM events.plan_cache_runtime_watermark
    WHERE instance_id=p_instance_id AND last_run_id=p_run_id) THEN hold_code:='cache_watermark'; RETURN NEXT; END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_runtime_delta
    WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN hold_code:='cache_delta'; RETURN NEXT; END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_runtime_receipt
    WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN hold_code:='cache_receipt'; RETURN NEXT; END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_content_receipt WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN hold_code:='cache_content_receipt'; RETURN NEXT; END IF;
 IF EXISTS(SELECT 1 FROM events.plan_cache_content_link WHERE instance_id=p_instance_id AND run_id=p_run_id) THEN hold_code:='cache_content_link'; RETURN NEXT; END IF;
 -- Detached grace data is no longer visible through its former parent.
 -- Probe the exact target/run in each registered relation, with its run index.
 FOR v_partition IN SELECT registry.* FROM system.partition_registry registry
  WHERE registry.parent_schema='events'
    AND registry.parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt','plan_cache_content_receipt','plan_cache_content_link')
    AND registry.lifecycle_state='detached' ORDER BY registry.parent_table,registry.partition_name LOOP
  v_relation:=to_regclass(format('%I.%I',v_partition.partition_schema,v_partition.partition_name));
  v_column:=CASE WHEN v_partition.parent_table='plan_cache_runtime_watermark' THEN 'last_run_id' ELSE 'run_id' END;
  IF v_relation IS NULL OR v_partition.partition_schema<>'events' OR NOT EXISTS(
    SELECT 1 FROM pg_class relation WHERE relation.oid=v_relation AND relation.relkind='r'
      AND relation.relrowsecurity AND relation.relforcerowsecurity
      AND pg_get_userbyid(relation.relowner)='sqlobserver_migrator') OR NOT EXISTS(
    SELECT 1 FROM pg_constraint fk JOIN pg_attribute col ON col.attrelid=fk.conrelid AND col.attnum=fk.conkey[1]
    WHERE fk.contype='f' AND fk.conrelid=v_relation AND fk.confrelid='telemetry.collection_run_identity'::regclass
      AND fk.convalidated AND NOT fk.condeferrable AND fk.confdeltype='a' AND col.attname=v_column) THEN
   hold_code:='cache_partition_unverified'; RETURN NEXT; CONTINUE;
  END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I WHERE instance_id=$1 AND %I=$2)',
    v_partition.partition_schema,v_partition.partition_name,v_column) INTO v_present USING p_instance_id,p_run_id;
  IF v_present THEN hold_code:='cache_partition_grace'; RETURN NEXT; END IF;
 END LOOP;
END $cache_holds$;
REVOKE ALL ON FUNCTION system.plan_cache_runtime_run_retention_holds(uuid,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.plan_cache_runtime_run_retention_holds(uuid,uuid) TO sqlobserver_migrator;


-- Preserve references in attached and detached grace content partitions during
-- orphan cleanup. The existing non-login expirer remains the function owner.
RESET ROLE;
DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = 'sqlobserver_payload_expirer'
                     AND rolcanlogin = false AND rolbypassrls = true
                     AND rolsuper = false AND rolcreatedb = false
                     AND rolcreaterole = false AND rolinherit = false
                     AND rolreplication = false) THEN
        RAISE EXCEPTION 'safe payload expirer role is required' USING ERRCODE = '55000';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
               JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
               WHERE member_role.rolname = 'sqlobserver_payload_expirer') THEN
        RAISE EXCEPTION 'payload expirer must not inherit another role' USING ERRCODE = '55000';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
                   JOIN pg_catalog.pg_roles granted_role ON granted_role.oid = membership.roleid
                   JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
                   WHERE granted_role.rolname = 'sqlobserver_payload_expirer'
                     AND member_role.rolname = current_user AND membership.admin_option) THEN
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                       WHERE rolname = current_user AND rolsuper) THEN
            RAISE EXCEPTION 'payload expirer requires temporary bootstrap membership'
                USING ERRCODE = '42501';
        END IF;
        EXECUTE format('GRANT sqlobserver_payload_expirer TO %I WITH ADMIN OPTION', current_user);
    END IF;
END
$role$;
GRANT sqlobserver_payload_expirer TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;

GRANT SELECT ON events.plan_cache_content_link TO sqlobserver_payload_expirer;
GRANT SELECT,UPDATE ON system.partition_registry TO sqlobserver_payload_expirer;
GRANT CREATE ON SCHEMA system TO sqlobserver_payload_expirer;
SET LOCAL ROLE sqlobserver_payload_expirer;

CREATE FUNCTION system.plan_cache_content_payload_hold(p_payload_id uuid)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $payload_hold$
DECLARE v_partition record; v_relation oid; v_present boolean;
BEGIN
 IF p_payload_id IS NULL THEN RAISE EXCEPTION 'Cache content payload identity is required' USING ERRCODE='22023'; END IF;
 PERFORM 1 FROM system.partition_registry registry WHERE registry.parent_schema='events'
  AND registry.parent_table='plan_cache_content_link' AND registry.lifecycle_state IN ('attached','detached') FOR SHARE;
 IF EXISTS(SELECT 1 FROM events.plan_cache_content_link WHERE content_reference=p_payload_id) THEN RETURN true; END IF;
 FOR v_partition IN SELECT registry.* FROM system.partition_registry registry
  WHERE registry.parent_schema='events' AND registry.parent_table='plan_cache_content_link' AND registry.lifecycle_state='detached'
  ORDER BY registry.partition_name LOOP
  v_relation:=to_regclass(format('%I.%I',v_partition.partition_schema,v_partition.partition_name));
  IF v_relation IS NULL OR v_partition.partition_schema<>'events' OR NOT EXISTS(
   SELECT 1 FROM pg_class relation WHERE relation.oid=v_relation AND relation.relkind='r' AND relation.relrowsecurity AND relation.relforcerowsecurity
    AND pg_get_userbyid(relation.relowner)='sqlobserver_migrator') OR NOT EXISTS(
   SELECT 1 FROM pg_constraint fk WHERE fk.conrelid=v_relation AND fk.contype='f'
    AND fk.confrelid='security.protected_diagnostic_payload'::regclass AND fk.convalidated AND NOT fk.condeferrable AND fk.confdeltype='a') THEN
   RETURN true; -- Unverified registered grace data must not lose any ciphertext.
  END IF;
  IF NOT has_table_privilege(current_user,v_relation,'SELECT') THEN RETURN true; END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I WHERE content_reference=$1)',v_partition.partition_schema,v_partition.partition_name)
    INTO v_present USING p_payload_id;
  IF v_present THEN RETURN true; END IF;
 END LOOP;
 RETURN false;
END $payload_hold$;
REVOKE ALL ON FUNCTION system.plan_cache_content_payload_hold(uuid) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor,sqlobserver_migrator;

CREATE OR REPLACE FUNCTION system.prune_orphan_query_text_payloads(
    p_owner_execution_id uuid, p_fencing_token bigint, p_limit integer)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, system, control, security, events, audit
SET TimeZone = 'UTC'
AS $prune$
DECLARE
    v_deleted integer;
BEGIN
    IF p_owner_execution_id IS NULL OR p_fencing_token IS NULL
       OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'orphan payload cleanup arguments are invalid'
            USING ERRCODE = '22023';
    END IF;
    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);

    WITH candidate AS (
        SELECT payload.payload_id
        FROM security.protected_diagnostic_payload AS payload
        WHERE payload.payload_kind = 'query_text'
          AND payload.instance_id IS NOT NULL
          AND payload.created_at < clock_timestamp() - interval '7 days'
          AND coalesce(payload.last_seen_at, payload.created_at)
              < clock_timestamp() - interval '7 days'
          AND NOT EXISTS (
              SELECT 1 FROM events.query_performance_content_link AS link
              WHERE link.content_reference = payload.payload_id)
          AND NOT EXISTS (
              SELECT 1 FROM events.diagnostic_event AS event
              WHERE event.protected_payload_id = payload.payload_id)
          AND NOT system.plan_cache_content_payload_hold(payload.payload_id)
        ORDER BY payload.created_at, payload.payload_id
        LIMIT p_limit
        FOR UPDATE OF payload SKIP LOCKED
    ), deleted AS (
        DELETE FROM security.protected_diagnostic_payload AS payload
        USING candidate
        WHERE payload.payload_id = candidate.payload_id
        RETURNING payload.payload_id
    )
    SELECT count(*) INTO v_deleted FROM deleted;

    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);
    IF v_deleted > 0 THEN
        INSERT INTO audit.activity
            (activity_id, actor_kind, actor_identifier, action_name,
             authorization_result, outcome, correlation_id, safe_details)
        VALUES (gen_random_uuid(), 'system', 'collector/retention',
                'retention.query_text_orphans.prune', 'not_applicable',
                'succeeded', gen_random_uuid(),
                jsonb_build_object('deletedCount', v_deleted,
                                   'minimumAgeDays', 7,
                                   'fencingToken', p_fencing_token));
    END IF;
    RETURN v_deleted;
END;
$prune$;

REVOKE ALL ON FUNCTION system.prune_orphan_query_text_payloads(uuid,bigint,integer)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.prune_orphan_query_text_payloads(uuid,bigint,integer)
    TO sqlobserver_collector;


CREATE OR REPLACE FUNCTION system.prune_orphan_query_plan_payloads(
    p_owner_execution_id uuid, p_fencing_token bigint, p_limit integer)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, system, control, security, events, audit
SET TimeZone = 'UTC'
AS $prune$
DECLARE v_deleted integer;
BEGIN
    IF p_owner_execution_id IS NULL OR p_fencing_token IS NULL
       OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'orphan plan cleanup arguments are invalid' USING ERRCODE = '22023';
    END IF;
    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);

    WITH candidate AS (
        SELECT payload.payload_id
        FROM security.protected_diagnostic_payload AS payload
        WHERE payload.payload_kind = 'execution_plan'
          AND payload.instance_id IS NOT NULL
          AND payload.created_at < clock_timestamp() - interval '7 days'
          AND coalesce(payload.last_seen_at, payload.created_at)
              < clock_timestamp() - interval '7 days'
          AND NOT EXISTS (
              SELECT 1 FROM events.query_performance_plan_content_link AS link
              WHERE link.content_reference = payload.payload_id)
          AND NOT EXISTS (
              SELECT 1 FROM events.diagnostic_event AS event
              WHERE event.protected_payload_id = payload.payload_id)
          AND NOT system.plan_cache_content_payload_hold(payload.payload_id)
        ORDER BY payload.created_at, payload.payload_id
        LIMIT p_limit
        FOR UPDATE OF payload SKIP LOCKED
    ), deleted AS (
        DELETE FROM security.protected_diagnostic_payload AS payload
        USING candidate
        WHERE payload.payload_id = candidate.payload_id
        RETURNING payload.payload_id
    )
    SELECT count(*) INTO v_deleted FROM deleted;

    PERFORM control.assert_worker_lease(
        'retention/maintenance', p_owner_execution_id, p_fencing_token);
    IF v_deleted > 0 THEN
        INSERT INTO audit.activity
            (activity_id, actor_kind, actor_identifier, action_name,
             authorization_result, outcome, correlation_id, safe_details)
        VALUES (gen_random_uuid(), 'system', 'collector/retention',
                'retention.query_plan_orphans.prune', 'not_applicable',
                'succeeded', gen_random_uuid(),
                jsonb_build_object('deletedCount', v_deleted,
                                   'minimumAgeDays', 7,
                                   'fencingToken', p_fencing_token));
    END IF;
    RETURN v_deleted;
END;
$prune$;

REVOKE ALL ON FUNCTION system.prune_orphan_query_plan_payloads(uuid,bigint,integer)
    FROM PUBLIC, sqlobserver_server, sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.prune_orphan_query_plan_payloads(uuid,bigint,integer)
    TO sqlobserver_collector;

SET LOCAL ROLE sqlobserver_migrator;
REVOKE CREATE ON SCHEMA system FROM sqlobserver_payload_expirer;
RESET ROLE;
REVOKE sqlobserver_payload_expirer FROM sqlobserver_migrator;
REVOKE sqlobserver_payload_expirer FROM CURRENT_USER;
