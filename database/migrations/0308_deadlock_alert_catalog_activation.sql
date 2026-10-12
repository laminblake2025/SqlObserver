-- Activate deadlock count administration with the verified runtime backend.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

DO $catalog_guard$
BEGIN
  UPDATE alerting.catalog_registry SET contract_version=5,
    catalog_digest='b1e551855cd7731c59e024c8f35ae1375944664cd26b1d0f5b3f6dc3cfe1d1c7'
  WHERE catalog_id='sqlobserver.m8.alerts' AND contract_version=4
    AND catalog_digest='2acfcab767941fa6a40dff240314662456ff9908d4422eb83afe8ede865b8f0b';
  IF NOT FOUND THEN RAISE EXCEPTION 'deadlock alert catalog upgrade binding mismatch' USING ERRCODE='55000'; END IF;
END $catalog_guard$;

CREATE OR REPLACE FUNCTION alerting.upsert_rule(p_rule jsonb, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, alerting, audit SET row_security=on AS $$
DECLARE a uuid; t timestamptz; existing_digest bytea; updated integer; id uuid := (p_rule->>'RuleId')::uuid; target uuid := (p_rule->>'TargetId')::uuid;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF id IS NULL OR target IS NULL OR id='00000000-0000-0000-0000-000000000000'::uuid
     OR coalesce(p_rule->>'Action','') NOT IN ('CreateAlertRule','UpdateAlertRule','RetireAlertRule')
     OR jsonb_typeof(p_rule->'Enabled') IS DISTINCT FROM 'boolean'
  THEN RAISE EXCEPTION 'rule identity or action is invalid' USING ERRCODE='22023'; END IF;
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR target::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'rule target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(target::text,0));
  PERFORM 1 FROM control.observation_target target_row WHERE target_row.instance_id=target FOR UPDATE;
  IF EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id<>target) THEN RAISE EXCEPTION 'rule identity target mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_rule' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_rule::text,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_rule->>'Action'='CreateAlertRule' AND EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id=target) THEN RAISE EXCEPTION 'rule already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_rule->>'Action'<>'CreateAlertRule' AND NOT EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id=target) THEN RAISE EXCEPTION 'rule does not exist' USING ERRCODE='P0002'; END IF;
  IF p_rule->>'Action'='UpdateAlertRule' AND (p_rule->>'ExpectedRevision') IS NULL THEN RAISE EXCEPTION 'update requires expected revision' USING ERRCODE='22023'; END IF;
  IF jsonb_typeof(p_rule->'ClearConfirmationCount') IS DISTINCT FROM 'number' OR (p_rule->'ClearConfirmationCount')::text !~ '^(100|[1-9][0-9]?)$' THEN RAISE EXCEPTION 'clear confirmation count must be an integer from 1 to 100' USING ERRCODE='22023'; END IF;
  IF p_rule->>'CatalogDigest' IS DISTINCT FROM (SELECT c.catalog_digest FROM alerting.catalog_registry c WHERE c.catalog_id='sqlobserver.m8.alerts') THEN RAISE EXCEPTION 'alert catalog digest drift detected' USING ERRCODE='55000'; END IF;
  IF (
    p_rule->>'Name' ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'
    AND (
      (p_rule->>'SourceCollector'='engine.core' AND (p_rule->>'SourceSchemaVersion')::integer=1 AND (
        ((p_rule->>'Kind')::smallint=1 AND p_rule->>'MetricId'='engine.user_connections'
          AND (p_rule->>'Comparison')::smallint IN (1,2)
          AND (p_rule->>'Threshold')::double precision BETWEEN 0 AND 1000000
          AND (p_rule->>'Hysteresis')::double precision BETWEEN 0 AND (p_rule->>'Threshold')::double precision
          AND (p_rule->>'ConfirmationCount')::integer BETWEEN 1 AND 100
          AND (p_rule->>'ConfirmationWindow')::interval BETWEEN interval '0 seconds' AND interval '7 days'
          AND (p_rule->>'EvaluationInterval')::interval BETWEEN interval '15 seconds' AND interval '1 hour')
        OR (p_rule->>'Name'='collector.health' AND (p_rule->>'Kind')::smallint=2
          AND p_rule->'MetricId'='null'::jsonb AND (p_rule->>'Comparison')::smallint=3
          AND (p_rule->>'Threshold')::double precision=1 AND (p_rule->>'Hysteresis')::double precision=0
          AND (p_rule->>'ConfirmationCount')::integer=2
          AND (p_rule->>'ConfirmationWindow')::interval=interval '5 minutes'
          AND (p_rule->>'EvaluationInterval')::interval=interval '30 seconds')))
      OR (p_rule->>'SourceCollector'='backups.status' AND (p_rule->>'SourceSchemaVersion')::integer=2
        AND (p_rule->>'Kind')::smallint IN (3,4,5) AND p_rule->'MetricId'='null'::jsonb
        AND (p_rule->>'Comparison')::smallint=1 AND (p_rule->>'Threshold')::double precision=0
        AND (p_rule->>'Hysteresis')::double precision=0
        AND (p_rule->>'ConfirmationCount')::integer BETWEEN 1 AND 100
        AND (p_rule->>'ConfirmationWindow')::interval BETWEEN interval '0 seconds' AND interval '7 days'
        AND (p_rule->>'EvaluationInterval')::interval BETWEEN interval '15 seconds' AND interval '1 hour'
        AND (p_rule->>'ConfirmationWindow')::interval >= (p_rule->>'EvaluationInterval')::interval *
          (greatest((p_rule->>'ConfirmationCount')::integer,(p_rule->>'ClearConfirmationCount')::integer)-1))
      OR (p_rule->>'SourceCollector'='blocking.current' AND (p_rule->>'SourceSchemaVersion')::integer=1
        AND (p_rule->>'Kind')::smallint=6 AND p_rule->'MetricId'='null'::jsonb
        AND (p_rule->>'Comparison')::smallint IN (1,2)
        AND (p_rule->>'Threshold')::double precision BETWEEN 0 AND 86400
        AND (p_rule->>'Hysteresis')::double precision BETWEEN 0 AND (p_rule->>'Threshold')::double precision
        AND (p_rule->>'ConfirmationCount')::integer BETWEEN 1 AND 100
        AND (p_rule->>'ConfirmationWindow')::interval BETWEEN interval '0 seconds' AND interval '7 days'
        AND (p_rule->>'EvaluationInterval')::interval BETWEEN interval '5 seconds' AND interval '60 seconds'
        AND (p_rule->>'ConfirmationWindow')::interval >= (p_rule->>'EvaluationInterval')::interval *
          (greatest((p_rule->>'ConfirmationCount')::integer,(p_rule->>'ClearConfirmationCount')::integer)-1))
      OR (p_rule->>'SourceCollector'='deadlocks.system-health' AND (p_rule->>'SourceSchemaVersion')::integer=1
        AND (p_rule->>'Kind')::smallint=7 AND p_rule->'MetricId'='null'::jsonb
        AND (p_rule->>'Comparison')::smallint IN (1,2)
        AND (p_rule->>'Threshold')::double precision BETWEEN 0 AND 256
        AND (p_rule->>'Threshold')::double precision=trunc((p_rule->>'Threshold')::numeric)
        AND (p_rule->>'Hysteresis')::double precision=trunc((p_rule->>'Hysteresis')::numeric)
        AND (p_rule->>'Hysteresis')::double precision BETWEEN 0 AND (p_rule->>'Threshold')::double precision
        AND (p_rule->>'ConfirmationCount')::integer BETWEEN 1 AND 100
        AND (p_rule->>'ConfirmationWindow')::interval BETWEEN interval '0 seconds' AND interval '7 days'
        AND (p_rule->>'EvaluationInterval')::interval BETWEEN interval '15 seconds' AND interval '60 seconds'
        AND (p_rule->>'ConfirmationWindow')::interval >= (p_rule->>'EvaluationInterval')::interval *
          (greatest((p_rule->>'ConfirmationCount')::integer,(p_rule->>'ClearConfirmationCount')::integer)-1))
    )) IS NOT TRUE
  THEN RAISE EXCEPTION 'alert rule is outside the approved catalog contract' USING ERRCODE='22023'; END IF;
  IF EXISTS(SELECT 1 FROM alerting.rule existing WHERE existing.rule_id=id AND existing.instance_id=target
      AND (existing.kind IN (3,4,5,6,7) OR (p_rule->>'Kind')::smallint IN (3,4,5,6,7))
      AND (existing.kind IS DISTINCT FROM (p_rule->>'Kind')::smallint
        OR existing.metric_id IS DISTINCT FROM p_rule->>'MetricId'))
  THEN RAISE EXCEPTION 'source-bound rule signal cannot change; create a new rule' USING ERRCODE='22023'; END IF;
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

REVOKE ALL ON FUNCTION alerting.upsert_rule(jsonb,text,text,uuid)
  FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.upsert_rule(jsonb,text,text,uuid) TO sqlobserver_server;
