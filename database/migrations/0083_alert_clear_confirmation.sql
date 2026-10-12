-- Confirm recovery across distinct accepted observations, preserving old rule
-- settings and immutable replay documents. Deploy with the matching evaluator.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TimeZone = 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

DO $catalog_guard$
BEGIN
  UPDATE alerting.catalog_registry SET contract_version=2,catalog_digest='fc53c14e0ad3d15364c0c02846cd93d3e020f25dc378b653ffc4c77a393bc0dd'
  WHERE catalog_id='sqlobserver.m8.alerts' AND contract_version=1 AND catalog_digest='f28dab1e5bd65bf13f972a887b98742fe15eeea1dd7b13ac7ba0e8b6f0485d91';
  IF NOT FOUND THEN RAISE EXCEPTION 'alert catalog upgrade binding mismatch' USING ERRCODE='55000'; END IF;
END $catalog_guard$;

ALTER TABLE alerting.rule ADD COLUMN clear_confirmation_count integer NOT NULL DEFAULT 1
  CHECK (clear_confirmation_count BETWEEN 1 AND 100);
ALTER TABLE alerting.rule_state ADD COLUMN consecutive_clears integer NOT NULL DEFAULT 0
  CHECK (consecutive_clears BETWEEN 0 AND 100 AND (consecutive_clears=0 OR state IN (3,4)));


CREATE OR REPLACE FUNCTION alerting.list_rules_v2(p_target_id uuid)
RETURNS TABLE(rule_id uuid,name text,kind integer,metric_id text,comparison integer,threshold double precision,hysteresis double precision,confirmation_count integer,confirmation_window interval,evaluation_interval interval,enabled boolean,clear_confirmation_count integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
 SELECT rule_id,name,kind::integer,metric_id,comparison::integer,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled,clear_confirmation_count FROM alerting.rule WHERE instance_id=p_target_id AND enabled AND current_setting('sqlobserver.target_scope', true) IS NOT NULL AND p_target_id::text=current_setting('sqlobserver.target_scope', true) ORDER BY rule_id
$$;

CREATE OR REPLACE FUNCTION alerting.get_rule_state_v2(p_target_id uuid, p_rule_id uuid)
RETURNS TABLE(rule_id uuid, instance_id uuid, state integer, consecutive_matches integer, first_match_at timestamptz, last_observed_at timestamptz, fired_at timestamptz, acknowledged_at timestamptz, resolved_at timestamptz, episode_started_at timestamptz, alert_id uuid, alert_episode_id uuid, last_operation_id uuid, evidence_digest bytea, reason text, last_reason text, delivery_suppressed boolean, acknowledged_by text, revision bigint, last_value double precision,consecutive_clears integer)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
 SELECT s.rule_id,s.instance_id,s.state::integer,s.consecutive_matches,s.first_match_at,s.last_observed_at,s.fired_at,s.acknowledged_at,s.resolved_at,s.episode_started_at,s.alert_id,s.alert_episode_id,s.last_operation_id,s.evidence_digest,s.reason,s.last_reason,s.delivery_suppressed,s.acknowledged_by,s.revision,s.last_value,s.consecutive_clears FROM alerting.rule_state s WHERE s.instance_id=p_target_id AND s.rule_id=p_rule_id AND current_setting('sqlobserver.target_scope', true) IS NOT NULL AND p_target_id::text=current_setting('sqlobserver.target_scope', true)
$$;

CREATE OR REPLACE FUNCTION alerting.upsert_rule(p_rule jsonb, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid; t timestamptz; existing_digest bytea; updated integer; id uuid := (p_rule->>'RuleId')::uuid; target uuid := (p_rule->>'TargetId')::uuid;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR target::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'rule target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(target::text,0));
  IF EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id<>target) THEN RAISE EXCEPTION 'rule identity target mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_rule' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_rule::text,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_rule->>'Action'='CreateAlertRule' AND EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id=target) THEN RAISE EXCEPTION 'rule already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_rule->>'Action'<>'CreateAlertRule' AND NOT EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id=target) THEN RAISE EXCEPTION 'rule does not exist' USING ERRCODE='P0002'; END IF;
  IF p_rule->>'Action'='UpdateAlertRule' AND (p_rule->>'ExpectedRevision') IS NULL THEN RAISE EXCEPTION 'update requires expected revision' USING ERRCODE='22023'; END IF;
  IF jsonb_typeof(p_rule->'ClearConfirmationCount') IS DISTINCT FROM 'number' OR (p_rule->'ClearConfirmationCount')::text !~ '^(100|[1-9][0-9]?)$' THEN RAISE EXCEPTION 'clear confirmation count must be an integer from 1 to 100' USING ERRCODE='22023'; END IF;
  IF p_rule->>'CatalogDigest' IS DISTINCT FROM (SELECT c.catalog_digest FROM alerting.catalog_registry c WHERE c.catalog_id='sqlobserver.m8.alerts') THEN RAISE EXCEPTION 'alert catalog digest drift detected' USING ERRCODE='55000'; END IF;
  IF p_rule->>'SourceCollector' IS DISTINCT FROM 'engine.core' OR (p_rule->>'SourceSchemaVersion')::integer IS DISTINCT FROM 1 OR NOT ((p_rule->>'Name' ~ '^[A-Za-z0-9._-]{1,128}$' AND (p_rule->>'Kind')::smallint=1 AND p_rule->>'MetricId'='engine.user_connections' AND (p_rule->>'Comparison')::smallint IN (1,2) AND (p_rule->>'Threshold')::double precision BETWEEN 0 AND 1000000 AND (p_rule->>'Hysteresis')::double precision BETWEEN 0 AND (p_rule->>'Threshold')::double precision AND (p_rule->>'ConfirmationCount')::integer BETWEEN 1 AND 100 AND (p_rule->>'ConfirmationWindow')::interval BETWEEN interval '0 seconds' AND interval '7 days' AND (p_rule->>'EvaluationInterval')::interval BETWEEN interval '15 seconds' AND interval '1 hour') OR (p_rule->>'Name'='collector.health' AND (p_rule->>'Kind')::smallint=2 AND COALESCE(p_rule->>'MetricId','')='' AND (p_rule->>'Comparison')::smallint=3 AND (p_rule->>'Threshold')::double precision=1 AND (p_rule->>'Hysteresis')::double precision=0 AND (p_rule->>'ConfirmationCount')::integer=2 AND (p_rule->>'ConfirmationWindow')::interval=interval '5 minutes' AND (p_rule->>'EvaluationInterval')::interval=interval '30 seconds')) THEN RAISE EXCEPTION 'alert rule is outside the approved catalog contract' USING ERRCODE='22023'; END IF;
  IF (p_rule->>'ExpectedRevision') IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND revision<>(p_rule->>'ExpectedRevision')::bigint) THEN RAISE EXCEPTION 'rule revision conflict' USING ERRCODE='40001'; END IF;
  IF p_rule->>'Action'='CreateAlertRule' THEN
    INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled,clear_confirmation_count)
    VALUES(id,target,p_rule->>'Name',(p_rule->>'Kind')::smallint,NULLIF(p_rule->>'MetricId','')::text,(p_rule->>'Comparison')::smallint,(p_rule->>'Threshold')::double precision,(p_rule->>'Hysteresis')::double precision,(p_rule->>'ConfirmationCount')::integer,(p_rule->>'ConfirmationWindow')::interval,(p_rule->>'EvaluationInterval')::interval,COALESCE((p_rule->>'Enabled')::boolean,true),(p_rule->>'ClearConfirmationCount')::integer)
    ON CONFLICT(rule_id) DO NOTHING;
  ELSE
    INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled,clear_confirmation_count)
    VALUES(id,target,p_rule->>'Name',(p_rule->>'Kind')::smallint,NULLIF(p_rule->>'MetricId','')::text,(p_rule->>'Comparison')::smallint,(p_rule->>'Threshold')::double precision,(p_rule->>'Hysteresis')::double precision,(p_rule->>'ConfirmationCount')::integer,(p_rule->>'ConfirmationWindow')::interval,(p_rule->>'EvaluationInterval')::interval,COALESCE((p_rule->>'Enabled')::boolean,true),(p_rule->>'ClearConfirmationCount')::integer)
    ON CONFLICT(rule_id) DO UPDATE SET name=EXCLUDED.name,kind=EXCLUDED.kind,metric_id=EXCLUDED.metric_id,comparison=EXCLUDED.comparison,threshold=EXCLUDED.threshold,hysteresis=EXCLUDED.hysteresis,confirmation_count=EXCLUDED.confirmation_count,confirmation_window=EXCLUDED.confirmation_window,evaluation_interval=EXCLUDED.evaluation_interval,enabled=EXCLUDED.enabled,clear_confirmation_count=EXCLUDED.clear_confirmation_count,updated_at=clock_timestamp(),revision=alerting.rule.revision+1 WHERE alerting.rule.instance_id=target AND alerting.rule.revision=(p_rule->>'ExpectedRevision')::bigint;
  END IF;
  GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'rule revision conflict' USING ERRCODE='40001'; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  IF COALESCE((p_rule->>'Enabled')::boolean,true)=false THEN
    INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,delivery_suppressed,operation_id) SELECT instance_id,rule_id,alert_id,state,5,t,'rule_disabled',false,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid ELSE gen_random_uuid() END FROM alerting.rule_state WHERE instance_id=target AND rule_id=id AND state IN (3,4);
    UPDATE alerting.rule_state SET state=5,consecutive_clears=0,resolved_at=t,delivery_suppressed=false,reason='rule_disabled',last_reason='rule_disabled',revision=revision+1 WHERE instance_id=target AND rule_id=id AND state IN (3,4);
    UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason='rule_disabled',completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE instance_id=target AND rule_id=id AND completed_at IS NULL AND cancelled_at IS NULL;
    UPDATE alerting.evaluation_queue SET completed_at=t,claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=NULL WHERE instance_id=target AND rule_id=id AND completed_at IS NULL;
  ELSIF p_rule->>'Action'='UpdateAlertRule' THEN
    UPDATE alerting.rule_state SET consecutive_clears=0,revision=revision+1 WHERE instance_id=target AND rule_id=id AND consecutive_clears<>0;
    UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason='rule_revision_changed',completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE instance_id=target AND rule_id=id AND completed_at IS NULL AND cancelled_at IS NULL;
  END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_rule',p_idempotency_key,sha256(convert_to(p_rule::text,'UTF8')),target,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,NULLIF(p_rule->>'ExpectedRevision','')::bigint,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,CASE WHEN COALESCE((p_rule->>'Enabled')::boolean,true)=false THEN 'alert.rule.retire' WHEN (p_rule->>'ExpectedRevision') IS NULL THEN 'alert.rule.create' ELSE 'alert.rule.update' END,'allowed','succeeded','alert_rule',id::text,p_correlation_id,'{}');
  RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.canonical_decision_snapshot(p_decision jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog, public AS $$
  SELECT jsonb_build_object(
    'Observation', jsonb_build_object(
      'TargetId', NULLIF(p_decision->'Observation'->>'TargetId','')::uuid,
      'RuleId', NULLIF(p_decision->'Observation'->>'RuleId','')::uuid,
      'ObservedAtUtc', NULLIF(p_decision->'Observation'->>'ObservedAtUtc','')::timestamptz,
      'Value', NULLIF(p_decision->'Observation'->>'Value','')::double precision,
      'CollectorHealthy', NULLIF(p_decision->'Observation'->>'CollectorHealthy','')::boolean,
      'Reason', p_decision->'Observation'->>'Reason',
      'SampleId', p_decision->'Observation'->>'SampleId',
      'RunId', NULLIF(p_decision->'Observation'->>'RunId','')::uuid,
      'SourceKind', p_decision->'Observation'->>'SourceKind',
      'MetricId', p_decision->'Observation'->>'MetricId',
      'SourceCollector', p_decision->'Observation'->>'SourceCollector',
      'SourceVersion', p_decision->'Observation'->>'SourceVersion',
      'SourceSchemaVersion', NULLIF(p_decision->'Observation'->>'SourceSchemaVersion','')::integer,
      'SourceDigest', p_decision->'Observation'->>'SourceDigest',
      'OperationId', NULLIF(p_decision->'Observation'->>'OperationId','')::uuid,
      'EvidenceDigest', p_decision->'Observation'->>'EvidenceDigest'),
    'State', jsonb_build_object(
      'RuleId', NULLIF(p_decision->'State'->>'RuleId','')::uuid,
      'TargetId', NULLIF(p_decision->'State'->>'TargetId','')::uuid,
      'State', NULLIF(p_decision->'State'->>'State','')::smallint,
      'ConsecutiveClears', CASE WHEN p_decision->'State' ? 'ConsecutiveClears' THEN (p_decision->'State'->>'ConsecutiveClears')::integer ELSE 0 END,
      'ConsecutiveMatches', NULLIF(p_decision->'State'->>'ConsecutiveMatches','')::integer,
      'FirstMatchUtc', NULLIF(p_decision->'State'->>'FirstMatchUtc','')::timestamptz,
      'LastObservedUtc', NULLIF(p_decision->'State'->>'LastObservedUtc','')::timestamptz,
      'FiredUtc', NULLIF(p_decision->'State'->>'FiredUtc','')::timestamptz,
      'AcknowledgedUtc', NULLIF(p_decision->'State'->>'AcknowledgedUtc','')::timestamptz,
      'ResolvedUtc', NULLIF(p_decision->'State'->>'ResolvedUtc','')::timestamptz,
      'EpisodeStartedUtc', NULLIF(p_decision->'State'->>'EpisodeStartedUtc','')::timestamptz,
      'AlertId', NULLIF(p_decision->'State'->>'AlertId','')::uuid,
      'EpisodeId', NULLIF(p_decision->'State'->>'EpisodeId','')::uuid,
      'LastOperationId', NULLIF(p_decision->'State'->>'LastOperationId','')::uuid,
      'EvidenceDigest', p_decision->'State'->>'EvidenceDigest',
      'Reason', p_decision->'State'->>'Reason',
      'LastReason', p_decision->'State'->>'LastReason',
      'DeliverySuppressed', coalesce((p_decision->'State'->>'DeliverySuppressed')::boolean,false),
      'AcknowledgedBy', p_decision->'State'->>'AcknowledgedBy',
      'Revision', NULLIF(p_decision->'State'->>'Revision','')::bigint,
      'LastValue', NULLIF(p_decision->'State'->>'LastValue','')::double precision),
    'Event', NULLIF(p_decision->>'Event','')::smallint,
    'DeliverySuppressed', coalesce((p_decision->>'DeliverySuppressed')::boolean,false),
    'Reason', p_decision->>'Reason')
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
      JOIN telemetry.collection_run cr ON cr.run_id=m.collection_run_id
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
      JOIN telemetry.collection_run cr ON cr.run_id=h.run_id
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

CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue_internal(p_observations jsonb, p_decisions jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer, changed_count integer, suppressed_count integer, completed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE clears integer; expected_clears integer; prior_clears integer; partial_clear boolean; replay_result jsonb; legacy_replay boolean; candidate_result jsonb; v_decision jsonb; v_observation jsonb; target uuid; rule uuid; observed timestamptz; next_state smallint; expected_state smallint; prior_state smallint; prior_operation uuid; prior_digest bytea; history_digest bytea; replay_result_digest bytea; replay_rule_revision bigint; replay_target_binding uuid; replay_rule_binding jsonb; computed_evidence bytea; operation uuid; evidence bytea; v_event_kind smallint; suppressed boolean; matches boolean; v_metric double precision; consecutive integer; expected_consecutive integer; expected_first timestamptz; prior_last_observed timestamptz; prior_first_match timestamptz; changed integer := 0; hidden integer := 0; n integer := 0; evaluated_operations uuid[] := ARRAY[]::uuid[]; now_utc timestamptz := clock_timestamp(); existing_alert uuid; next_alert uuid; existing_revision bigint; canonical_result jsonb; canonical_result_digest bytea; rule_config alerting.rule%ROWTYPE;
BEGIN
  IF p_lease_fencing IS NULL OR p_lease_fencing <= 0 OR jsonb_array_length(p_decisions) > 10000 OR jsonb_array_length(p_decisions) <> jsonb_array_length(p_observations) THEN RAISE EXCEPTION 'invalid alert evaluation lease or one-to-one batch'; END IF;
  IF (SELECT count(*) FROM jsonb_array_elements(p_decisions) d WHERE d->'Observation'->>'OperationId' IS NULL) > 0 OR (SELECT count(DISTINCT d->'Observation'->>'OperationId') FROM jsonb_array_elements(p_decisions) d) <> jsonb_array_length(p_decisions) THEN RAISE EXCEPTION 'alert operation identities must be unique'; END IF;
  IF (SELECT count(DISTINCT o->>'OperationId') FROM jsonb_array_elements(p_observations) o) <> jsonb_array_length(p_observations) OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_decisions) d WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(p_observations) o WHERE o->>'OperationId'=d->'Observation'->>'OperationId' AND o->>'TargetId'=d->'Observation'->>'TargetId' AND o->>'RuleId'=d->'Observation'->>'RuleId' AND o->>'ObservedAtUtc'=d->'Observation'->>'ObservedAtUtc' AND coalesce(o->>'Value','')=coalesce(d->'Observation'->>'Value','') AND coalesce(o->>'CollectorHealthy','')=coalesce(d->'Observation'->>'CollectorHealthy','') AND coalesce(o->>'EvidenceDigest','')=coalesce(d->'Observation'->>'EvidenceDigest',''))) THEN RAISE EXCEPTION 'observation and decision bindings do not match'; END IF;
  FOR v_decision IN SELECT value FROM jsonb_array_elements(p_decisions) LOOP
    v_observation := v_decision->'Observation'; target := (v_observation->>'TargetId')::uuid; rule := (v_observation->>'RuleId')::uuid; observed := (v_observation->>'ObservedAtUtc')::timestamptz; next_state := (v_decision->'State'->>'State')::smallint; operation := (v_observation->>'OperationId')::uuid; evidence := decode(v_observation->>'EvidenceDigest','hex'); v_event_kind := NULLIF(v_decision->>'Event','')::smallint;
    IF jsonb_typeof(v_decision->'State') <> 'object' OR (SELECT count(*) FROM jsonb_object_keys((v_decision->'State')-'ConsecutiveClears')) <> 20 OR EXISTS (SELECT 1 FROM jsonb_object_keys(v_decision->'State') k WHERE k NOT IN ('RuleId','TargetId','State','ConsecutiveMatches','ConsecutiveClears','FirstMatchUtc','LastObservedUtc','FiredUtc','AcknowledgedUtc','ResolvedUtc','EpisodeStartedUtc','AlertId','EpisodeId','LastOperationId','EvidenceDigest','Reason','LastReason','DeliverySuppressed','AcknowledgedBy','Revision','LastValue')) THEN RAISE EXCEPTION 'alert decision state snapshot is malformed' USING ERRCODE='22023'; END IF;
    IF v_decision->'State' ? 'ConsecutiveClears' AND (jsonb_typeof(v_decision->'State'->'ConsecutiveClears') IS DISTINCT FROM 'number' OR (v_decision->'State'->'ConsecutiveClears')::text !~ '^(0|[1-9][0-9]?|100)$') THEN RAISE EXCEPTION 'alert clear progress is malformed' USING ERRCODE='22023'; END IF;
    IF current_setting('sqlobserver.target_scope', true) IS NULL OR target::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'alert target scope mismatch' USING ERRCODE='42501'; END IF;
    IF operation IS NULL OR octet_length(evidence) <> 32 THEN RAISE EXCEPTION 'alert operation evidence is required' USING ERRCODE='22023'; END IF;
    IF operation IS DISTINCT FROM alerting.canonical_operation_id(target,rule,observed,v_observation->>'Reason') THEN RAISE EXCEPTION 'non-canonical alert operation identity' USING ERRCODE='22023'; END IF;
    computed_evidence := alerting.canonical_evidence_sha256(target,rule,v_observation->>'SourceKind',v_observation->>'MetricId',v_observation->>'SourceCollector',v_observation->>'SourceVersion',NULLIF(v_observation->>'SourceSchemaVersion','')::integer,v_observation->>'SourceDigest',observed,v_observation->>'SampleId',NULLIF(v_observation->>'RunId','')::uuid,NULLIF(v_observation->>'Value','')::double precision,NULLIF(v_observation->>'CollectorHealthy','')::boolean,v_observation->>'Reason');
    IF evidence IS DISTINCT FROM computed_evidence THEN RAISE EXCEPTION 'non-canonical alert evidence digest' USING ERRCODE='22023'; END IF;
    -- Check the independent replay ledger before touching rule state. Exact
    -- operation+digest replays return without mutation; divergent evidence is
    -- a conflict even when the current state has moved on.
    SELECT r.evidence_digest,r.result_digest,r.rule_revision,r.target_binding,r.rule_binding,r.result INTO history_digest,replay_result_digest,replay_rule_revision,replay_target_binding,replay_rule_binding,replay_result FROM alerting.evaluation_replay r WHERE r.operation_id=operation AND r.instance_id=target AND r.rule_id=rule;
    IF FOUND THEN
      -- Only a stored legacy binding AND legacy result select compatibility.
      -- Missing legacy fields mean precisely count=1/progress=0; never erase
      -- nonzero caller progress or rewrite a historical result/hash.
      legacy_replay := NOT (replay_rule_binding ? 'clear_confirmation_count') AND NOT (replay_result->'State' ? 'ConsecutiveClears');
      candidate_result := alerting.canonical_decision_snapshot(v_decision);
      IF legacy_replay THEN
        IF (candidate_result->'State'->>'ConsecutiveClears')::integer IS DISTINCT FROM 0 THEN RAISE EXCEPTION 'divergent alert replay' USING ERRCODE='40001'; END IF;
        candidate_result := candidate_result #- '{State,ConsecutiveClears}';
      ELSIF NOT (v_decision->'State' ? 'ConsecutiveClears') THEN
        RAISE EXCEPTION 'alert clear progress is required' USING ERRCODE='22023';
      END IF;
      IF history_digest IS DISTINCT FROM evidence OR replay_result_digest IS DISTINCT FROM sha256(convert_to(replay_result::text,'UTF8')) OR candidate_result IS DISTINCT FROM replay_result OR replay_target_binding IS DISTINCT FROM target OR replay_rule_revision IS DISTINCT FROM (SELECT r.revision FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule) OR ('{"clear_confirmation_count":1}'::jsonb || replay_rule_binding) IS DISTINCT FROM (SELECT to_jsonb(r) FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule) THEN RAISE EXCEPTION 'divergent alert replay' USING ERRCODE='40001'; END IF;
      CONTINUE;
    END IF;
    IF NOT (v_decision->'State' ? 'ConsecutiveClears') THEN RAISE EXCEPTION 'alert clear progress is required' USING ERRCODE='22023'; END IF;
    SELECT h.evidence_digest,h.result_digest INTO history_digest,replay_result_digest FROM alerting.state_history h WHERE h.instance_id=target AND h.rule_id=rule AND h.operation_id=operation LIMIT 1;
    IF FOUND THEN
      candidate_result := alerting.canonical_decision_snapshot(v_decision);
      IF replay_result_digest IS DISTINCT FROM sha256(convert_to(candidate_result::text,'UTF8')) AND (
        (candidate_result->'State'->>'ConsecutiveClears')::integer=0
        AND (SELECT r.clear_confirmation_count FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule)=1
        AND replay_result_digest=sha256(convert_to((candidate_result #- '{State,ConsecutiveClears}')::text,'UTF8'))
      ) IS NOT TRUE THEN RAISE EXCEPTION 'divergent alert history payload' USING ERRCODE='40001'; END IF;
      IF history_digest IS DISTINCT FROM evidence OR EXISTS (SELECT 1 FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule AND (s.last_operation_id IS DISTINCT FROM operation OR s.state IS DISTINCT FROM next_state OR s.consecutive_clears IS DISTINCT FROM (v_decision->'State'->>'ConsecutiveClears')::integer OR s.consecutive_matches IS DISTINCT FROM (v_decision->'State'->>'ConsecutiveMatches')::integer OR s.first_match_at IS DISTINCT FROM NULLIF(v_decision->'State'->>'FirstMatchUtc','')::timestamptz OR s.last_observed_at IS DISTINCT FROM NULLIF(v_decision->'State'->>'LastObservedUtc','')::timestamptz OR s.alert_id IS DISTINCT FROM NULLIF(v_decision->'State'->>'AlertId','')::uuid OR s.alert_episode_id IS DISTINCT FROM NULLIF(v_decision->'State'->>'EpisodeId','')::uuid OR s.resolved_at IS DISTINCT FROM NULLIF(v_decision->'State'->>'ResolvedUtc','')::timestamptz OR s.episode_started_at IS DISTINCT FROM NULLIF(v_decision->'State'->>'EpisodeStartedUtc','')::timestamptz OR s.acknowledged_by IS DISTINCT FROM v_decision->'State'->>'AcknowledgedBy' OR s.last_value IS DISTINCT FROM NULLIF(v_decision->'State'->>'LastValue','')::double precision OR s.reason IS DISTINCT FROM v_decision->'State'->>'Reason' OR s.last_reason IS DISTINCT FROM v_decision->'State'->>'LastReason' OR s.delivery_suppressed IS DISTINCT FROM (v_decision->'State'->>'DeliverySuppressed')::boolean OR s.revision IS DISTINCT FROM (v_decision->'State'->>'Revision')::bigint)) THEN RAISE EXCEPTION 'divergent alert history replay' USING ERRCODE='40001'; END IF;
      CONTINUE;
    END IF;
    SELECT r.* INTO rule_config FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule AND r.enabled FOR SHARE;
    IF NOT FOUND OR (rule_config.kind=1 AND v_observation->>'Value' IS NULL) OR (rule_config.kind=2 AND v_observation->>'CollectorHealthy' IS NULL) THEN RAISE EXCEPTION 'alert observation rule binding is invalid' USING ERRCODE='22023'; END IF;
    IF rule_config.threshold IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision) OR rule_config.hysteresis IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision) OR rule_config.hysteresis < 0 OR rule_config.confirmation_count < 1 THEN RAISE EXCEPTION 'alert rule configuration is invalid' USING ERRCODE='22023'; END IF;
    SELECT s.state,s.last_operation_id,s.evidence_digest,s.alert_id,s.revision,s.last_observed_at,s.first_match_at,s.consecutive_clears INTO prior_state,prior_operation,prior_digest,existing_alert,existing_revision,prior_last_observed,prior_first_match,prior_clears FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule FOR UPDATE;
    clears := (v_decision->'State'->>'ConsecutiveClears')::integer;
    consecutive := COALESCE((v_decision->'State'->>'ConsecutiveMatches')::integer,0);
    IF next_state NOT BETWEEN 1 AND 5 OR consecutive < 0 OR consecutive > rule_config.confirmation_count THEN RAISE EXCEPTION 'alert decision bounds are invalid' USING ERRCODE='22023'; END IF;
    v_metric := CASE WHEN rule_config.kind=2 THEN CASE WHEN lower(v_observation->>'CollectorHealthy')='true' THEN 1 ELSE 0 END ELSE NULLIF(v_observation->>'Value','')::double precision END;
    IF prior_last_observed IS NOT NULL AND observed < prior_last_observed THEN RAISE EXCEPTION 'alert observation time regressed' USING ERRCODE='22023'; END IF;
    IF rule_config.comparison=1 THEN matches := v_metric > (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold-rule_config.hysteresis ELSE rule_config.threshold END);
    ELSIF rule_config.comparison=2 THEN matches := v_metric >= (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold-rule_config.hysteresis ELSE rule_config.threshold END);
    ELSIF rule_config.comparison=3 THEN matches := v_metric < (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold+rule_config.hysteresis ELSE rule_config.threshold END);
    ELSIF rule_config.comparison=4 THEN matches := v_metric <= (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold+rule_config.hysteresis ELSE rule_config.threshold END);
    ELSE matches := abs(v_metric-rule_config.threshold) <= rule_config.hysteresis;
    END IF;
    expected_clears := CASE WHEN prior_state IN (3,4) AND NOT matches THEN LEAST(rule_config.clear_confirmation_count,coalesce(prior_clears,0)+1) ELSE 0 END;
    partial_clear := expected_clears>0 AND expected_clears<rule_config.clear_confirmation_count;
    expected_consecutive := CASE WHEN matches THEN LEAST(rule_config.confirmation_count,COALESCE((SELECT s.consecutive_matches FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule),0)+1) WHEN partial_clear THEN LEAST(rule_config.confirmation_count,(SELECT s.consecutive_matches FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule)) ELSE 0 END;
    expected_first := CASE WHEN matches THEN COALESCE(prior_first_match,observed) WHEN partial_clear THEN prior_first_match ELSE NULL END;
    IF matches AND coalesce(prior_state,1) NOT IN (3,4) AND expected_first IS NOT NULL AND observed-expected_first > rule_config.confirmation_window THEN expected_consecutive := 1; expected_first := observed; END IF;
    expected_state := CASE WHEN NOT matches THEN CASE WHEN partial_clear THEN prior_state WHEN prior_state IN (3,4) THEN 5 ELSE 1 END WHEN COALESCE(prior_state,1) IN (1,5) THEN CASE WHEN expected_consecutive >= rule_config.confirmation_count THEN 3 ELSE 2 END WHEN prior_state=2 THEN CASE WHEN expected_consecutive >= rule_config.confirmation_count THEN 3 ELSE 2 END ELSE COALESCE(prior_state,1) END;
    IF NOT partial_clear THEN expected_clears := 0; END IF;
    IF clears IS DISTINCT FROM expected_clears OR next_state IS DISTINCT FROM expected_state OR consecutive IS DISTINCT FROM expected_consecutive OR NULLIF(v_decision->'State'->>'FirstMatchUtc','')::timestamptz IS DISTINCT FROM expected_first THEN RAISE EXCEPTION 'alert decision state does not match repository evaluation' USING ERRCODE='22023'; END IF;
    IF v_event_kind IS DISTINCT FROM (CASE WHEN expected_state=3 AND COALESCE(prior_state,1) NOT IN (3,4) THEN 1 WHEN expected_state=5 AND prior_state IN (3,4) THEN 2 WHEN expected_state=4 AND prior_state=3 THEN 3 ELSE NULL END) THEN RAISE EXCEPTION 'alert event kind does not match legal transition' USING ERRCODE='22023'; END IF;
    IF next_state IN (3,4) AND NOT matches AND NOT partial_clear THEN RAISE EXCEPTION 'alert decision does not satisfy rule threshold' USING ERRCODE='22023'; END IF;
    IF next_state=3 AND coalesce(prior_state,1) NOT IN (3,4) AND consecutive < rule_config.confirmation_count THEN RAISE EXCEPTION 'alert firing confirmation is incomplete' USING ERRCODE='22023'; END IF;
    IF next_state=4 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'acknowledgement transition is illegal' USING ERRCODE='22023'; END IF;
    IF next_state=5 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'resolution transition is illegal' USING ERRCODE='22023'; END IF;
    IF next_state=1 AND COALESCE(prior_state,1) IN (3,4) THEN RAISE EXCEPTION 'illegal alert transition' USING ERRCODE='22023'; END IF;
    IF next_state=2 AND COALESCE(prior_state,1) NOT IN (1,2,5) THEN RAISE EXCEPTION 'illegal pending transition' USING ERRCODE='22023'; END IF;
    IF next_state=3 AND COALESCE(prior_state,1) NOT IN (1,2,3,5) THEN RAISE EXCEPTION 'illegal firing transition' USING ERRCODE='22023'; END IF;
    IF next_state=4 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'illegal acknowledgement transition' USING ERRCODE='22023'; END IF;
    IF next_state=5 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'illegal resolution transition' USING ERRCODE='22023'; END IF;
    IF prior_state IS NOT NULL AND existing_revision IS NULL THEN RAISE EXCEPTION 'alert state revision is required' USING ERRCODE='22023'; END IF;
    IF prior_operation = operation THEN
      IF prior_digest IS DISTINCT FROM evidence THEN RAISE EXCEPTION 'divergent alert replay' USING ERRCODE='40001'; END IF;
      CONTINUE;
    END IF;
    suppressed := alerting.maintenance_active_unscoped(target,observed);
    next_alert := CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state=3 AND prior_state=5 THEN alerting.sha_uuid('alert|' || target::text || '|' || rule::text || '|' || operation::text) WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) AND existing_alert IS NULL THEN alerting.sha_uuid('alert|' || target::text || '|' || rule::text || '|' || operation::text) ELSE existing_alert END;
    IF COALESCE(v_decision->'State'->>'AlertId','') IS DISTINCT FROM COALESCE(next_alert::text,'') OR COALESCE(v_decision->'State'->>'EpisodeId','') IS DISTINCT FROM COALESCE((CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) THEN operation ELSE (SELECT s.alert_episode_id FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END)::text,'') OR COALESCE(v_decision->'State'->>'LastOperationId','') IS DISTINCT FROM operation::text OR COALESCE(lower(v_decision->'State'->>'EvidenceDigest'),'') IS DISTINCT FROM encode(evidence,'hex') OR COALESCE((v_decision->'State'->>'DeliverySuppressed')::boolean,false) IS DISTINCT FROM suppressed OR (v_decision->'State'->>'State')::smallint IS DISTINCT FROM next_state OR (v_decision->'State'->>'ConsecutiveMatches')::integer IS DISTINCT FROM expected_consecutive OR NULLIF(v_decision->'State'->>'LastValue','')::double precision IS DISTINCT FROM NULLIF(v_observation->>'Value','')::double precision OR v_decision->'State'->>'Reason' IS DISTINCT FROM v_observation->>'Reason' OR v_decision->'State'->>'LastReason' IS DISTINCT FROM v_observation->>'Reason' THEN RAISE EXCEPTION 'alert decision state identity does not match repository evaluation' USING ERRCODE='22023'; END IF;
    INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,state,consecutive_matches,first_match_at,last_observed_at,fired_at,acknowledged_at,resolved_at,episode_started_at,acknowledged_by,last_value,reason,last_reason,delivery_suppressed,alert_episode_id,last_operation_id,evidence_digest,revision,consecutive_clears)
    VALUES(target,rule,next_alert,next_state,expected_consecutive,expected_first,observed,CASE WHEN prior_state=5 THEN CASE WHEN next_state=3 THEN observed ELSE NULL END WHEN next_state=3 THEN CASE WHEN prior_state=3 THEN (SELECT s.fired_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE observed END ELSE CASE WHEN prior_state IN (3,4) THEN (SELECT s.fired_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE NULL END END,CASE WHEN prior_state=5 THEN NULL WHEN next_state=4 THEN CASE WHEN prior_state=4 THEN (SELECT s.acknowledged_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE observed END ELSE CASE WHEN prior_state=4 THEN (SELECT s.acknowledged_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE NULL END END,CASE WHEN prior_state=5 THEN NULL WHEN next_state=5 THEN observed ELSE (SELECT s.resolved_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END,CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state=3 AND prior_state=5 THEN observed WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) THEN observed ELSE (SELECT s.episode_started_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END,CASE WHEN prior_state=5 THEN NULL WHEN next_state=4 OR prior_state=4 THEN (SELECT s.acknowledged_by FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE NULL END,(v_observation->>'Value')::double precision,v_observation->>'Reason',v_decision->'State'->>'LastReason',suppressed,CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state=3 AND prior_state=5 THEN operation WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) THEN operation ELSE (SELECT s.alert_episode_id FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END,operation,evidence,coalesce(existing_revision,1)+1,expected_clears)
    ON CONFLICT(instance_id,rule_id) DO UPDATE SET alert_id=EXCLUDED.alert_id,state=EXCLUDED.state,consecutive_matches=EXCLUDED.consecutive_matches,consecutive_clears=EXCLUDED.consecutive_clears,first_match_at=EXCLUDED.first_match_at,last_observed_at=EXCLUDED.last_observed_at,fired_at=EXCLUDED.fired_at,acknowledged_at=EXCLUDED.acknowledged_at,resolved_at=EXCLUDED.resolved_at,episode_started_at=EXCLUDED.episode_started_at,acknowledged_by=EXCLUDED.acknowledged_by,last_value=EXCLUDED.last_value,reason=EXCLUDED.reason,last_reason=EXCLUDED.last_reason,delivery_suppressed=EXCLUDED.delivery_suppressed,alert_episode_id=EXCLUDED.alert_episode_id,last_operation_id=EXCLUDED.last_operation_id,evidence_digest=EXCLUDED.evidence_digest,revision=alerting.rule_state.revision+1;
    -- Reconstruct the complete result from the validated SQL row.  The
    -- caller's JSON is only an input assertion; it is never persisted.
    SELECT jsonb_build_object(
      'Observation', jsonb_build_object(
        'TargetId', target, 'RuleId', rule, 'ObservedAtUtc', observed,
        'Value', NULLIF(v_observation->>'Value','')::double precision,
        'CollectorHealthy', NULLIF(v_observation->>'CollectorHealthy','')::boolean,
        'Reason', v_observation->>'Reason', 'SampleId', v_observation->>'SampleId',
        'RunId', NULLIF(v_observation->>'RunId','')::uuid,
        'SourceKind', v_observation->>'SourceKind', 'MetricId', v_observation->>'MetricId',
        'SourceCollector', v_observation->>'SourceCollector', 'SourceVersion', v_observation->>'SourceVersion',
        'SourceSchemaVersion', NULLIF(v_observation->>'SourceSchemaVersion','')::integer,
        'SourceDigest', v_observation->>'SourceDigest', 'OperationId', operation,
        'EvidenceDigest', encode(evidence,'hex')),
      'State', jsonb_build_object(
        'RuleId', s.rule_id, 'TargetId', s.instance_id, 'State', s.state,
        'ConsecutiveMatches', s.consecutive_matches, 'ConsecutiveClears', s.consecutive_clears, 'FirstMatchUtc', s.first_match_at,
        'LastObservedUtc', s.last_observed_at, 'FiredUtc', s.fired_at,
        'AcknowledgedUtc', s.acknowledged_at, 'ResolvedUtc', s.resolved_at,
        'EpisodeStartedUtc', s.episode_started_at, 'AlertId', s.alert_id,
        'EpisodeId', s.alert_episode_id, 'LastOperationId', s.last_operation_id,
        'EvidenceDigest', encode(s.evidence_digest,'hex'), 'Reason', s.reason,
        'LastReason', s.last_reason, 'DeliverySuppressed', s.delivery_suppressed,
        'AcknowledgedBy', s.acknowledged_by, 'Revision', s.revision, 'LastValue', s.last_value),
      'Event', v_event_kind, 'DeliverySuppressed', s.delivery_suppressed, 'Reason', CASE WHEN s.delivery_suppressed THEN 'maintenance' ELSE 'evaluated' END)
      INTO canonical_result
      FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule;
    canonical_result_digest := sha256(convert_to(canonical_result::text,'UTF8'));
    IF alerting.canonical_decision_snapshot(v_decision) IS DISTINCT FROM canonical_result THEN
      RAISE EXCEPTION 'alert decision snapshot does not match server state' USING ERRCODE='22023';
    END IF;
    IF EXISTS (SELECT 1 FROM alerting.state_history h WHERE h.instance_id=target AND h.rule_id=rule AND h.operation_id=operation AND (h.evidence_digest IS DISTINCT FROM evidence OR h.result_digest IS DISTINCT FROM canonical_result_digest)) THEN RAISE EXCEPTION 'divergent alert history payload' USING ERRCODE='40001'; END IF;
    INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,delivery_suppressed,operation_id,evidence_digest,result_digest) SELECT target,rule,s.alert_id,prior_state,next_state,observed,s.reason,s.delivery_suppressed,operation,evidence,canonical_result_digest FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule;
    INSERT INTO alerting.evaluation_replay(operation_id,instance_id,rule_id,rule_revision,target_binding,rule_binding,evidence_digest,result_digest,result) VALUES(operation,target,rule,rule_config.revision,target,to_jsonb(rule_config),evidence,canonical_result_digest,canonical_result) ON CONFLICT(operation_id) DO NOTHING;
    n := n + 1; evaluated_operations := array_append(evaluated_operations, operation); IF prior_state IS DISTINCT FROM next_state THEN changed := changed + 1; END IF; IF suppressed THEN hidden := hidden + 1; END IF;
    IF v_event_kind IS NOT NULL THEN INSERT INTO alerting.delivery_outbox(delivery_id,alert_id,instance_id,rule_id,destination_id,event_kind,payload,due_at,operation_id,evidence_digest,destination_revision,destination_kind,destination_configuration_reference,destination_approval_revision,destination_approval_digest,destination_approval_scope,destination_configuration_digest) SELECT gen_random_uuid(),s.alert_id,target,rule,x.destination_id,v_event_kind,jsonb_build_object('schemaVersion',1,'operationId',operation,'evidenceDigest',encode(evidence,'hex'),'alertId',s.alert_id,'ruleId',rule,'targetId',target,'event',v_event_kind,'reasonCode',v_decision->>'Reason','suppressed',suppressed),CASE WHEN suppressed THEN COALESCE((SELECT max(m.ends_at) FROM alerting.maintenance_window m WHERE m.instance_id=target AND m.cancelled_at IS NULL AND observed>=m.starts_at AND observed<m.ends_at),now_utc) ELSE now_utc END,operation,evidence,x.revision,x.kind,x.configuration_reference,x.approved_revision,x.approval_digest,x.approval_scope,x.configuration_digest FROM alerting.rule_state s JOIN alerting.destination x ON x.instance_id=target AND x.enabled AND x.approved AND x.approval_scope=target AND x.approved_kind=x.kind AND x.approved_configuration_reference=x.configuration_reference AND x.approved_revision IS NOT NULL AND x.approval_digest IS NOT NULL AND x.instance_id=target WHERE s.instance_id=target AND s.rule_id=rule ON CONFLICT(alert_id,destination_id,event_kind) DO NOTHING; END IF;
  END LOOP;
  UPDATE alerting.evaluation_queue q SET completed_at=now_utc,claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=now_utc+r.evaluation_interval,due_at=now_utc+r.evaluation_interval
  FROM alerting.rule r
  WHERE r.rule_id=q.rule_id AND r.instance_id=q.instance_id
    AND q.completed_at IS NULL AND q.claimed_until>now_utc
    AND q.claim_work_key=p_work_key AND q.claim_owner_execution_id=p_owner_execution_id AND q.claim_fencing=p_lease_fencing
    AND q.operation_id=ANY(evaluated_operations)
    AND EXISTS (SELECT 1 FROM jsonb_array_elements(p_decisions) decision WHERE (decision->'Observation'->>'OperationId')::uuid=q.operation_id AND (decision->'Observation'->>'TargetId')::uuid=q.instance_id AND (decision->'Observation'->>'RuleId')::uuid=q.rule_id AND (decision->'Observation'->>'ObservedAtUtc')::timestamptz=q.observed_at AND decode(decision->'Observation'->>'EvidenceDigest','hex')=q.evidence_digest);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details) VALUES(now_utc,gen_random_uuid(),'service','collector','alerting.evaluate','allowed','succeeded','alert_evaluation',p_lease_fencing::text,gen_random_uuid(),jsonb_build_object('evaluated_count',n,'changed_count',changed));
  RETURN QUERY SELECT n,changed,hidden,now_utc;
END $$;

-- The old read signatures remain for compatibility; only the collector may
-- use either read shape. Internal mutation/canonical functions keep their ACLs.
REVOKE ALL ON FUNCTION alerting.list_rules_v2(uuid),alerting.get_rule_state_v2(uuid,uuid) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.list_rules_v2(uuid),alerting.get_rule_state_v2(uuid,uuid) TO sqlobserver_collector;
