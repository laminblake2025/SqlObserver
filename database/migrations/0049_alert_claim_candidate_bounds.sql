-- Materialize each bounded claim before updating the same queue. Otherwise a
-- reevaluated candidate subquery can move past rows claimed by its own UPDATE.
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION alerting.claim_due_evaluations(p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(operation_id uuid, observations jsonb, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'evaluation target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation queue claim arguments are invalid' USING ERRCODE='22023'; END IF;
  UPDATE alerting.evaluation_queue q SET completed_at=clock_timestamp(),cancel_reason='rule_revision_stale',claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL
    WHERE q.instance_id=p_target_id AND q.completed_at IS NULL AND EXISTS (SELECT 1 FROM alerting.rule r WHERE r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.revision<>q.rule_revision);
  -- Reconciliation is coordinator-owned and already completed before target
  -- discovery; a target claim never performs an unscoped evidence scan.
  RETURN QUERY WITH oldest AS MATERIALIZED (SELECT x.operation_id,x.instance_id FROM alerting.evaluation_queue x JOIN alerting.rule xr ON xr.instance_id=x.instance_id AND xr.rule_id=x.rule_id WHERE x.completed_at IS NULL AND x.due_at<=clock_timestamp() AND (x.claimed_until IS NULL OR x.claimed_until<clock_timestamp() OR x.claim_work_key IS NULL) AND xr.enabled AND xr.revision=x.rule_revision ORDER BY x.observed_at,x.sample_id,x.run_id NULLS LAST,x.operation_id FOR UPDATE OF x SKIP LOCKED LIMIT p_max_results), candidates AS MATERIALIZED (SELECT oldest.operation_id FROM oldest WHERE instance_id=p_target_id), claimed AS (
  UPDATE alerting.evaluation_queue q
    SET claimed_until=clock_timestamp()+interval '30 seconds',claim_work_key=p_work_key,claim_owner_execution_id=p_owner_execution_id,claim_fencing=p_fencing,completed_at=NULL
    WHERE q.instance_id=p_target_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp() OR q.claim_work_key IS NULL)
    AND q.operation_id IN (SELECT candidates.operation_id FROM candidates)
    RETURNING q.operation_id,q.observations,q.due_at) SELECT claimed.operation_id,claimed.observations,claimed.due_at FROM claimed ORDER BY (claimed.observations->0->>'ObservedAtUtc')::timestamptz,claimed.operation_id;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

CREATE OR REPLACE FUNCTION alerting.claim_due_deliveries(p_instance_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(delivery_id uuid, alert_id uuid, destination_id uuid, target_id uuid, kind text, configuration_reference text, payload bytea, attempt integer, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'delivery claim arguments are invalid' USING ERRCODE='22023'; END IF;
  RETURN QUERY WITH candidates AS MATERIALIZED (SELECT x.delivery_id FROM alerting.delivery_outbox x JOIN alerting.rule xr ON xr.instance_id=x.instance_id AND xr.rule_id=x.rule_id AND xr.enabled JOIN alerting.destination xz ON xz.destination_id=x.destination_id AND xz.instance_id=x.instance_id AND xz.enabled AND xz.approved AND xz.approval_scope=x.destination_approval_scope AND xz.approved_kind=x.destination_kind AND xz.approved_configuration_reference=x.destination_configuration_reference AND xz.approved_revision=x.destination_approval_revision AND xz.approval_digest=x.destination_approval_digest AND xz.configuration_digest=x.destination_configuration_digest AND xz.revision=x.destination_revision WHERE x.instance_id=p_instance_id AND x.completed_at IS NULL AND x.cancelled_at IS NULL AND x.attempt BETWEEN 0 AND 7 AND x.due_at<=clock_timestamp() AND (x.leased_until IS NULL OR x.leased_until<clock_timestamp()) AND NOT alerting.maintenance_active_unscoped(x.instance_id,clock_timestamp()) ORDER BY x.due_at,x.delivery_id FOR UPDATE OF x SKIP LOCKED LIMIT p_max_results)
  UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes',lease_fencing=p_fencing,lease_work_key=p_work_key,lease_owner_execution_id=p_owner_execution_id
    WHERE d.instance_id=p_instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.attempt<=7 AND d.due_at<=clock_timestamp() AND (d.leased_until IS NULL OR d.leased_until<clock_timestamp())
      AND NOT alerting.maintenance_active_unscoped(d.instance_id,clock_timestamp())
      AND EXISTS (SELECT 1 FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=d.instance_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision)
    AND d.delivery_id IN (SELECT candidates.delivery_id FROM candidates)
    RETURNING d.delivery_id,d.alert_id,d.destination_id,d.instance_id,(SELECT z.kind FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_instance_id),(SELECT z.configuration_reference FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_instance_id),convert_to(d.payload::text,'UTF8'),d.attempt,d.due_at;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

