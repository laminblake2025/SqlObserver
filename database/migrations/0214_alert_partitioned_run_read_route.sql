-- Keep alert evidence joins on the same fenced run source as collector health.
-- Installation keeps the original run source until the health route is activated.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE VIEW telemetry.collection_run_alert_read
WITH (security_barrier = true)
AS
SELECT run.run_id,run.instance_id,run.collector_id,
       run.collector_version,run.output_schema_version
FROM telemetry.collection_run AS run
CROSS JOIN system.collection_run_v2_health_read_route AS route
WHERE NOT route.use_v2
UNION ALL
SELECT run.run_id,run.instance_id,run.collector_id,
       run.collector_version,run.output_schema_version
FROM telemetry.collection_run_v2 AS run
CROSS JOIN system.collection_run_v2_health_read_route AS route
WHERE route.use_v2;
REVOKE ALL ON telemetry.collection_run_alert_read
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT SELECT ON telemetry.collection_run_alert_read TO sqlobserver_migrator;

-- Preserve the existing queue and evidence predicates; only the run source changes.
CREATE OR REPLACE FUNCTION alerting.reconcile_due_evidence_internal(p_max_results integer)
RETURNS TABLE(operation_id uuid, observations jsonb, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
BEGIN
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation queue limit is outside bounds' USING ERRCODE='22023'; END IF;
  -- Reconcile immutable collector evidence into the durable queue once. A
  -- repeated poll only claims queue rows; it never rescans old observations.
  INSERT INTO alerting.evaluation_queue(operation_id,instance_id,rule_id,observed_at,observations,due_at,next_due_at,evidence_digest,sample_id,run_id,rule_revision)
  SELECT q.operation_id,q.instance_id,q.rule_id,q.observed_at,q.observations,clock_timestamp(),q.observed_at+q.evaluation_interval,q.evidence_digest,q.sample_id,q.run_id,q.rule_revision
  FROM (
    SELECT alerting.canonical_operation_id(r.instance_id,r.rule_id,e.observed_at,e.reason) operation_id,
      r.instance_id,r.rule_id,e.observed_at,r.evaluation_interval,e.sample_id,e.run_id,r.revision AS rule_revision,
      jsonb_build_array(jsonb_build_object('TargetId',r.instance_id,'RuleId',r.rule_id,'ObservedAtUtc',e.observed_at,'Value',e.metric_value,'CollectorHealthy',e.collector_healthy,'Reason',e.reason,'SampleId',e.sample_id,'RunId',e.run_id,'SourceKind',e.source_kind,'MetricId',e.metric_id,'SourceCollector',e.source_collector,'SourceVersion',e.source_version,'SourceSchemaVersion',e.source_schema_version,'SourceDigest',e.source_digest,'OperationId',alerting.canonical_operation_id(r.instance_id,r.rule_id,e.observed_at,e.reason),'EvidenceDigest',encode(alerting.canonical_evidence_sha256(r.instance_id,r.rule_id,e.source_kind,e.metric_id,e.source_collector,e.source_version,e.source_schema_version,e.source_digest,e.observed_at,e.sample_id::text,e.run_id,e.metric_value,e.collector_healthy,e.reason),'hex'))) observations,
      alerting.canonical_evidence_sha256(r.instance_id,r.rule_id,e.source_kind,e.metric_id,e.source_collector,e.source_version,e.source_schema_version,e.source_digest,e.observed_at,e.sample_id::text,e.run_id,e.metric_value,e.collector_healthy,e.reason) evidence_digest
    FROM alerting.rule r JOIN LATERAL (
      SELECT m.observed_at AS observed_at,m.sample_id AS sample_id,m.collection_run_id AS run_id,m.metric_value AS metric_value,NULL::boolean AS collector_healthy,'observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text AS reason,'metric_threshold'::text AS source_kind,r.metric_id::text AS metric_id,cr.collector_id AS source_collector,cr.collector_version::text AS source_version,cr.output_schema_version AS source_schema_version,encode(co.completion_digest,'hex') AS source_digest,r.instance_id::text || '|' || r.rule_id::text || '|' || to_char(m.observed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|' || coalesce(m.metric_value::text,'null') || '|null|observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text AS evidence_text FROM (SELECT scoped.* FROM telemetry.raw_metric_sample scoped WHERE scoped.instance_id=r.instance_id AND scoped.metric_key=r.metric_id AND scoped.observed_at>=r.updated_at OFFSET 0) m JOIN telemetry.collection_run_alert_read cr ON cr.run_id=m.collection_run_id JOIN telemetry.collection_run_outcome co ON co.run_id=cr.run_id WHERE r.kind=1 AND m.collection_run_id IS NOT NULL AND m.instance_id=r.instance_id AND m.metric_key=r.metric_id AND co.outcome IN ('succeeded','partial') AND co.completion_digest IS NOT NULL AND cr.collector_id='engine.core' AND cr.output_schema_version=1 AND m.observed_at>=greatest(clock_timestamp()-interval '7 days',r.updated_at) AND NOT EXISTS (SELECT 1 FROM alerting.evaluation_queue oldq WHERE oldq.operation_id=alerting.canonical_operation_id(r.instance_id,r.rule_id,m.observed_at,'observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text))
      UNION ALL SELECT CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END AS observed_at,alerting.sha_uuid('collector-health|' || h.collector_id || '|' || h.collector_version::text || '|' || h.schedule_revision::text || '|' || h.schedule_target_revision::text || '|' || h.run_id::text || '|' || coalesce(h.completed_at::text,'') || '|' || coalesce(h.outcome,'') || '|' || h.health_state || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint) AS sample_id,h.run_id,NULL::double precision AS metric_value,(h.health_state='current' AND coh.outcome IN ('succeeded','partial')) AS collector_healthy,'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'') || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint AS reason,'collector_health'::text AS source_kind,NULL::text AS metric_id,h.collector_id AS source_collector,h.collector_version::text AS source_version,h.output_schema_version AS source_schema_version,encode(coh.completion_digest,'hex') AS source_digest,r.instance_id::text || '|' || r.rule_id::text || '|' || to_char((CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END) AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|null|' || CASE WHEN h.health_state='current' THEN 'true' ELSE 'false' END || '|collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'' ) || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint AS evidence_text FROM reporting.collector_health_projection h JOIN telemetry.collection_run_alert_read crh ON crh.run_id=h.run_id JOIN telemetry.collection_run_outcome coh ON coh.run_id=crh.run_id WHERE r.kind=2 AND h.run_id IS NOT NULL AND h.collector_id='engine.core' AND crh.collector_id='engine.core' AND h.collector_id=crh.collector_id AND h.collector_version=crh.collector_version AND h.output_schema_version=crh.output_schema_version AND crh.collector_id='engine.core' AND crh.output_schema_version=1 AND coh.outcome IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open') AND coh.completion_digest IS NOT NULL AND h.instance_id=r.instance_id AND coalesce(h.completed_at,h.repository_time)>=clock_timestamp()-interval '7 days' AND NOT EXISTS (SELECT 1 FROM alerting.evaluation_queue oldq WHERE oldq.operation_id=alerting.canonical_operation_id(r.instance_id,r.rule_id,CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END,'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'' ) || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint))
      -- Equal-time sources remain distinct through sample/run/reason; the outer
      -- query orders the computed canonical operation identity.
      ) e ON true WHERE r.enabled AND (nullif(current_setting('sqlobserver.target_scope',true),'') IS NULL OR r.instance_id::text=current_setting('sqlobserver.target_scope',true)) AND (r.kind=1 OR EXISTS (SELECT 1 FROM reporting.collector_health_projection hp WHERE hp.instance_id=r.instance_id AND hp.collector_id='engine.core'))
  ) q ORDER BY q.observed_at ASC,q.sample_id ASC,q.run_id ASC NULLS LAST,q.operation_id ASC LIMIT p_max_results ON CONFLICT ON CONSTRAINT evaluation_queue_pkey DO NOTHING;
  -- Reconciliation is insert-only. Lease metadata is written exclusively by
  -- the target-scoped fenced claim function below.
  RETURN QUERY SELECT q.operation_id,q.observations,q.due_at FROM alerting.evaluation_queue q
    WHERE q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())
    ORDER BY q.observed_at,q.sample_id,q.run_id NULLS LAST,q.operation_id LIMIT p_max_results;
END;
$$;

CREATE OR REPLACE FUNCTION alerting.assert_evaluation_claims(p_instance_id uuid, p_observations jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
BEGIN
  IF p_instance_id IS NULL OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_lease_fencing IS NULL OR p_lease_fencing <= 0 THEN
    RAISE EXCEPTION 'evaluation claim identity is required' USING ERRCODE='22023';
  END IF;
  -- Lock policy before checking claim revisions and before taking state locks.
  -- Configuration writes take the same rule-to-state order. A queued evaluation
  -- must not pass the revision fence and then commit progress under old policy.
  PERFORM r.rule_id FROM alerting.rule r
  WHERE r.instance_id=p_instance_id AND EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_observations) o WHERE (o->>'RuleId')::uuid=r.rule_id)
  ORDER BY r.rule_id FOR SHARE;
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_observations) o
    WHERE (o->>'TargetId')::uuid IS DISTINCT FROM p_instance_id
       OR NOT EXISTS (
         SELECT 1 FROM alerting.evaluation_queue q
         WHERE q.instance_id=p_instance_id
           AND q.operation_id=(o->>'OperationId')::uuid
           AND q.rule_id=(o->>'RuleId')::uuid
           AND q.observed_at=(o->>'ObservedAtUtc')::timestamptz
           AND q.completed_at IS NULL
           AND q.claimed_until > clock_timestamp()
           AND q.claim_work_key=p_work_key
           AND q.claim_owner_execution_id=p_owner_execution_id
           AND q.claim_fencing=p_lease_fencing
           AND q.evidence_digest=decode(o->>'EvidenceDigest','hex')
           AND q.sample_id IS NOT DISTINCT FROM NULLIF(o->>'SampleId','')
           AND q.run_id IS NOT DISTINCT FROM NULLIF(o->>'RunId','')::uuid
           AND q.rule_revision=(SELECT r.revision FROM alerting.rule r WHERE r.instance_id=q.instance_id AND r.rule_id=q.rule_id)
           AND q.observations = jsonb_build_array(o)
       )
       AND NOT EXISTS (
         SELECT 1 FROM alerting.evaluation_replay replay
         WHERE replay.operation_id=(o->>'OperationId')::uuid
           AND replay.instance_id=p_instance_id
           AND replay.rule_id=(o->>'RuleId')::uuid
           AND replay.target_binding=p_instance_id
           AND replay.evidence_digest=decode(o->>'EvidenceDigest','hex')
           AND ('{"clear_confirmation_count":1}'::jsonb || replay.rule_binding)=(SELECT to_jsonb(r) FROM alerting.rule r WHERE r.instance_id=p_instance_id AND r.rule_id=(o->>'RuleId')::uuid)
       )
  ) THEN RAISE EXCEPTION 'evaluation evidence is not an exact unexpired claim' USING ERRCODE='40001'; END IF;
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_observations) o
    WHERE CASE WHEN o->>'SourceKind'='metric_threshold' THEN NOT EXISTS (
      SELECT 1 FROM telemetry.raw_metric_sample m
      JOIN telemetry.collection_run_alert_read cr ON cr.run_id=m.collection_run_id
      JOIN telemetry.collection_run_outcome co ON co.run_id=cr.run_id
      WHERE m.instance_id=p_instance_id AND m.sample_id=(o->>'SampleId')::uuid
        AND m.collection_run_id=(o->>'RunId')::uuid AND m.metric_key=o->>'MetricId'
        AND m.observed_at=(o->>'ObservedAtUtc')::timestamptz
        AND m.metric_value=(o->>'Value')::double precision
        AND cr.collector_id=o->>'SourceCollector' AND cr.collector_version::text=o->>'SourceVersion'
        AND cr.output_schema_version=(o->>'SourceSchemaVersion')::integer
        AND encode(co.completion_digest,'hex')=o->>'SourceDigest'
        AND co.outcome IN ('succeeded','partial')
    ) WHEN o->>'SourceKind'='collector_health' THEN NOT EXISTS (
      SELECT 1 FROM reporting.collector_health_projection h
      JOIN telemetry.collection_run_alert_read cr ON cr.run_id=h.run_id
      JOIN telemetry.collection_run_outcome co ON co.run_id=cr.run_id
      WHERE h.instance_id=p_instance_id AND h.run_id=(o->>'RunId')::uuid
        AND h.collector_id=o->>'SourceCollector' AND h.collector_version::text=o->>'SourceVersion'
        AND h.output_schema_version=(o->>'SourceSchemaVersion')::integer
        AND encode(co.completion_digest,'hex')=o->>'SourceDigest'
        AND (o->>'ObservedAtUtc')::timestamptz = CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END
        AND (o->>'SampleId')::uuid = alerting.sha_uuid('collector-health|' || h.collector_id || '|' || h.collector_version::text || '|' || h.schedule_revision::text || '|' || h.schedule_target_revision::text || '|' || h.run_id::text || '|' || coalesce(h.completed_at::text,'') || '|' || coalesce(h.outcome,'') || '|' || h.health_state || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from (SELECT r.evaluation_interval FROM alerting.rule r WHERE r.instance_id=p_instance_id AND r.kind=2 AND r.enabled LIMIT 1)))::bigint)
        AND o->>'Reason' = 'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'') || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from (SELECT r.evaluation_interval FROM alerting.rule r WHERE r.instance_id=p_instance_id AND r.kind=2 AND r.enabled LIMIT 1)))::bigint
        AND (o->>'CollectorHealthy')::boolean = (h.health_state='current' AND co.outcome IN ('succeeded','partial'))
    ) ELSE true END
  ) THEN RAISE EXCEPTION 'evaluation evidence source binding is not immutable telemetry' USING ERRCODE='22023'; END IF;
END $$;
