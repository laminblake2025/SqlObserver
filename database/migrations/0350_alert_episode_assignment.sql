-- The server-approved responder roster is ownership metadata, not a role grant.
-- Assignment is independent of evaluation evidence, acknowledgement and delivery.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE alerting.episode_assignment(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 alert_id uuid NOT NULL,episode_id uuid NOT NULL,
 assignee_sid text,assignee_name text,reason text NOT NULL CHECK(octet_length(reason) BETWEEN 1 AND 512),
 actor_sid text NOT NULL,applied_at timestamptz NOT NULL,operation_id uuid NOT NULL,
 PRIMARY KEY(instance_id,alert_id,episode_id),
 CHECK((assignee_sid IS NULL)=(assignee_name IS NULL))
);
CREATE TABLE alerting.assignment_operation(
 operation_id uuid PRIMARY KEY,change_sequence bigserial NOT NULL UNIQUE,
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 alert_id uuid NOT NULL,episode_id uuid NOT NULL,action_name text NOT NULL,
 actor_sid text NOT NULL,request_digest bytea NOT NULL CHECK(octet_length(request_digest)=32),
 audit_id uuid NOT NULL,applied_at timestamptz NOT NULL,
 result_revision bigint NOT NULL CHECK(result_revision>0),
 assignee_sid text,assignee_name text,previous_assignee_sid text,previous_assignee_name text,
 reason text NOT NULL CHECK(octet_length(reason) BETWEEN 1 AND 512),
 CHECK((assignee_sid IS NULL)=(assignee_name IS NULL)),
 CHECK((previous_assignee_sid IS NULL)=(previous_assignee_name IS NULL))
);
CREATE INDEX ix_assignment_history ON alerting.assignment_operation(instance_id,alert_id,change_sequence DESC);
ALTER TABLE alerting.episode_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.episode_assignment FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.assignment_operation ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.assignment_operation FORCE ROW LEVEL SECURITY;
CREATE POLICY episode_assignment_scope ON alerting.episode_assignment
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY episode_assignment_migrator ON alerting.episode_assignment
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY assignment_operation_scope ON alerting.assignment_operation
 USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY assignment_operation_migrator ON alerting.assignment_operation
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.episode_assignment,alerting.assignment_operation
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON SEQUENCE alerting.assignment_operation_change_sequence_seq
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION alerting.get_episode_assignment(p_target_id uuid,p_alert_id uuid)
RETURNS TABLE(target_id uuid,alert_id uuid,episode_id uuid,revision bigint,state smallint,
 server_now timestamptz,assignee_sid text,assignee_name text,assignment_reason text,assigned_by text,assigned_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE t timestamptz:=clock_timestamp();
BEGIN
 IF p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_alert_id IS NULL OR p_alert_id='00000000-0000-0000-0000-000000000000'::uuid THEN
  RAISE EXCEPTION 'assignment identities required' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN
  RAISE EXCEPTION 'assignment target scope required' USING ERRCODE='42501'; END IF;
 RETURN QUERY SELECT s.instance_id,s.alert_id,s.alert_episode_id,s.revision,s.state,t,
  a.assignee_sid,a.assignee_name,
  CASE WHEN a.assignee_sid IS NOT NULL THEN a.reason END,
  CASE WHEN a.assignee_sid IS NOT NULL THEN a.actor_sid END,
  CASE WHEN a.assignee_sid IS NOT NULL THEN a.applied_at END
 FROM alerting.rule_state s LEFT JOIN alerting.episode_assignment a
  ON a.instance_id=s.instance_id AND a.alert_id=s.alert_id AND a.episode_id=s.alert_episode_id
 WHERE s.instance_id=p_target_id AND s.alert_id=p_alert_id AND s.alert_episode_id IS NOT NULL;
END $$;

CREATE FUNCTION alerting.set_episode_assignment(p_target_id uuid,p_alert_id uuid,
 p_expected_episode_id uuid,p_expected_revision bigint,p_assignee_sid text,p_assignee_name text,
 p_assignee_eligible boolean,p_reason text,p_idempotency_key text,p_actor_sid text,
 p_correlation_id uuid,p_request_digest text)
RETURNS TABLE(audit_id uuid,recorded_at timestamptz,revision bigint,assignee_sid text,applied_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting,audit SET TimeZone='UTC' AS $$
DECLARE op uuid; existing alerting.assignment_operation%ROWTYPE;
 current_state alerting.rule_state%ROWTYPE; prior_assignment alerting.episode_assignment%ROWTYPE;
 a uuid:=gen_random_uuid();t timestamptz;digest bytea;
 action text:=CASE WHEN p_assignee_sid IS NULL THEN 'alert.unassign' ELSE 'alert.assign' END;
 owner_name text;
BEGIN
 PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
 IF p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
  OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN
  RAISE EXCEPTION 'assignment target scope required' USING ERRCODE='42501'; END IF;
 op:=p_idempotency_key::uuid;
 -- Approval and display name are server roster observations, not request identity.
 -- Exact historical replay is independent of later roster edits or revocation.
 digest:=sha256(convert_to(jsonb_build_object('target',p_target_id,'alert',p_alert_id,
  'episode',p_expected_episode_id,'revision',p_expected_revision,'assignee',p_assignee_sid,
  'reason',p_reason,'actor',p_actor_sid,'action',action,'request',p_request_digest)::text,'UTF8'));
 PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
 SELECT i.* INTO existing FROM alerting.assignment_operation i WHERE i.operation_id=op;
 IF FOUND THEN
  IF existing.request_digest IS DISTINCT FROM digest THEN
   RAISE EXCEPTION 'assignment operation payload conflict' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT existing.audit_id,existing.applied_at,existing.result_revision,
   existing.assignee_sid,existing.applied_at; RETURN;
 END IF;
 IF p_alert_id IS NULL OR p_alert_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_expected_episode_id IS NULL OR p_expected_episode_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_expected_revision IS NULL OR p_expected_revision<=0
  OR p_actor_sid IS NULL OR octet_length(p_actor_sid)>184 OR p_actor_sid !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
  OR (p_assignee_sid IS NOT NULL AND (octet_length(p_assignee_sid)>184 OR p_assignee_sid !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'))
  OR p_reason IS NULL OR length(btrim(p_reason))=0 OR octet_length(p_reason)>512 OR p_reason ~ '[[:cntrl:]]'
  OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-fA-F]{64}$' THEN
  RAISE EXCEPTION 'assignment arguments invalid' USING ERRCODE='22023'; END IF;
 IF lower(p_request_digest) IS DISTINCT FROM encode(sha256(convert_to(action || '|' || p_target_id::text
  || '|' || p_alert_id::text || '|' || p_expected_episode_id::text || '|' || p_expected_revision::text
  || '|' || coalesce(p_assignee_sid,'<null>') || '|' || p_reason,'UTF8')),'hex') THEN
  RAISE EXCEPTION 'assignment request digest mismatch' USING ERRCODE='22023'; END IF;
 SELECT s.* INTO current_state FROM alerting.rule_state s
  WHERE s.instance_id=p_target_id AND s.alert_id=p_alert_id FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'alert not found' USING ERRCODE='P0002'; END IF;
 IF current_state.state NOT IN(3,4) OR current_state.alert_episode_id IS DISTINCT FROM p_expected_episode_id
  OR current_state.revision<>p_expected_revision THEN
  RAISE EXCEPTION 'alert episode or revision changed' USING ERRCODE='40001'; END IF;
 IF p_assignee_sid IS NOT NULL THEN
  IF p_assignee_eligible IS DISTINCT FROM true THEN
   RAISE EXCEPTION 'assignee not approved' USING ERRCODE='P0422'; END IF;
  IF p_assignee_name IS NULL OR length(btrim(p_assignee_name))=0 OR octet_length(p_assignee_name)>128 OR p_assignee_name ~ '[[:cntrl:]]' THEN
   RAISE EXCEPTION 'approved assignee name invalid' USING ERRCODE='22023'; END IF;
  owner_name:=p_assignee_name;
 END IF;
 SELECT x.* INTO prior_assignment FROM alerting.episode_assignment x WHERE x.instance_id=p_target_id
  AND x.alert_id=p_alert_id AND x.episode_id=p_expected_episode_id;
 t:=clock_timestamp();
 INSERT INTO alerting.episode_assignment(instance_id,alert_id,episode_id,assignee_sid,assignee_name,reason,actor_sid,applied_at,operation_id)
 VALUES(p_target_id,p_alert_id,p_expected_episode_id,p_assignee_sid,owner_name,p_reason,p_actor_sid,t,op)
 ON CONFLICT(instance_id,alert_id,episode_id) DO UPDATE
 SET assignee_sid=EXCLUDED.assignee_sid,assignee_name=EXCLUDED.assignee_name,reason=EXCLUDED.reason,
  actor_sid=EXCLUDED.actor_sid,applied_at=EXCLUDED.applied_at,operation_id=EXCLUDED.operation_id;
 UPDATE alerting.rule_state s SET revision=s.revision+1 WHERE s.instance_id=p_target_id AND s.rule_id=current_state.rule_id;
 INSERT INTO alerting.assignment_operation(operation_id,instance_id,alert_id,episode_id,action_name,actor_sid,
  request_digest,audit_id,applied_at,result_revision,assignee_sid,assignee_name,previous_assignee_sid,previous_assignee_name,reason)
 VALUES(op,p_target_id,p_alert_id,p_expected_episode_id,action,p_actor_sid,digest,a,t,current_state.revision+1,
  p_assignee_sid,owner_name,prior_assignment.assignee_sid,prior_assignment.assignee_name,p_reason);
 INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,
  authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
 VALUES(t,a,'user',p_actor_sid,action,'allowed','succeeded','alerting',p_target_id::text,p_correlation_id,
  jsonb_build_object('operationId',op,'targetId',p_target_id,'alertId',p_alert_id,'episodeId',p_expected_episode_id,
   'revision',current_state.revision+1,'assigneeSid',p_assignee_sid,'previousAssigneeSid',prior_assignment.assignee_sid,'reason',p_reason));
 RETURN QUERY SELECT a,t,current_state.revision+1,p_assignee_sid,t;
END $$;

CREATE FUNCTION alerting.list_assignment_history(p_target_id uuid,p_alert_id uuid,p_max_results integer,p_before_operation_id uuid)
RETURNS TABLE(operation_id uuid,episode_id uuid,revision bigint,assignee_sid text,assignee_name text,
 previous_assignee_sid text,previous_assignee_name text,reason text,actor_sid text,applied_at timestamptz)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE before_sequence bigint;
BEGIN
 IF p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_alert_id IS NULL OR p_alert_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 50
  OR p_before_operation_id='00000000-0000-0000-0000-000000000000'::uuid THEN
  RAISE EXCEPTION 'assignment history bounds invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN
  RAISE EXCEPTION 'assignment history target scope required' USING ERRCODE='42501'; END IF;
 IF p_before_operation_id IS NOT NULL THEN
  SELECT x.change_sequence INTO before_sequence FROM alerting.assignment_operation x
   WHERE x.operation_id=p_before_operation_id AND x.instance_id=p_target_id AND x.alert_id=p_alert_id;
  IF NOT FOUND THEN RAISE EXCEPTION 'assignment history anchor not found' USING ERRCODE='P0002'; END IF;
 END IF;
 RETURN QUERY SELECT x.operation_id,x.episode_id,x.result_revision,x.assignee_sid,x.assignee_name,
  x.previous_assignee_sid,x.previous_assignee_name,x.reason,x.actor_sid,x.applied_at
 FROM alerting.assignment_operation x WHERE x.instance_id=p_target_id AND x.alert_id=p_alert_id
  AND (before_sequence IS NULL OR x.change_sequence<before_sequence)
 ORDER BY x.change_sequence DESC LIMIT p_max_results+1;
END $$;
REVOKE ALL ON FUNCTION alerting.get_episode_assignment(uuid,uuid),
 alerting.set_episode_assignment(uuid,uuid,uuid,bigint,text,text,boolean,text,text,text,uuid,text),
 alerting.list_assignment_history(uuid,uuid,integer,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.get_episode_assignment(uuid,uuid),
 alerting.set_episode_assignment(uuid,uuid,uuid,bigint,text,text,boolean,text,text,text,uuid,text),
 alerting.list_assignment_history(uuid,uuid,integer,uuid) TO sqlobserver_server;

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
     OR p_action NOT IN ('alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.snooze','alert.unsnooze','alert.assign','alert.unassign','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
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
     OR p_action NOT IN ('register_observation_target','update_observation_target','retire_observation_target','request_capability_rediscovery','alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.snooze','alert.unsnooze','alert.assign','alert.unassign','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
     OR p_reason NOT IN ('principal_disabled','required_role_missing','target_out_of_scope','already_exists','revision_conflict','target_not_found','target_retired','discovery_failed','repository_failure','invalid_request','idempotent_replay') THEN
    RAISE EXCEPTION 'invalid M8 denial audit fields' USING ERRCODE='22023';
  END IF;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
  VALUES(captured_repository_time,generated_activity_id,'user',p_actor_identifier,CASE p_action WHEN 'register_observation_target' THEN 'observation_target.register' WHEN 'update_observation_target' THEN 'observation_target.update' WHEN 'retire_observation_target' THEN 'observation_target.retire' WHEN 'request_capability_rediscovery' THEN 'capability.rediscovery_request' ELSE p_action END,'denied','rejected',CASE WHEN p_action LIKE 'alert.%' THEN 'alerting' ELSE 'observation_target' END,p_target_id::text,p_correlation_id,jsonb_build_object('reason',p_reason));
  RETURN QUERY SELECT generated_activity_id,captured_repository_time;
END $$;
