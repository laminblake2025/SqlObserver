-- Human delivery suppression is independent of evaluator state and evidence.
-- The target dispatch fence orders commands against already permitted sends.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE alerting.episode_snooze (
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 alert_id uuid NOT NULL, episode_id uuid NOT NULL,
 ends_at timestamptz, reason text NOT NULL CHECK(octet_length(reason) BETWEEN 1 AND 512),
 actor_sid text NOT NULL, applied_at timestamptz NOT NULL,
 operation_id uuid NOT NULL,
 PRIMARY KEY(instance_id,alert_id,episode_id)
);
CREATE TABLE alerting.snooze_operation (
 operation_id uuid PRIMARY KEY, instance_id uuid NOT NULL,
 alert_id uuid NOT NULL, episode_id uuid NOT NULL, action_name text NOT NULL,
 actor_sid text NOT NULL, request_digest bytea NOT NULL CHECK(octet_length(request_digest)=32),
 audit_id uuid NOT NULL, applied_at timestamptz NOT NULL,
 result_revision bigint NOT NULL CHECK(result_revision>0), ends_at timestamptz
);
ALTER TABLE alerting.episode_snooze ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.episode_snooze FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.snooze_operation ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.snooze_operation FORCE ROW LEVEL SECURITY;
CREATE POLICY episode_snooze_target_scope ON alerting.episode_snooze
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY episode_snooze_migrator ON alerting.episode_snooze
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY snooze_operation_target_scope ON alerting.snooze_operation
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY snooze_operation_migrator ON alerting.snooze_operation
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.episode_snooze,alerting.snooze_operation
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- Internal only: discovery needs an unscoped predicate; externally callable
-- functions enforce target scope or the authorized fleet target inventory.
CREATE FUNCTION alerting.episode_snoozed_internal(p_target uuid,p_alert uuid,p_now timestamptz)
RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,alerting AS $$
 SELECT EXISTS(SELECT 1 FROM alerting.rule_state s
 JOIN alerting.episode_snooze z ON z.instance_id=s.instance_id
  AND z.alert_id=s.alert_id AND z.episode_id=s.alert_episode_id
 WHERE s.instance_id=p_target AND s.alert_id=p_alert AND s.state IN(3,4)
  AND z.ends_at>p_now)
$$;
REVOKE ALL ON FUNCTION alerting.episode_snoozed_internal(uuid,uuid,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION alerting.get_episode_snooze(p_target_id uuid,p_alert_id uuid)
RETURNS TABLE(target_id uuid,alert_id uuid,episode_id uuid,revision bigint,state smallint,
 server_now timestamptz,ends_at timestamptz,reason text,actor_sid text,applied_at timestamptz,
 snoozed boolean,delivery_suppressed boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE t timestamptz:=clock_timestamp();
BEGIN
 IF p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_alert_id IS NULL OR p_alert_id='00000000-0000-0000-0000-000000000000'::uuid THEN
  RAISE EXCEPTION 'snooze identities required' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text
  THEN RAISE EXCEPTION 'snooze target scope required' USING ERRCODE='42501'; END IF;
 RETURN QUERY SELECT s.instance_id,s.alert_id,s.alert_episode_id,s.revision,s.state,t,
  z.ends_at,CASE WHEN z.ends_at IS NOT NULL THEN z.reason END,
  CASE WHEN z.ends_at IS NOT NULL THEN z.actor_sid END,
  CASE WHEN z.ends_at IS NOT NULL THEN z.applied_at END,
  coalesce(s.state IN(3,4) AND z.ends_at>t,false),
  s.delivery_suppressed OR coalesce(s.state IN(3,4) AND z.ends_at>t,false)
 FROM alerting.rule_state s LEFT JOIN alerting.episode_snooze z
  ON z.instance_id=s.instance_id AND z.alert_id=s.alert_id AND z.episode_id=s.alert_episode_id
 WHERE s.instance_id=p_target_id AND s.alert_id=p_alert_id AND s.alert_episode_id IS NOT NULL;
END $$;

CREATE FUNCTION alerting.set_episode_snooze(p_target_id uuid,p_alert_id uuid,
 p_expected_episode_id uuid,p_expected_revision bigint,p_ends_at timestamptz,p_reason text,
 p_idempotency_key text,p_actor_sid text,p_correlation_id uuid,p_request_digest text)
RETURNS TABLE(audit_id uuid,recorded_at timestamptz,revision bigint,ends_at timestamptz,applied_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting,audit
SET timezone='UTC' AS $$
DECLARE op uuid; existing alerting.snooze_operation%ROWTYPE;
 current_state alerting.rule_state%ROWTYPE; a uuid:=gen_random_uuid(); t timestamptz;
 digest bytea; action text:=CASE WHEN p_ends_at IS NULL THEN 'alert.unsnooze' ELSE 'alert.snooze' END;
BEGIN
 PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
 IF p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
  OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text
  THEN RAISE EXCEPTION 'snooze target scope required' USING ERRCODE='42501'; END IF;
 op:=p_idempotency_key::uuid;
 digest:=sha256(convert_to(jsonb_build_object('target',p_target_id,'alert',p_alert_id,
  'episode',p_expected_episode_id,'revision',p_expected_revision,'ends',p_ends_at,
  'reason',p_reason,'actor',p_actor_sid,'action',action,'request',p_request_digest)::text,'UTF8'));
 PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
 -- Replay is an immutable receipt even after expiry, resolution or later edits.
 SELECT i.* INTO existing FROM alerting.snooze_operation i WHERE i.operation_id=op;
 IF FOUND THEN
  IF existing.request_digest IS DISTINCT FROM digest THEN
   RAISE EXCEPTION 'snooze operation payload conflict' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT existing.audit_id,existing.applied_at,existing.result_revision,
   existing.ends_at,existing.applied_at; RETURN;
 END IF;
 IF p_alert_id IS NULL OR p_alert_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_expected_episode_id IS NULL OR p_expected_episode_id='00000000-0000-0000-0000-000000000000'::uuid OR p_expected_revision IS NULL
  OR p_expected_revision<=0 OR p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-fA-F]{64}$'
  OR p_reason IS NULL OR length(btrim(p_reason))=0 OR octet_length(p_reason)>512 OR p_reason ~ '[[:cntrl:]]'
  OR p_actor_sid IS NULL OR p_actor_sid !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
  OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid THEN
  RAISE EXCEPTION 'snooze arguments invalid' USING ERRCODE='22023'; END IF;
 IF lower(p_request_digest) IS DISTINCT FROM encode(sha256(convert_to(action || '|' || p_target_id::text
  || '|' || p_alert_id::text || '|' || p_expected_episode_id::text || '|' || p_expected_revision::text
  || '|' || coalesce(to_char(p_ends_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'<null>')
  || '|' || p_reason,'UTF8')),'hex') THEN
  RAISE EXCEPTION 'snooze request digest mismatch' USING ERRCODE='22023'; END IF;
 SELECT s.* INTO current_state FROM alerting.rule_state s
  WHERE s.instance_id=p_target_id AND s.alert_id=p_alert_id FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'alert not found' USING ERRCODE='P0002'; END IF;
 IF current_state.state NOT IN(3,4) OR current_state.alert_episode_id IS DISTINCT FROM p_expected_episode_id
  OR current_state.revision<>p_expected_revision THEN
  RAISE EXCEPTION 'alert episode or revision changed' USING ERRCODE='40001'; END IF;
 t:=clock_timestamp();
 IF p_ends_at IS NOT NULL AND (p_ends_at<=t OR p_ends_at>t+interval '24 hours') THEN
  RAISE EXCEPTION 'snooze deadline invalid' USING ERRCODE='22023'; END IF;
 INSERT INTO alerting.episode_snooze(instance_id,alert_id,episode_id,ends_at,reason,actor_sid,applied_at,operation_id)
 VALUES(p_target_id,p_alert_id,p_expected_episode_id,p_ends_at,p_reason,p_actor_sid,t,op)
 ON CONFLICT(instance_id,alert_id,episode_id) DO UPDATE
  SET ends_at=EXCLUDED.ends_at,reason=EXCLUDED.reason,actor_sid=EXCLUDED.actor_sid,
   applied_at=EXCLUDED.applied_at,operation_id=EXCLUDED.operation_id;
 UPDATE alerting.rule_state s SET revision=s.revision+1
  WHERE s.instance_id=p_target_id AND s.rule_id=current_state.rule_id;
 INSERT INTO alerting.snooze_operation(operation_id,instance_id,alert_id,episode_id,action_name,
  actor_sid,request_digest,audit_id,applied_at,result_revision,ends_at)
 VALUES(op,p_target_id,p_alert_id,p_expected_episode_id,action,p_actor_sid,digest,a,t,current_state.revision+1,p_ends_at);
 INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,
  authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
 VALUES(t,a,'user',p_actor_sid,action,'allowed','succeeded','alerting',p_target_id::text,p_correlation_id,
  jsonb_build_object('targetId',p_target_id,'alertId',p_alert_id,'episodeId',p_expected_episode_id,
   'revision',current_state.revision+1,'endsAtUtc',p_ends_at,'reason',p_reason,'operationId',op));
 RETURN QUERY SELECT a,t,current_state.revision+1,p_ends_at,t;
END $$;
REVOKE ALL ON FUNCTION alerting.get_episode_snooze(uuid,uuid),
 alerting.set_episode_snooze(uuid,uuid,uuid,bigint,timestamptz,text,text,text,uuid,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.get_episode_snooze(uuid,uuid),
 alerting.set_episode_snooze(uuid,uuid,uuid,bigint,timestamptz,text,text,text,uuid,text)
 TO sqlobserver_server;

-- Preserve the existing fire-time cursor ordering; this is current status,
-- not an immutable state snapshot. Legacy readers retain their old signatures.
CREATE VIEW reporting.active_alerts_v2 WITH(security_barrier=true) AS
WITH observed AS MATERIALIZED(SELECT clock_timestamp() AS now_utc)
SELECT s.alert_id,s.rule_id,s.instance_id AS target_id,r.name AS rule_name,s.state,
 s.first_match_at AS first_observed_at,s.fired_at,s.acknowledged_at,s.last_value AS value,s.reason,
 s.delivery_suppressed OR coalesce(z.ends_at>observed.now_utc,false) AS delivery_suppressed,
 s.alert_episode_id AS episode_id,s.revision,z.ends_at AS snoozed_until,
 CASE WHEN z.ends_at IS NOT NULL THEN z.reason END AS snooze_reason,
 CASE WHEN z.ends_at IS NOT NULL THEN z.actor_sid END AS snoozed_by,
 CASE WHEN z.ends_at IS NOT NULL THEN z.applied_at END AS snoozed_at,
 coalesce(z.ends_at>observed.now_utc,false) AS snoozed
FROM alerting.rule_state s JOIN alerting.rule r USING(rule_id,instance_id)
LEFT JOIN alerting.episode_snooze z ON z.instance_id=s.instance_id AND z.alert_id=s.alert_id
 AND z.episode_id=s.alert_episode_id
CROSS JOIN observed
WHERE s.state IN(3,4);
REVOKE ALL ON reporting.active_alerts_v2 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.list_active_alerts_v2(p_target_id uuid,p_max_results integer,
 p_after_fired timestamptz,p_after_alert_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(alert_id uuid,rule_id uuid,target_id uuid,rule_name text,state smallint,
 first_observed_at timestamptz,fired_at timestamptz,acknowledged_at timestamptz,value double precision,
 reason text,delivery_suppressed boolean,episode_id uuid,revision bigint,snoozed_until timestamptz,
 snooze_reason text,snoozed_by text,snoozed_at timestamptz,snoozed boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,reporting AS $$
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text
  THEN RAISE EXCEPTION 'active alert target scope required' USING ERRCODE='42501'; END IF;
 IF p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100
  OR (p_after_fired IS NULL)<>(p_after_alert_id IS NULL) THEN
  RAISE EXCEPTION 'active alert page bounds rejected' USING ERRCODE='22023'; END IF;
 RETURN QUERY SELECT a.alert_id,a.rule_id,a.target_id,a.rule_name,a.state,a.first_observed_at,
  a.fired_at,a.acknowledged_at,a.value,a.reason,a.delivery_suppressed,a.episode_id,a.revision,
  a.snoozed_until,a.snooze_reason,a.snoozed_by,a.snoozed_at,a.snoozed
 FROM reporting.active_alerts_v2 a WHERE a.target_id=p_target_id
  AND (p_snapshot_utc IS NULL OR coalesce(a.fired_at,a.first_observed_at)<=p_snapshot_utc)
  AND (p_after_fired IS NULL OR coalesce(a.fired_at,a.first_observed_at)<p_after_fired
   OR (coalesce(a.fired_at,a.first_observed_at)=p_after_fired AND a.alert_id>p_after_alert_id))
 ORDER BY coalesce(a.fired_at,a.first_observed_at) DESC,a.alert_id LIMIT p_max_results+1;
END $$;
REVOKE ALL ON FUNCTION reporting.list_active_alerts_v2(uuid,integer,timestamptz,uuid,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_active_alerts_v2(uuid,integer,timestamptz,uuid,timestamptz)
 TO sqlobserver_server;

-- Keep existing delivery function signatures and exact lease fencing.
CREATE OR REPLACE FUNCTION alerting.claim_due_deliveries(p_instance_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(delivery_id uuid, alert_id uuid, destination_id uuid, target_id uuid, kind text, configuration_reference text, payload bytea, attempt integer, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'delivery claim arguments are invalid' USING ERRCODE='22023'; END IF;
  RETURN QUERY WITH candidates AS MATERIALIZED (SELECT x.delivery_id FROM alerting.delivery_outbox x JOIN alerting.rule xr ON xr.instance_id=x.instance_id AND xr.rule_id=x.rule_id AND xr.enabled JOIN alerting.destination xz ON xz.destination_id=x.destination_id AND xz.instance_id=x.instance_id AND xz.enabled AND xz.approved AND xz.approval_scope=x.destination_approval_scope AND xz.approved_kind=x.destination_kind AND xz.approved_configuration_reference=x.destination_configuration_reference AND xz.approved_revision=x.destination_approval_revision AND xz.approval_digest=x.destination_approval_digest AND xz.configuration_digest=x.destination_configuration_digest AND xz.revision=x.destination_revision WHERE x.instance_id=p_instance_id AND x.completed_at IS NULL AND x.cancelled_at IS NULL AND x.attempt BETWEEN 0 AND 7 AND x.due_at<=clock_timestamp() AND (x.leased_until IS NULL OR x.leased_until<clock_timestamp()) AND NOT alerting.maintenance_active_unscoped(x.instance_id,clock_timestamp()) AND NOT alerting.episode_snoozed_internal(x.instance_id,x.alert_id,clock_timestamp()) ORDER BY x.due_at,x.delivery_id FOR UPDATE OF x SKIP LOCKED LIMIT p_max_results)
  UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes',lease_fencing=p_fencing,lease_work_key=p_work_key,lease_owner_execution_id=p_owner_execution_id
    WHERE d.instance_id=p_instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.attempt<=7 AND d.due_at<=clock_timestamp() AND (d.leased_until IS NULL OR d.leased_until<clock_timestamp())
      AND NOT alerting.maintenance_active_unscoped(d.instance_id,clock_timestamp()) AND NOT alerting.episode_snoozed_internal(d.instance_id,d.alert_id,clock_timestamp())
      AND EXISTS (SELECT 1 FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=d.instance_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision)
    AND d.delivery_id IN (SELECT candidates.delivery_id FROM candidates)
    RETURNING d.delivery_id,d.alert_id,d.destination_id,d.instance_id,(SELECT z.kind FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_instance_id),(SELECT z.configuration_reference FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_instance_id),convert_to(d.payload::text,'UTF8'),d.attempt,d.due_at;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

CREATE OR REPLACE FUNCTION alerting.renew_delivery(p_delivery_id uuid, p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock_shared(hashtextextended(p_target_id::text,0));
  UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes' WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp() AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND EXISTS (SELECT 1 FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_target_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision) AND EXISTS (SELECT 1 FROM alerting.rule r WHERE r.rule_id=d.rule_id AND r.instance_id=p_target_id AND r.enabled) AND NOT alerting.maintenance_active_unscoped(p_target_id,clock_timestamp()) AND NOT alerting.episode_snoozed_internal(p_target_id,d.alert_id,clock_timestamp());
  GET DIAGNOSTICS changed=ROW_COUNT; PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing); RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.recheck_delivery(p_delivery_id uuid, p_target_id uuid)
RETURNS text LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  IF EXISTS (SELECT 1 FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND (cancelled_at IS NOT NULL OR completed_at IS NOT NULL)) THEN RETURN 'Cancelled'; END IF;
  IF alerting.maintenance_active_unscoped(p_target_id,clock_timestamp()) THEN RETURN 'Maintenance'; END IF;
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox d JOIN alerting.rule r ON r.rule_id=d.rule_id AND r.instance_id=d.instance_id WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND r.enabled) THEN RETURN 'RuleDisabled'; END IF;
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox d JOIN alerting.destination z ON z.destination_id=d.destination_id AND z.instance_id=d.instance_id WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision) THEN RETURN 'DestinationNotApproved'; END IF;
  IF EXISTS (SELECT 1 FROM alerting.delivery_outbox d WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND alerting.episode_snoozed_internal(p_target_id,d.alert_id,clock_timestamp())) THEN RETURN 'Snoozed'; END IF;
  RETURN 'Active';
END $$;

CREATE OR REPLACE FUNCTION alerting.defer_delivery(p_delivery_id uuid, p_target_id uuid, p_reason text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer; active_window_id uuid; active_window_ends timestamptz;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock_shared(hashtextextended(p_target_id::text,0));
  -- Snooze clears only this claim. Preserve the retry deadline and attempt;
  -- the episode predicate alone resumes due work at expiry or explicit unsnooze.
  IF EXISTS (SELECT 1 FROM alerting.delivery_outbox d WHERE d.delivery_id=p_delivery_id
      AND d.instance_id=p_target_id AND alerting.episode_snoozed_internal(p_target_id,d.alert_id,clock_timestamp()))
     AND NOT alerting.maintenance_active_unscoped(p_target_id,clock_timestamp()) THEN
    UPDATE alerting.delivery_outbox d SET leased_until=NULL,lease_work_key=NULL,
      lease_owner_execution_id=NULL,lease_fencing=NULL,last_error_code='snoozed'
    WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id
      AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id
      AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp()
      AND d.completed_at IS NULL AND d.cancelled_at IS NULL;
    GET DIAGNOSTICS changed=ROW_COUNT;
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    RETURN changed=1;
  END IF;
  -- Overlapping windows are deterministic: the earliest start, then UUID,
  -- owns this defer intent. Cancellation can therefore release only work
  -- attributed to that exact active window.
  SELECT m.window_id,m.ends_at INTO active_window_id,active_window_ends
    FROM alerting.maintenance_window m
    WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at
    ORDER BY m.starts_at,m.window_id LIMIT 1 FOR UPDATE;
  IF active_window_id IS NULL THEN
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    RETURN false;
  END IF;
  UPDATE alerting.delivery_outbox d
    SET due_at=active_window_ends,
        leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL,last_error_code=left(coalesce(p_reason,'maintenance'),64)
    WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp() AND d.completed_at IS NULL AND d.cancelled_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT;
  INSERT INTO alerting.delivery_suppression_intent(delivery_id,instance_id,operation_id,reason)
    SELECT d.delivery_id,d.instance_id,d.operation_id,'maintenance_started:' || active_window_id::text FROM alerting.delivery_outbox d WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND changed=1;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.renew_delivery_with_outcome(p_delivery_id uuid, p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE readiness text; changed boolean;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock_shared(hashtextextended(p_target_id::text,0));
  -- Serialize readiness, maintenance transition, and lease mutation on the
  -- claimed outbox row.  A revoked/disabled row therefore cannot be renewed
  -- after this function has decided to keep it active.
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND instance_id=p_target_id FOR UPDATE) THEN RETURN 'LostFence'; END IF;
  readiness := alerting.recheck_delivery(p_delivery_id,p_target_id);
  IF readiness='Snoozed' THEN changed := alerting.defer_delivery(p_delivery_id,p_target_id,'snoozed',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'DeferSnooze'; END IF;
  IF readiness='Maintenance' THEN changed := alerting.defer_delivery(p_delivery_id,p_target_id,'maintenance',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'DeferMaintenance'; END IF;
  IF readiness='RuleDisabled' THEN changed := alerting.cancel_delivery(p_delivery_id,p_target_id,'disabled',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'CancelDisabled'; END IF;
  IF readiness='DestinationNotApproved' THEN changed := alerting.cancel_delivery(p_delivery_id,p_target_id,'unapproved',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'CancelUnapproved'; END IF;
  IF readiness='Cancelled' THEN RETURN 'CancelDisabled'; END IF;
  IF NOT alerting.renew_delivery(p_delivery_id,p_target_id,p_work_key,p_owner_execution_id,p_fencing) THEN RETURN 'LostFence'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN 'Active';
EXCEPTION
  -- A lease can be released between the worker's claim and its renewal.
  -- Convert the assertion failure into the typed outcome and clear only the
  -- stale claim owned by this exact fence; never touch a newer claimant.
  WHEN SQLSTATE '55000' THEN
    UPDATE alerting.delivery_outbox d
       SET leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
     WHERE d.delivery_id=p_delivery_id
       AND d.instance_id=p_target_id
       AND d.lease_work_key=p_work_key
       AND d.lease_owner_execution_id=p_owner_execution_id
       AND d.lease_fencing=p_fencing
       AND NOT EXISTS (
         SELECT 1 FROM control.worker_lease l
          WHERE l.work_key=p_work_key
            AND l.owner_execution_id=p_owner_execution_id
            AND l.fencing_token=p_fencing
            AND l.released_at IS NULL
            AND l.expires_at>clock_timestamp());
    RETURN 'LostFence';
END $$;

CREATE OR REPLACE FUNCTION alerting.list_legacy_targets_with_due_alert_work(p_work_kind text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(target_id uuid) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_work_kind NOT IN ('evaluation','delivery') OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing<=0 OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'alert work discovery arguments are invalid' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_work_kind='evaluation' THEN
    -- Reconcile the oldest canonical evidence before paging targets. This
    -- makes target discovery consume durable queue identities rather than
    -- repeatedly rediscovering already queued source rows.
    PERFORM alerting.reconcile_due_evidence_internal(p_max_results);
  END IF;
  RETURN QUERY SELECT t.instance_id
  FROM control.observation_target t
    WHERE t.retired_at IS NULL AND
    CASE WHEN p_work_kind='evaluation' THEN EXISTS (SELECT 1 FROM alerting.evaluation_queue q JOIN alerting.rule r ON r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.enabled WHERE q.instance_id=t.instance_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())) ELSE EXISTS (SELECT 1 FROM alerting.delivery_outbox d JOIN alerting.rule r ON r.instance_id=d.instance_id AND r.rule_id=d.rule_id AND r.enabled JOIN alerting.destination z ON z.destination_id=d.destination_id AND z.instance_id=d.instance_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision WHERE d.instance_id=t.instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.attempt BETWEEN 0 AND 7 AND d.due_at<=clock_timestamp() AND (d.leased_until IS NULL OR d.leased_until<clock_timestamp()) AND NOT alerting.maintenance_active_unscoped(t.instance_id,clock_timestamp()) AND NOT alerting.episode_snoozed_internal(d.instance_id,d.alert_id,clock_timestamp())) END
  ORDER BY CASE WHEN p_work_kind='evaluation' THEN (SELECT min(q.observed_at) FROM alerting.evaluation_queue q JOIN alerting.rule r ON r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.enabled WHERE q.instance_id=t.instance_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())) ELSE (SELECT min(d.due_at) FROM alerting.delivery_outbox d WHERE d.instance_id=t.instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.due_at<=clock_timestamp()) END NULLS LAST,t.instance_id LIMIT p_max_results;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

CREATE FUNCTION reporting.list_fleet_active_alerts_v2(
 p_target_ids uuid[], p_all_targets boolean, p_max_results integer,
 p_after_at timestamptz, p_after_target uuid, p_after_alert_id uuid,
 p_snapshot_utc timestamptz)
RETURNS TABLE(alert_id uuid,rule_id uuid,target_id uuid,target_name text,
 rule_name text,state smallint,first_observed_at timestamptz,
 fired_at timestamptz,acknowledged_at timestamptz,value double precision,
 reason text,delivery_suppressed boolean,episode_id uuid,revision bigint,snoozed_until timestamptz,snooze_reason text,snoozed_by text,snoozed_at timestamptz,snoozed boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,control,alerting
AS $inbox$
BEGIN
 IF p_target_ids IS NULL OR cardinality(p_target_ids)>1024
    OR array_position(p_target_ids,NULL::uuid) IS NOT NULL
    OR p_all_targets IS NULL OR p_max_results IS NULL
    OR p_max_results<1 OR p_max_results>100 OR p_snapshot_utc IS NULL
    OR (p_after_at IS NULL AND
        (p_after_target IS NOT NULL OR p_after_alert_id IS NOT NULL))
    OR (p_after_at IS NOT NULL AND
        (p_after_target IS NULL OR p_after_alert_id IS NULL)) THEN
  RAISE EXCEPTION 'fleet alert page bounds rejected' USING ERRCODE='22023';
 END IF;
 RETURN QUERY
 SELECT a.alert_id,a.rule_id,a.target_id,t.display_name,a.rule_name,a.state,
   a.first_observed_at,a.fired_at,a.acknowledged_at,a.value,a.reason,
   a.delivery_suppressed,a.episode_id,a.revision,a.snoozed_until,a.snooze_reason,a.snoozed_by,a.snoozed_at,a.snoozed
 FROM reporting.active_alerts_v2 a
 JOIN control.observation_target t ON t.instance_id=a.target_id
 WHERE (p_all_targets OR a.target_id=ANY(p_target_ids))
   AND coalesce(a.fired_at,a.first_observed_at)<=p_snapshot_utc
   AND (p_after_at IS NULL OR
     (coalesce(a.fired_at,a.first_observed_at),a.target_id,a.alert_id)
       <(p_after_at,p_after_target,p_after_alert_id))
 ORDER BY coalesce(a.fired_at,a.first_observed_at) DESC,
   a.target_id DESC,a.alert_id DESC
 LIMIT p_max_results+1;
END $inbox$;

REVOKE ALL ON FUNCTION reporting.list_fleet_active_alerts_v2(
 uuid[],boolean,integer,timestamptz,uuid,uuid,timestamptz)
FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_fleet_active_alerts_v2(
 uuid[],boolean,integer,timestamptz,uuid,uuid,timestamptz)
TO sqlobserver_server;


CREATE OR REPLACE FUNCTION audit.append_m8_administrative_activity(
    p_actor_identifier text,
    p_correlation_id uuid,
    p_action text,
    p_target_id uuid,
    p_operation_id uuid,
    p_authorization_result text,
    p_operation_outcome text,
    p_reason text,
    p_safe_details jsonb
)
RETURNS TABLE(audit_activity_id uuid, repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, public, audit
SET TimeZone = 'UTC'
AS $$
DECLARE captured_repository_time timestamptz := clock_timestamp(); generated_activity_id uuid := gen_random_uuid();
BEGIN
  IF p_actor_identifier IS NULL OR p_actor_identifier !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
     OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_operation_id IS NULL OR p_operation_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_action NOT IN ('alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.snooze','alert.unsnooze','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
     OR p_authorization_result NOT IN ('allowed','denied')
     OR p_operation_outcome NOT IN ('succeeded','failed','conflict','cancelled','rejected')
     OR p_reason NOT IN ('completed','principal_disabled','required_role_missing','target_out_of_scope','already_exists','revision_conflict','target_not_found','target_retired','discovery_failed','repository_failure','invalid_request','idempotent_replay')
     OR jsonb_typeof(coalesce(p_safe_details,'{}'::jsonb)) <> 'object'
     OR octet_length(coalesce(p_safe_details,'{}'::jsonb)::text) > 2048 THEN
    RAISE EXCEPTION 'invalid M8 outcome audit fields' USING ERRCODE='22023';
  END IF;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
  VALUES(captured_repository_time,generated_activity_id,'user',p_actor_identifier,p_action,p_authorization_result,p_operation_outcome,'alerting',p_target_id::text,p_correlation_id,
         jsonb_build_object('operationId',p_operation_id,'reason',p_reason,'details',coalesce(p_safe_details,'{}'::jsonb)));
  RETURN QUERY SELECT generated_activity_id,captured_repository_time;
END $$;

CREATE OR REPLACE FUNCTION audit.append_denied_m8_administrative_activity(
    p_actor_identifier text,
    p_correlation_id uuid,
    p_action text,
    p_target_id uuid,
    p_reason text
)
RETURNS TABLE(audit_activity_id uuid, repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, public, audit
SET TimeZone = 'UTC'
AS $$
DECLARE captured_repository_time timestamptz := clock_timestamp(); generated_activity_id uuid := gen_random_uuid();
BEGIN
  IF p_actor_identifier IS NULL OR p_actor_identifier !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
     OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_action NOT IN ('register_observation_target','update_observation_target','retire_observation_target','request_capability_rediscovery','alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.snooze','alert.unsnooze','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
     OR p_reason NOT IN ('principal_disabled','required_role_missing','target_out_of_scope','already_exists','revision_conflict','target_not_found','target_retired','discovery_failed','repository_failure','invalid_request','idempotent_replay') THEN
    RAISE EXCEPTION 'invalid M8 denial audit fields' USING ERRCODE='22023';
  END IF;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
  VALUES(captured_repository_time,generated_activity_id,'user',p_actor_identifier,CASE p_action WHEN 'register_observation_target' THEN 'observation_target.register' WHEN 'update_observation_target' THEN 'observation_target.update' WHEN 'retire_observation_target' THEN 'observation_target.retire' WHEN 'request_capability_rediscovery' THEN 'capability.rediscovery_request' ELSE p_action END,'denied','rejected',CASE WHEN p_action LIKE 'alert.%' THEN 'alerting' ELSE 'observation_target' END,p_target_id::text,p_correlation_id,jsonb_build_object('reason',p_reason));
  RETURN QUERY SELECT generated_activity_id,captured_repository_time;
END $$;
