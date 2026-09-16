-- A first-use idempotency lookup clears SELECT INTO variables when no row exists.
-- Reinitialize audit identity/time only after the replay-return branch.
-- Function signatures, authorization checks, locks and grants are unchanged.
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION alerting.upsert_maintenance(p_window jsonb, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; updated integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR (p_window->>'TargetId') <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE='42501'; END IF;
  -- Serialize maintenance transitions with the collector's session-scoped
  -- dispatch permit for this target. The permit remains held across the
  -- adapter side effect; this transaction therefore cannot start a window
  -- between the final readiness check and the send.
  PERFORM pg_advisory_xact_lock(hashtextextended(p_window->>'TargetId',0));
  IF EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id<>(p_window->>'TargetId')::uuid) THEN RAISE EXCEPTION 'maintenance identity target mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_maintenance' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_window::text,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  IF p_window->>'Action'='CreateMaintenanceWindow' AND EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id=(p_window->>'TargetId')::uuid) THEN RAISE EXCEPTION 'maintenance window already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_window->>'Action'<>'CreateMaintenanceWindow' AND NOT EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id=(p_window->>'TargetId')::uuid) THEN RAISE EXCEPTION 'maintenance window does not exist' USING ERRCODE='P0002'; END IF;
  IF p_window->>'Action'<>'CreateMaintenanceWindow' AND EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id=(p_window->>'TargetId')::uuid AND cancelled_at IS NOT NULL) THEN RAISE EXCEPTION 'cancelled maintenance window cannot be updated or reactivated' USING ERRCODE='40001'; END IF;
  IF p_window->>'Action'='UpdateMaintenanceWindow' AND (p_window->>'ExpectedRevision') IS NULL THEN RAISE EXCEPTION 'update requires expected revision' USING ERRCODE='22023'; END IF;
  IF (p_window->>'ExpectedRevision') IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND revision<>(p_window->>'ExpectedRevision')::bigint) THEN RAISE EXCEPTION 'maintenance revision conflict' USING ERRCODE='40001'; END IF;
  IF p_window->>'Action'='CreateMaintenanceWindow' THEN
    INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason) VALUES((p_window->>'Id')::uuid,(p_window->>'TargetId')::uuid,(p_window->>'StartsAtUtc')::timestamptz,(p_window->>'EndsAtUtc')::timestamptz,p_window->>'Reason') ON CONFLICT(window_id) DO NOTHING;
  ELSE
    INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason) VALUES((p_window->>'Id')::uuid,(p_window->>'TargetId')::uuid,(p_window->>'StartsAtUtc')::timestamptz,(p_window->>'EndsAtUtc')::timestamptz,p_window->>'Reason') ON CONFLICT(window_id) DO UPDATE SET starts_at=EXCLUDED.starts_at,ends_at=EXCLUDED.ends_at,reason=EXCLUDED.reason,revision=alerting.maintenance_window.revision+1 WHERE alerting.maintenance_window.instance_id=(p_window->>'TargetId')::uuid AND alerting.maintenance_window.revision=(p_window->>'ExpectedRevision')::bigint;
  END IF;
  GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'maintenance revision conflict' USING ERRCODE='40001'; END IF;
  IF (p_window->>'StartsAtUtc')::timestamptz <= t AND (p_window->>'EndsAtUtc')::timestamptz > t THEN
    INSERT INTO alerting.delivery_suppression_intent(delivery_id,instance_id,operation_id,reason)
      SELECT d.delivery_id,d.instance_id,d.operation_id,'maintenance_started:' || (p_window->>'Id')
      FROM alerting.delivery_outbox d
      WHERE d.instance_id=(p_window->>'TargetId')::uuid AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.due_at < (p_window->>'EndsAtUtc')::timestamptz;
    UPDATE alerting.delivery_outbox SET leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL,due_at=GREATEST(due_at,(p_window->>'EndsAtUtc')::timestamptz)
      WHERE instance_id=(p_window->>'TargetId')::uuid AND completed_at IS NULL AND cancelled_at IS NULL;
  END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_maintenance',p_idempotency_key,sha256(convert_to(p_window::text,'UTF8')),(p_window->>'TargetId')::uuid,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,NULLIF(p_window->>'ExpectedRevision','')::bigint,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,CASE WHEN (p_window->>'ExpectedRevision') IS NULL THEN 'alert.maintenance.create' ELSE 'alert.maintenance.update' END,'allowed','succeeded',p_correlation_id,'{}'); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.acknowledge(p_alert_id uuid, p_target_id uuid, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_expected_episode_id uuid, p_request_digest text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; changed integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'acknowledgement target scope mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='acknowledge' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_alert_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_expected_episode_id::text,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'acknowledgement revision is invalid' USING ERRCODE='22023'; END IF;
  IF NOT EXISTS (SELECT 1 FROM alerting.rule_state WHERE alert_id=p_alert_id AND instance_id=p_target_id) THEN RAISE EXCEPTION 'alert target mismatch'; END IF;
  IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.rule_state s WHERE s.alert_id=p_alert_id AND s.instance_id=p_target_id AND s.revision<>p_expected_revision) THEN RAISE EXCEPTION 'acknowledgement revision conflict' USING ERRCODE='40001'; END IF;
  IF p_expected_episode_id IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.rule_state s WHERE s.alert_id=p_alert_id AND s.instance_id=p_target_id AND s.alert_episode_id IS DISTINCT FROM p_expected_episode_id) THEN RAISE EXCEPTION 'acknowledgement episode conflict' USING ERRCODE='40001'; END IF;
  UPDATE alerting.rule_state SET state=4,acknowledged_at=t,acknowledged_by=p_actor_sid,revision=revision+1 WHERE alert_id=p_alert_id AND instance_id=p_target_id AND state=3 AND (p_expected_revision IS NULL OR revision=p_expected_revision) AND (p_expected_episode_id IS NULL OR alert_episode_id=p_expected_episode_id);
  GET DIAGNOSTICS changed=ROW_COUNT; IF changed=0 THEN RAISE EXCEPTION 'alert acknowledgement was already applied or revision-conflicted' USING ERRCODE='40001'; END IF;
  INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,operation_id) SELECT instance_id,rule_id,alert_id,3,4,t,'acknowledged',CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END FROM alerting.rule_state WHERE alert_id=p_alert_id AND instance_id=p_target_id AND acknowledged_at=t;
  INSERT INTO alerting.delivery_outbox(delivery_id,alert_id,instance_id,rule_id,destination_id,event_kind,payload,due_at,operation_id,destination_revision,destination_kind,destination_configuration_reference,destination_approval_revision,destination_approval_digest,destination_approval_scope,destination_configuration_digest) SELECT gen_random_uuid(),s.alert_id,s.instance_id,s.rule_id,d.destination_id,3,jsonb_build_object('schemaVersion',1,'alertId',s.alert_id,'targetId',s.instance_id,'event','acknowledged'),t,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,d.revision,d.kind,d.configuration_reference,d.approved_revision,d.approval_digest,d.approval_scope,d.configuration_digest FROM alerting.rule_state s JOIN alerting.destination d ON d.instance_id=s.instance_id AND d.enabled AND d.approved AND d.approval_scope=s.instance_id AND d.approved_kind=d.kind AND d.approved_configuration_reference=d.configuration_reference AND d.approved_revision IS NOT NULL AND d.approval_digest IS NOT NULL WHERE s.alert_id=p_alert_id AND s.acknowledged_at=t AND NOT alerting.maintenance_active_unscoped(s.instance_id,t) ON CONFLICT(alert_id,destination_id,event_kind) DO NOTHING;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('acknowledge',p_idempotency_key,sha256(convert_to(p_alert_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_expected_episode_id::text,'') || coalesce(p_request_digest,''),'UTF8')),p_target_id,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,'alert.acknowledge','allowed','succeeded',p_correlation_id,'{}'); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.upsert_destination(p_destination_id uuid, p_kind text, p_configuration_reference text, p_enabled boolean, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_request_digest text, p_approve boolean, p_action text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; updated integer; next_revision integer; expected_approval_digest bytea;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL THEN RAISE EXCEPTION 'destination target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(current_setting('sqlobserver.target_scope'),0));
  IF p_action NOT IN ('alert.destination.configure','alert.destination.update','alert.destination.retire') THEN RAISE EXCEPTION 'destination mutation action is invalid' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND p_expected_revision IS NOT NULL THEN RAISE EXCEPTION 'destination create cannot carry an expected revision' USING ERRCODE='22023'; END IF;
  IF p_action<>'alert.destination.configure' AND (p_expected_revision IS NULL OR p_expected_revision<=0) THEN RAISE EXCEPTION 'destination update requires a positive expected revision' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND EXISTS (SELECT 1 FROM alerting.destination WHERE destination_id=p_destination_id AND instance_id=current_setting('sqlobserver.target_scope')::uuid) THEN RAISE EXCEPTION 'destination already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_action<>'alert.destination.configure' AND NOT EXISTS (SELECT 1 FROM alerting.destination WHERE destination_id=p_destination_id AND instance_id=current_setting('sqlobserver.target_scope')::uuid) THEN RAISE EXCEPTION 'destination does not exist' USING ERRCODE='P0002'; END IF;
  IF EXISTS (SELECT 1 FROM alerting.destination WHERE destination_id=p_destination_id AND instance_id<>current_setting('sqlobserver.target_scope')::uuid) THEN RAISE EXCEPTION 'destination identity target mismatch' USING ERRCODE='42501'; END IF;
  IF p_kind NOT IN ('https-webhook','windows-event-log') OR position('://' in p_configuration_reference) > 0 THEN RAISE EXCEPTION 'destination catalog binding is invalid' USING ERRCODE='22023'; END IF;
  SELECT COALESCE((SELECT (d.revision + 1)::integer FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=current_setting('sqlobserver.target_scope')::uuid),1) INTO next_revision;
  expected_approval_digest := sha256(convert_to(current_setting('sqlobserver.target_scope') || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || next_revision::text,'UTF8'));
  IF p_approve AND (p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-fA-F]{64}$' OR decode(lower(p_request_digest),'hex') IS DISTINCT FROM expected_approval_digest) THEN RAISE EXCEPTION 'destination approval digest does not match reconciled configuration' USING ERRCODE='22023'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF (SELECT i.target_scope FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key) IS DISTINCT FROM current_setting('sqlobserver.target_scope')::uuid OR existing_digest IS DISTINCT FROM sha256(convert_to(current_setting('sqlobserver.target_scope') || '|' || p_destination_id::text || p_kind || p_configuration_reference || p_enabled::text || p_approve::text || p_action || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'destination revision is invalid' USING ERRCODE='22023'; END IF;
  IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.revision<>p_expected_revision) THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
  IF p_action='alert.destination.configure' THEN
    -- Configure is insert-only.  DO NOTHING turns a concurrent second create
    -- into the bounded conflict below without mutating the existing row.
    INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope) VALUES(p_destination_id,current_setting('sqlobserver.target_scope')::uuid,p_kind,p_configuration_reference,p_enabled,p_approve,CASE WHEN p_approve THEN p_kind ELSE NULL END,CASE WHEN p_approve THEN p_configuration_reference ELSE NULL END,CASE WHEN p_approve THEN next_revision ELSE NULL END,CASE WHEN p_approve THEN encode(expected_approval_digest,'hex') ELSE NULL END,CASE WHEN p_approve THEN current_setting('sqlobserver.target_scope')::uuid ELSE NULL END) ON CONFLICT(destination_id) DO NOTHING;
  ELSE
    INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope) VALUES(p_destination_id,current_setting('sqlobserver.target_scope')::uuid,p_kind,p_configuration_reference,p_enabled,p_approve,CASE WHEN p_approve THEN p_kind ELSE NULL END,CASE WHEN p_approve THEN p_configuration_reference ELSE NULL END,CASE WHEN p_approve THEN next_revision ELSE NULL END,CASE WHEN p_approve THEN encode(expected_approval_digest,'hex') ELSE NULL END,CASE WHEN p_approve THEN current_setting('sqlobserver.target_scope')::uuid ELSE NULL END) ON CONFLICT(destination_id) DO UPDATE SET kind=EXCLUDED.kind,configuration_reference=EXCLUDED.configuration_reference,enabled=EXCLUDED.enabled,revision=alerting.destination.revision+1,approved=EXCLUDED.approved,approved_kind=EXCLUDED.approved_kind,approved_configuration_reference=EXCLUDED.approved_configuration_reference,approved_revision=CASE WHEN EXCLUDED.approved THEN alerting.destination.revision+1 ELSE NULL END,approval_digest=EXCLUDED.approval_digest,approval_scope=EXCLUDED.approval_scope WHERE p_expected_revision IS NULL OR alerting.destination.revision=p_expected_revision;
  END IF;
  GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
  IF p_action<>'alert.destination.configure' OR NOT p_enabled THEN UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason=CASE WHEN NOT p_enabled THEN 'destination_disabled' ELSE 'destination_revision_changed' END,completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE destination_id=p_destination_id AND instance_id=current_setting('sqlobserver.target_scope')::uuid AND completed_at IS NULL AND cancelled_at IS NULL; END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_destination',p_idempotency_key,sha256(convert_to(current_setting('sqlobserver.target_scope') || '|' || p_destination_id::text || p_kind || p_configuration_reference || p_enabled::text || p_approve::text || p_action || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')),current_setting('sqlobserver.target_scope')::uuid,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,p_action,'allowed','succeeded',p_correlation_id,'{}'); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.cancel_maintenance(p_window_id uuid, p_target_id uuid, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_request_digest text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; changed integer; was_active boolean; window_ends timestamptz;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='cancel_maintenance' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_window_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'maintenance revision is invalid' USING ERRCODE='22023'; END IF;
  SELECT m.starts_at <= t AND t < m.ends_at, m.ends_at INTO was_active,window_ends FROM alerting.maintenance_window m WHERE m.window_id=p_window_id AND m.instance_id=p_target_id AND m.cancelled_at IS NULL FOR UPDATE;
  IF NOT FOUND THEN
    IF EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.window_id=p_window_id AND m.instance_id=p_target_id AND m.cancelled_at IS NOT NULL) THEN RAISE EXCEPTION 'maintenance cancellation was already applied or revision-conflicted' USING ERRCODE='40001';
    ELSE RAISE EXCEPTION 'maintenance window does not exist' USING ERRCODE='P0002'; END IF;
  END IF;
  IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.window_id=p_window_id AND m.instance_id=p_target_id AND m.revision<>p_expected_revision) THEN RAISE EXCEPTION 'maintenance revision conflict' USING ERRCODE='40001'; END IF;
  UPDATE alerting.maintenance_window SET cancelled_at=t,cancelled_actor_sid=p_actor_sid,cancelled_operation_id=CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,revision=revision+1 WHERE window_id=p_window_id AND instance_id=p_target_id AND cancelled_at IS NULL AND (p_expected_revision IS NULL OR revision=p_expected_revision);
  GET DIAGNOSTICS changed=ROW_COUNT; IF changed=0 THEN RAISE EXCEPTION 'maintenance cancellation was already applied or revision-conflicted' USING ERRCODE='40001'; END IF;
  IF was_active THEN
    UPDATE alerting.delivery_outbox d SET due_at=LEAST(d.due_at,t),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
      WHERE d.instance_id=p_target_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.due_at>=window_ends
        AND EXISTS (SELECT 1 FROM alerting.delivery_suppression_intent i WHERE i.delivery_id=d.delivery_id AND i.reason='maintenance_started:' || p_window_id::text);
  END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('cancel_maintenance',p_idempotency_key,sha256(convert_to(p_window_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')),p_target_id,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
    VALUES(t,a,'user',p_actor_sid,'alert.maintenance.retire','allowed','succeeded','maintenance_window',p_window_id::text,p_correlation_id,jsonb_build_object('operationId',CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,'targetId',p_target_id)); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.upsert_destination(p_destination_id uuid, p_kind text, p_configuration_reference text, p_enabled boolean, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_request_digest text, p_approve boolean, p_approval_revision bigint, p_approval_scope uuid, p_approval_digest text, p_action text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE target uuid := current_setting('sqlobserver.target_scope', true)::uuid; expected bytea; a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; updated integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF target IS NULL THEN RAISE EXCEPTION 'destination target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(target::text,0));
  IF p_action NOT IN ('alert.destination.configure','alert.destination.update','alert.destination.approve','alert.destination.retire') THEN RAISE EXCEPTION 'destination mutation action is invalid' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND (p_approve OR p_expected_revision IS NOT NULL) THEN RAISE EXCEPTION 'destination configure requires create semantics' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.approve' AND (NOT p_approve OR p_expected_revision IS NULL OR p_expected_revision<=0) THEN RAISE EXCEPTION 'destination approve requires an existing row and positive revision' USING ERRCODE='22023'; END IF;
  IF p_action IN ('alert.destination.update','alert.destination.retire') AND (p_approve OR p_expected_revision IS NULL OR p_expected_revision<=0) THEN RAISE EXCEPTION 'destination update requires a positive expected revision' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=target) THEN RAISE EXCEPTION 'destination already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_action<>'alert.destination.configure' AND NOT EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=target) THEN RAISE EXCEPTION 'destination does not exist' USING ERRCODE='P0002'; END IF;
  IF p_approve THEN
    IF p_approval_scope IS DISTINCT FROM target OR p_approval_revision IS NULL OR p_approval_revision <= 0 OR p_approval_digest IS NULL OR p_approval_digest !~ '^[0-9a-fA-F]{64}$' THEN RAISE EXCEPTION 'destination approval metadata is invalid' USING ERRCODE='22023'; END IF;
    IF p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-fA-F]{64}$' THEN RAISE EXCEPTION 'destination configuration digest is invalid' USING ERRCODE='22023'; END IF;
    expected := sha256(convert_to(target::text || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || p_approval_revision::text,'UTF8'));
    IF decode(lower(p_approval_digest),'hex') IS DISTINCT FROM expected THEN RAISE EXCEPTION 'destination approval digest does not match server preflight' USING ERRCODE='22023'; END IF;
    IF p_kind NOT IN ('https-webhook','windows-event-log') OR position('://' in p_configuration_reference) > 0 THEN RAISE EXCEPTION 'destination catalog binding is invalid' USING ERRCODE='22023'; END IF;
    SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key;
    IF a IS NOT NULL THEN
      IF (SELECT i.target_scope FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key) IS DISTINCT FROM target OR existing_digest IS DISTINCT FROM sha256(convert_to(target::text || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || p_enabled::text || '|' || p_approve::text || '|' || p_action || '|' || coalesce(p_expected_revision::text,'') || '|' || coalesce(p_request_digest,'') || '|' || p_approval_revision::text || '|' || p_approval_scope::text || '|' || p_approval_digest,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict' USING ERRCODE='40001'; END IF;
      RETURN QUERY SELECT a,t; RETURN;
    END IF;
  a := gen_random_uuid(); t := clock_timestamp();
    IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'destination revision is invalid' USING ERRCODE='22023'; END IF;
    IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=target AND d.revision<>p_expected_revision) THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
    INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope,configuration_digest)
      VALUES(p_destination_id,target,p_kind,p_configuration_reference,p_enabled,true,p_kind,p_configuration_reference,p_approval_revision,lower(p_approval_digest),target,lower(p_request_digest))
      ON CONFLICT(destination_id) DO UPDATE SET kind=EXCLUDED.kind,configuration_reference=EXCLUDED.configuration_reference,enabled=EXCLUDED.enabled,revision=alerting.destination.revision+1,approved=true,approved_kind=EXCLUDED.approved_kind,approved_configuration_reference=EXCLUDED.approved_configuration_reference,approved_revision=EXCLUDED.approved_revision,approval_digest=EXCLUDED.approval_digest,approval_scope=EXCLUDED.approval_scope,configuration_digest=EXCLUDED.configuration_digest WHERE p_expected_revision IS NULL OR alerting.destination.revision=p_expected_revision;
    GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
    UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason=CASE WHEN NOT p_enabled THEN 'destination_disabled' ELSE 'destination_revision_changed' END,completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE destination_id=p_destination_id AND instance_id=target AND p_action<>'alert.destination.configure' AND completed_at IS NULL AND cancelled_at IS NULL;
    INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_destination',p_idempotency_key,sha256(convert_to(target::text || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || p_enabled::text || '|' || p_approve::text || '|' || p_action || '|' || coalesce(p_expected_revision::text,'') || '|' || coalesce(p_request_digest,'') || '|' || p_approval_revision::text || '|' || p_approval_scope::text || '|' || p_approval_digest,'UTF8')),target,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
    INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,p_action,'allowed','succeeded',p_correlation_id,jsonb_build_object('configurationReference',p_configuration_reference,'revision',p_approval_revision,'scope',target));
    RETURN QUERY SELECT a,t;
  ELSE
    IF p_approval_revision IS NOT NULL OR p_approval_scope IS NOT NULL OR p_approval_digest IS NOT NULL THEN RAISE EXCEPTION 'approval metadata is only valid for approval' USING ERRCODE='22023'; END IF;
    RETURN QUERY SELECT * FROM alerting.upsert_destination(p_destination_id,p_kind,p_configuration_reference,p_enabled,p_idempotency_key,p_actor_sid,p_correlation_id,p_expected_revision,p_request_digest,false,p_action);
  END IF;
END $$;

CREATE OR REPLACE FUNCTION alerting.cancel_delivery_admin(p_delivery_id uuid, p_target_id uuid, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_reason text, p_request_digest text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; changed integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'delivery target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='cancel_delivery' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_delivery_id::text || p_target_id::text || coalesce(p_reason,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason=left(coalesce(p_reason,'administrative_cancel'),64),completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND completed_at IS NULL AND cancelled_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT; IF changed=0 THEN RAISE EXCEPTION 'delivery was already completed, cancelled, or target-bound incorrectly' USING ERRCODE='40001'; END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,audit_id,recorded_at) VALUES('cancel_delivery',p_idempotency_key,sha256(convert_to(p_delivery_id::text || p_target_id::text || coalesce(p_reason,'') || coalesce(p_request_digest,''),'UTF8')),p_target_id,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,'alert.delivery.cancel','allowed','succeeded',p_correlation_id,jsonb_build_object('deliveryId',p_delivery_id));
  RETURN QUERY SELECT a,t;
END $$;
