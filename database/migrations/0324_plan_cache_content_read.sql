-- Exact retained cache evidence is required; no latest-query or payload-ID fallback.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.get_plan_cache_content_payload(p_target uuid,p_run uuid,p_database integer,
 p_incarnation text,p_query text,p_plan text,p_entry text,p_kind text)
RETURNS TABLE(reason text,observed_at timestamptz,fingerprint bytea,protection_algorithm text,
 key_identifier text,nonce bytea,authentication_tag bytea,ciphertext bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $read$
BEGIN
 IF p_target IS NULL OR p_run IS NULL OR p_run='00000000-0000-0000-0000-000000000000'::uuid
  OR p_database IS NULL OR p_database NOT BETWEEN 5 AND 32766
  OR p_incarnation IS NULL OR p_incarnation !~ '^[0-9a-f]{64}$'
  OR p_query IS NULL OR p_query !~ '^[0-9a-f]{64}$' OR p_plan IS NULL OR p_plan !~ '^[0-9a-f]{64}$'
  OR p_entry IS NULL OR p_entry !~ '^[0-9a-f]{64}$' OR p_kind IS NULL OR p_kind NOT IN ('query_text','execution_plan') THEN
  RAISE EXCEPTION 'Cache content identity is invalid' USING ERRCODE='22023'; END IF;
 IF NOT control.query_performance_target_claim(p_target)
  OR current_setting('sqlobserver.role',true) IS DISTINCT FROM 'QueryTextReader' THEN
  RAISE EXCEPTION 'Cache content requires exact target and content-reader context' USING ERRCODE='42501'; END IF;

 RETURN QUERY
 WITH evidence AS MATERIALIZED (
  SELECT delta.source_read_completed_at AS captured_at,run.collector_version,
   content.capture_state,link.content_state,payload.fingerprint,payload.protection_algorithm,
   payload.key_identifier,payload.nonce,payload.authentication_tag,payload.ciphertext
  FROM control.observation_target target
  JOIN events.plan_cache_runtime_delta delta ON delta.instance_id=target.instance_id AND delta.target_revision=target.revision
  JOIN events.plan_cache_runtime_receipt counter_receipt ON counter_receipt.instance_id=delta.instance_id
   AND counter_receipt.run_id=delta.run_id AND counter_receipt.committed_at=delta.committed_at
   AND counter_receipt.target_revision=delta.target_revision AND counter_receipt.source_epoch=delta.source_epoch
   AND counter_receipt.source_read_completed_at=delta.source_read_completed_at
  JOIN telemetry.collection_run_identity identity ON identity.run_id=delta.run_id
  JOIN telemetry.collection_run_v2 run ON run.run_id=identity.run_id AND run.started_at=identity.started_at
   AND run.instance_id=delta.instance_id AND run.target_revision=delta.target_revision
   AND run.collector_id='queries.performance' AND run.collector_version IN (2,3) AND run.output_schema_version=run.collector_version
  JOIN telemetry.collection_run_outcome outcome ON outcome.run_id=delta.run_id AND outcome.outcome IN ('succeeded','partial')
  LEFT JOIN events.plan_cache_content_receipt content ON content.instance_id=delta.instance_id
   AND content.run_id=delta.run_id AND content.committed_at=delta.committed_at AND content.target_revision=delta.target_revision
   AND content.source_epoch=delta.source_epoch AND content.source_read_completed_at=delta.source_read_completed_at
  LEFT JOIN events.plan_cache_content_link link ON link.instance_id=delta.instance_id AND link.run_id=delta.run_id
   AND link.committed_at=delta.committed_at AND link.target_revision=delta.target_revision AND link.database_id=delta.database_id
   AND link.database_incarnation=delta.database_incarnation AND link.source_epoch=delta.source_epoch
   AND link.query_fingerprint=delta.query_fingerprint AND link.plan_fingerprint=delta.plan_fingerprint
   AND link.cache_entry_fingerprint=delta.cache_entry_fingerprint AND link.source_read_completed_at=delta.source_read_completed_at
   AND link.payload_kind=p_kind
  LEFT JOIN security.protected_diagnostic_payload payload ON payload.payload_id=link.content_reference
   AND payload.instance_id=link.instance_id AND payload.payload_kind=p_kind AND payload.fingerprint=link.payload_fingerprint
   AND octet_length(payload.ciphertext)=link.ciphertext_bytes
   AND octet_length(payload.ciphertext) BETWEEN 1 AND (CASE WHEN p_kind='query_text' THEN 16384 ELSE 1048576 END)
  WHERE target.instance_id=p_target AND delta.run_id=p_run AND delta.database_id=p_database
   AND delta.database_incarnation=decode(p_incarnation,'hex') AND delta.query_fingerprint=decode(p_query,'hex')
   AND delta.plan_fingerprint=decode(p_plan,'hex') AND delta.cache_entry_fingerprint=decode(p_entry,'hex')
 ), classified AS (
  SELECT e.*,CASE
   WHEN e.collector_version<>3 OR e.capture_state IS NULL THEN 'capture_missing'
   WHEN e.capture_state='disabled' THEN 'capture_disabled'
   WHEN e.capture_state='protection_unavailable' THEN 'protection_unavailable'
   WHEN e.content_state IS NULL THEN 'not_selected'
   WHEN e.content_state='unavailable' THEN 'source_unavailable'
   WHEN e.content_state<>'available' THEN e.content_state
   WHEN e.fingerprint IS NULL THEN 'evidence_unavailable'
   ELSE 'available' END AS read_reason FROM evidence e
 ) SELECT classified.read_reason,classified.captured_at,
   CASE WHEN classified.read_reason='available' THEN classified.fingerprint END,
   CASE WHEN classified.read_reason='available' THEN classified.protection_algorithm END,
   CASE WHEN classified.read_reason='available' THEN classified.key_identifier END,
   CASE WHEN classified.read_reason='available' THEN classified.nonce END,
   CASE WHEN classified.read_reason='available' THEN classified.authentication_tag END,
   CASE WHEN classified.read_reason='available' THEN classified.ciphertext END
  FROM classified;
 IF NOT FOUND THEN
  RETURN QUERY SELECT 'evidence_unavailable'::text,NULL::timestamptz,NULL::bytea,NULL::text,NULL::text,NULL::bytea,NULL::bytea,NULL::bytea;
 END IF;
END $read$;
REVOKE ALL ON FUNCTION control.get_plan_cache_content_payload(uuid,uuid,integer,text,text,text,text,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_plan_cache_content_payload(uuid,uuid,integer,text,text,text,text,text) TO sqlobserver_server;

-- Reuse the append-only audit envelope rather than creating unretained content logs.
CREATE FUNCTION control.audit_plan_cache_content_access(p_target uuid,p_run uuid,p_database integer,
 p_incarnation text,p_query text,p_plan text,p_entry text,p_kind text,p_actor text,p_outcome text)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $audit$
BEGIN
 IF p_target IS NULL OR p_run IS NULL OR p_run='00000000-0000-0000-0000-000000000000'::uuid
  OR p_database IS NULL OR p_database NOT BETWEEN 5 AND 32766
  OR p_incarnation IS NULL OR p_incarnation !~ '^[0-9a-f]{64}$'
  OR p_query IS NULL OR p_query !~ '^[0-9a-f]{64}$' OR p_plan IS NULL OR p_plan !~ '^[0-9a-f]{64}$'
  OR p_entry IS NULL OR p_entry !~ '^[0-9a-f]{64}$' OR p_kind IS NULL OR p_kind NOT IN ('query_text','execution_plan')
  OR p_actor IS NULL OR length(p_actor) NOT BETWEEN 1 AND 184
  OR p_outcome IS NULL OR p_outcome NOT IN ('denied','unavailable','opened') THEN
  RAISE EXCEPTION 'Cache content audit identity is invalid' USING ERRCODE='22023'; END IF;
 IF NOT control.query_performance_target_claim(p_target) THEN
  RAISE EXCEPTION 'Cache content audit requires exact target scope' USING ERRCODE='42501'; END IF;
 INSERT INTO audit.activity(activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
  subject_kind,subject_identifier,correlation_id,safe_details)
 VALUES(gen_random_uuid(),'user',p_actor,'query.cache_content.read',
  CASE WHEN p_outcome='denied' THEN 'denied' ELSE 'allowed' END,
  CASE WHEN p_outcome='denied' THEN 'rejected' ELSE 'succeeded' END,
  'observation_target',p_target::text,gen_random_uuid(),jsonb_build_object('collectionRunId',p_run,'databaseId',p_database,
   'databaseIncarnation',p_incarnation,'queryFingerprint',p_query,'planFingerprint',p_plan,
   'cacheEntryFingerprint',p_entry,'payloadKind',p_kind,'accessOutcome',p_outcome));
END $audit$;
REVOKE ALL ON FUNCTION control.audit_plan_cache_content_access(uuid,uuid,integer,text,text,text,text,text,text,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.audit_plan_cache_content_access(uuid,uuid,integer,text,text,text,text,text,text,text) TO sqlobserver_server;
