-- Retained lifecycle reads preserve source identity and never rewrite decisions.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION alerting.lifecycle_event_uuid(p_source_key text) RETURNS uuid
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT (substr(h,1,12)||'5'||substr(h,14,3)||'8'||substr(h,18,15))::uuid
 FROM (SELECT encode(sha256(convert_to('alert-lifecycle-v1|'||p_source_key,'UTF8')),'hex') h) d
$$;
CREATE FUNCTION alerting.lifecycle_state_name(p_state smallint) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT CASE p_state WHEN 1 THEN 'normal' WHEN 2 THEN 'pending' WHEN 3 THEN 'firing' WHEN 4 THEN 'acknowledged' WHEN 5 THEN 'resolved' END
$$;
CREATE TABLE alerting.lifecycle_capture_boundary(singleton boolean PRIMARY KEY CHECK(singleton),started_at timestamptz NOT NULL);
INSERT INTO alerting.lifecycle_capture_boundary VALUES(true,clock_timestamp());
ALTER TABLE alerting.lifecycle_capture_boundary ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.lifecycle_capture_boundary FORCE ROW LEVEL SECURITY;
CREATE POLICY lifecycle_boundary_owner ON alerting.lifecycle_capture_boundary FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TABLE alerting.lifecycle_provenance(
 source_key text PRIMARY KEY,event_id uuid NOT NULL UNIQUE,
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 event_at timestamptz NOT NULL,recorded_at timestamptz,payload jsonb NOT NULL,
 CHECK(isfinite(event_at) AND (recorded_at IS NULL OR isfinite(recorded_at))),
 CHECK(octet_length(payload::text)<=32768)
);
ALTER TABLE alerting.lifecycle_provenance ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.lifecycle_provenance FORCE ROW LEVEL SECURITY;
CREATE POLICY lifecycle_provenance_scope ON alerting.lifecycle_provenance USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY lifecycle_provenance_owner ON alerting.lifecycle_provenance FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER lifecycle_provenance_append_only BEFORE UPDATE OR DELETE ON alerting.lifecycle_provenance FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE INDEX ix_lifecycle_provenance_window ON alerting.lifecycle_provenance(instance_id,event_at,event_id);
CREATE INDEX ix_lifecycle_history_window ON alerting.state_history(instance_id,observed_at,history_id)
 WHERE alert_id IS NOT NULL AND from_state IS DISTINCT FROM to_state AND to_state IN(3,4,5);
CREATE INDEX ix_lifecycle_history_cycle ON alerting.state_history(instance_id,alert_id,observed_at,history_id);
CREATE INDEX ix_lifecycle_history_event ON alerting.state_history(instance_id,alerting.lifecycle_event_uuid('history:'||history_id::text));
CREATE INDEX ix_lifecycle_snooze_window ON alerting.snooze_operation(instance_id,applied_at,operation_id);
CREATE INDEX ix_lifecycle_snooze_event ON alerting.snooze_operation(instance_id,alerting.lifecycle_event_uuid('snooze:'||operation_id::text));
CREATE INDEX ix_lifecycle_assignment_window ON alerting.assignment_operation(instance_id,applied_at,operation_id);
CREATE INDEX ix_lifecycle_assignment_event ON alerting.assignment_operation(instance_id,alerting.lifecycle_event_uuid('assignment:'||operation_id::text));
REVOKE ALL ON alerting.lifecycle_provenance,alerting.lifecycle_capture_boundary FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION alerting.lifecycle_history_payload(p_history_id bigint,p_forward boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $$
DECLARE h alerting.state_history%ROWTYPE; e alerting.evaluation_replay%ROWTYPE;
 s alerting.rule_state%ROWTYPE; v_rule_name text; v_recorded timestamptz; v_episode uuid; v_revision bigint;
 v_actor text; v_kind text; v_source text; v_target_revision bigint; v_value double precision; v_complete boolean; v_audit uuid;
BEGIN
 SELECT x.* INTO h FROM alerting.state_history x WHERE x.history_id=p_history_id;
 IF NOT FOUND OR h.alert_id IS NULL OR h.from_state IS NOT DISTINCT FROM h.to_state THEN RETURN NULL; END IF;
 v_kind:=CASE WHEN h.to_state=3 AND coalesce(h.from_state,1) NOT IN(3,4) THEN 'fired'
  WHEN h.to_state=4 AND h.from_state=3 THEN 'acknowledged' WHEN h.to_state=5 AND h.from_state IN(3,4) THEN 'resolved' END;
 IF v_kind IS NULL THEN RETURN NULL; END IF;
 SELECT x.* INTO e FROM alerting.evaluation_replay x WHERE x.operation_id=h.operation_id AND x.instance_id=h.instance_id AND x.rule_id=h.rule_id;
 v_source:=CASE WHEN h.reason IN('acknowledged','rule_disabled') THEN 'administrative' ELSE 'evaluation' END;
 IF v_source='evaluation' AND e.operation_id IS NOT NULL AND e.result->'State'->>'AlertId'=h.alert_id::text
   AND e.result->'State'->>'State'=h.to_state::text AND e.result_digest=sha256(convert_to(e.result::text,'UTF8')) THEN
  v_episode:=(e.result->'State'->>'EpisodeId')::uuid;v_revision:=(e.result->'State'->>'Revision')::bigint;
  v_rule_name:=e.rule_binding->>'name';v_recorded:=e.recorded_at;
  v_value:=(e.result->'State'->>'LastValue')::double precision;
  IF coalesce(e.result->'Observation'->>'Reason','') ~ '^[^|]+[|]target=[1-9][0-9]{0,15}[|]' THEN
   v_target_revision:=substring(split_part(e.result->'Observation'->>'Reason','|',2) FROM 8)::bigint;
  ELSE
   SELECT r.target_revision INTO v_target_revision FROM telemetry.collection_run_identity i
    JOIN telemetry.collection_run_v2 r ON r.run_id=i.run_id AND r.started_at=i.started_at
    WHERE i.run_id=(e.result->'Observation'->>'RunId')::uuid AND r.instance_id=h.instance_id;
  END IF;
 ELSIF v_source='administrative' THEN
  SELECT i.recorded_at,i.audit_id INTO v_recorded,v_audit FROM alerting.admin_idempotency i
   WHERE i.operation_id=h.operation_id AND i.target_scope=h.instance_id
    AND i.recorded_at=h.observed_at
    AND i.action_name=CASE WHEN h.reason='acknowledged' THEN 'acknowledge' ELSE 'upsert_rule' END;
  SELECT a.actor_identifier INTO v_actor FROM audit.activity a WHERE a.activity_id=v_audit AND a.outcome='succeeded' AND a.authorization_result='allowed';
  IF p_forward THEN
   SELECT x.* INTO s FROM alerting.rule_state x WHERE x.instance_id=h.instance_id AND x.rule_id=h.rule_id AND x.alert_id=h.alert_id;
   IF FOUND THEN v_episode:=s.alert_episode_id;v_revision:=s.revision;v_value:=s.last_value; END IF;
   SELECT r.name INTO v_rule_name FROM alerting.rule r WHERE r.instance_id=h.instance_id AND r.rule_id=h.rule_id;
  END IF;
 END IF;
 IF v_target_revision NOT BETWEEN 1 AND 9007199254740991 THEN v_target_revision:=NULL; END IF;
 v_complete:=v_episode IS NOT NULL AND v_revision IS NOT NULL AND v_rule_name IS NOT NULL AND v_recorded IS NOT NULL;
 RETURN jsonb_build_object('eventId',alerting.lifecycle_event_uuid('history:'||h.history_id::text),'alertId',h.alert_id,
  'episodeId',v_episode,'ruleId',h.rule_id,'kind',v_kind,'timeBasis',CASE WHEN v_source='evaluation' THEN 'observed' ELSE 'applied' END,
  'eventAtUtc',h.observed_at,'recordedAtUtc',v_recorded,'source',v_source,'provenance',CASE WHEN v_complete THEN 'complete' ELSE 'legacy_partial' END,
  'sourceTargetRevision',v_target_revision,'stateRevision',v_revision,'ruleName',v_rule_name,
  'fromState',alerting.lifecycle_state_name(h.from_state),'toState',alerting.lifecycle_state_name(h.to_state),
  'value',v_value,'reason',h.reason,'actorSid',v_actor,'deliverySuppressed',h.delivery_suppressed,
  'snoozedUntilUtc',NULL,'assigneeSid',NULL,'assigneeName',NULL,'previousAssigneeSid',NULL,'previousAssigneeName',NULL);
END $$;

CREATE FUNCTION alerting.lifecycle_operation_payload(p_source text,p_operation uuid,p_forward boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $$
DECLARE v_op jsonb;v_reason text;v_rule uuid;v_name text;v_state smallint;v_complete boolean;v_kind text;v_target uuid;v_alert uuid;v_episode uuid;
BEGIN
 IF p_source='snooze' THEN SELECT to_jsonb(x) INTO v_op FROM alerting.snooze_operation x WHERE x.operation_id=p_operation;
 ELSIF p_source='assignment' THEN SELECT to_jsonb(x) INTO v_op FROM alerting.assignment_operation x WHERE x.operation_id=p_operation;
 ELSE RAISE EXCEPTION 'lifecycle source invalid' USING ERRCODE='22023'; END IF;
 IF v_op IS NULL THEN RETURN NULL; END IF;
 v_target:=(v_op->>'instance_id')::uuid;v_alert:=(v_op->>'alert_id')::uuid;v_episode:=(v_op->>'episode_id')::uuid;
 IF p_source='snooze' THEN
  SELECT a.safe_details->>'reason' INTO v_reason FROM audit.activity a WHERE a.activity_id=(v_op->>'audit_id')::uuid;
  IF v_reason IS NULL AND p_forward THEN SELECT x.reason INTO v_reason FROM alerting.episode_snooze x
   WHERE x.instance_id=v_target AND x.alert_id=v_alert AND x.episode_id=v_episode AND x.operation_id=p_operation; END IF;
  v_kind:=CASE WHEN v_op->>'ends_at' IS NULL THEN 'unsnoozed' ELSE 'snoozed' END;
 ELSE
  v_reason:=v_op->>'reason';v_kind:=CASE WHEN v_op->>'assignee_sid' IS NULL THEN 'unassigned'
    WHEN v_op->>'previous_assignee_sid' IS NULL THEN 'assigned' ELSE 'reassigned' END;
 END IF;
 IF p_forward THEN
  SELECT s.rule_id,s.state,r.name INTO v_rule,v_state,v_name FROM alerting.rule_state s JOIN alerting.rule r USING(instance_id,rule_id)
   WHERE s.instance_id=v_target AND s.alert_id=v_alert AND s.alert_episode_id=v_episode;
 ELSE SELECT h.rule_id INTO v_rule FROM alerting.state_history h WHERE h.instance_id=v_target AND h.alert_id=v_alert ORDER BY h.observed_at,h.history_id LIMIT 1;
 END IF;
 v_complete:=p_forward AND v_rule IS NOT NULL AND v_name IS NOT NULL;
 RETURN jsonb_build_object('eventId',alerting.lifecycle_event_uuid(p_source||':'||p_operation::text),'alertId',v_alert,'episodeId',v_episode,
  'ruleId',v_rule,'kind',v_kind,'timeBasis','applied','eventAtUtc',v_op->'applied_at','recordedAtUtc',v_op->'applied_at','source',p_source,
  'provenance',CASE WHEN v_complete THEN 'complete' ELSE 'legacy_partial' END,'sourceTargetRevision',NULL,
  'stateRevision',v_op->'result_revision','ruleName',v_name,'fromState',alerting.lifecycle_state_name(v_state),'toState',alerting.lifecycle_state_name(v_state),
  'value',NULL,'reason',v_reason,'actorSid',v_op->'actor_sid','deliverySuppressed',NULL,'snoozedUntilUtc',v_op->'ends_at',
  'assigneeSid',v_op->'assignee_sid','assigneeName',v_op->'assignee_name',
  'previousAssigneeSid',v_op->'previous_assignee_sid','previousAssigneeName',v_op->'previous_assignee_name');
END $$;

CREATE FUNCTION alerting.capture_lifecycle_provenance() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $$
DECLARE v_history bigint;v_key text;v_payload jsonb;v_target uuid;
BEGIN
 IF TG_TABLE_NAME='evaluation_replay' THEN
  SELECT h.history_id INTO v_history FROM alerting.state_history h WHERE h.instance_id=NEW.instance_id AND h.rule_id=NEW.rule_id AND h.operation_id=NEW.operation_id;
  IF v_history IS NOT NULL THEN v_key:='history:'||v_history::text;v_payload:=alerting.lifecycle_history_payload(v_history,true); END IF;
 ELSIF TG_TABLE_NAME='activity' THEN
  IF NEW.outcome<>'succeeded' OR NEW.authorization_result<>'allowed' OR NEW.action_name NOT IN('alert.acknowledge','alert.rule.retire') THEN RETURN NEW; END IF;
  SELECT h.history_id INTO v_history FROM alerting.admin_idempotency i JOIN alerting.state_history h
   ON h.operation_id=i.operation_id AND h.instance_id=i.target_scope
   WHERE i.audit_id=NEW.activity_id AND ((i.action_name='acknowledge' AND h.reason='acknowledged') OR (i.action_name='upsert_rule' AND h.reason='rule_disabled'));
  IF v_history IS NOT NULL THEN v_key:='history:'||v_history::text;v_payload:=alerting.lifecycle_history_payload(v_history,true); END IF;
 ELSE
  v_key:=CASE WHEN TG_TABLE_NAME='snooze_operation' THEN 'snooze' ELSE 'assignment' END;
  v_payload:=alerting.lifecycle_operation_payload(v_key,NEW.operation_id,true);v_key:=v_key||':'||NEW.operation_id::text;
 END IF;
 IF v_payload IS NOT NULL THEN
  IF TG_TABLE_NAME='activity' THEN SELECT h.instance_id INTO v_target FROM alerting.state_history h WHERE h.history_id=v_history;
  ELSE v_target:=NEW.instance_id; END IF;
  INSERT INTO alerting.lifecycle_provenance(source_key,event_id,instance_id,event_at,recorded_at,payload)
   VALUES(v_key,(v_payload->>'eventId')::uuid,v_target,(v_payload->>'eventAtUtc')::timestamptz,(v_payload->>'recordedAtUtc')::timestamptz,v_payload)
   ON CONFLICT(source_key) DO NOTHING;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER lifecycle_capture_evaluation AFTER INSERT ON alerting.evaluation_replay FOR EACH ROW EXECUTE FUNCTION alerting.capture_lifecycle_provenance();
CREATE TRIGGER lifecycle_capture_snooze AFTER INSERT ON alerting.snooze_operation FOR EACH ROW EXECUTE FUNCTION alerting.capture_lifecycle_provenance();
CREATE TRIGGER lifecycle_capture_assignment AFTER INSERT ON alerting.assignment_operation FOR EACH ROW EXECUTE FUNCTION alerting.capture_lifecycle_provenance();
CREATE TRIGGER lifecycle_capture_admin AFTER INSERT ON audit.activity FOR EACH ROW EXECUTE FUNCTION alerting.capture_lifecycle_provenance();

CREATE FUNCTION reporting.alert_lifecycle_sources(p_target uuid,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz,p_event uuid DEFAULT NULL)
RETURNS TABLE(source_key text,event_id uuid,event_at timestamptz,payload jsonb)
LANGUAGE sql SECURITY DEFINER STABLE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $$
 WITH candidates AS(
  (SELECT x.source_key,x.event_id,x.event_at,x.payload FROM alerting.lifecycle_provenance x
   WHERE x.instance_id=p_target AND x.event_at>=p_from AND x.event_at<p_to AND (x.recorded_at IS NULL OR x.recorded_at<=p_cutoff)
    AND (p_event IS NULL OR x.event_id=p_event) ORDER BY x.event_at,x.event_id LIMIT CASE WHEN p_event IS NULL THEN 10001 END)
  UNION ALL
  (SELECT 'history:'||h.history_id::text,alerting.lifecycle_event_uuid('history:'||h.history_id::text),h.observed_at,
    normalized.payload FROM alerting.state_history h
   CROSS JOIN LATERAL(SELECT alerting.lifecycle_history_payload(h.history_id,false) payload OFFSET 0) normalized
   WHERE h.instance_id=p_target AND h.observed_at>=p_from AND h.observed_at<p_to AND h.alert_id IS NOT NULL
    AND h.from_state IS DISTINCT FROM h.to_state AND ((h.to_state=3 AND coalesce(h.from_state,1) NOT IN(3,4)) OR(h.to_state=4 AND h.from_state=3) OR(h.to_state=5 AND h.from_state IN(3,4)))
    AND (p_event IS NULL OR alerting.lifecycle_event_uuid('history:'||h.history_id::text)=p_event)
    AND NOT EXISTS(SELECT 1 FROM alerting.lifecycle_provenance x WHERE x.source_key='history:'||h.history_id::text)
    AND normalized.payload IS NOT NULL AND ((normalized.payload->>'recordedAtUtc')::timestamptz IS NULL OR (normalized.payload->>'recordedAtUtc')::timestamptz<=p_cutoff)
   ORDER BY h.observed_at,h.history_id LIMIT CASE WHEN p_event IS NULL THEN 10001 END)
  UNION ALL
  (SELECT 'snooze:'||x.operation_id::text,alerting.lifecycle_event_uuid('snooze:'||x.operation_id::text),x.applied_at,
    alerting.lifecycle_operation_payload('snooze',x.operation_id,false) FROM alerting.snooze_operation x
   WHERE x.instance_id=p_target AND x.applied_at>=p_from AND x.applied_at<p_to AND x.applied_at<=p_cutoff
    AND (p_event IS NULL OR alerting.lifecycle_event_uuid('snooze:'||x.operation_id::text)=p_event)
    AND NOT EXISTS(SELECT 1 FROM alerting.lifecycle_provenance p WHERE p.source_key='snooze:'||x.operation_id::text)
   ORDER BY x.applied_at,x.operation_id LIMIT CASE WHEN p_event IS NULL THEN 10001 END)
  UNION ALL
  (SELECT 'assignment:'||x.operation_id::text,alerting.lifecycle_event_uuid('assignment:'||x.operation_id::text),x.applied_at,
    alerting.lifecycle_operation_payload('assignment',x.operation_id,false) FROM alerting.assignment_operation x
   WHERE x.instance_id=p_target AND x.applied_at>=p_from AND x.applied_at<p_to AND x.applied_at<=p_cutoff
    AND (p_event IS NULL OR alerting.lifecycle_event_uuid('assignment:'||x.operation_id::text)=p_event)
    AND NOT EXISTS(SELECT 1 FROM alerting.lifecycle_provenance p WHERE p.source_key='assignment:'||x.operation_id::text)
   ORDER BY x.applied_at,x.operation_id LIMIT CASE WHEN p_event IS NULL THEN 10001 END)
 ) SELECT c.source_key,c.event_id,c.event_at,c.payload FROM candidates c WHERE c.payload IS NOT NULL
  AND ((c.payload->>'recordedAtUtc')::timestamptz IS NULL OR (c.payload->>'recordedAtUtc')::timestamptz<=p_cutoff)
$$;

CREATE FUNCTION reporting.read_alert_lifecycle(p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_limit integer,p_cursor jsonb,
 p_event uuid DEFAULT NULL,p_alert uuid DEFAULT NULL,p_episode uuid DEFAULT NULL,p_event_at timestamptz DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $$
DECLARE v_now timestamptz:=clock_timestamp();v_cutoff timestamptz;v_issued timestamptz;v_current bigint;v_all jsonb;v_items jsonb;v_next jsonb;
 v_epoch text;v_count integer;v_complete integer;v_cycles integer;v_boundary timestamptz;v_last jsonb;v_selected jsonb;v_after_at timestamptz;v_after_id uuid;
BEGIN
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target::text THEN RAISE EXCEPTION 'lifecycle target scope required' USING ERRCODE='42501'; END IF;
 IF p_target IS NULL OR p_target='00000000-0000-0000-0000-000000000000' OR p_revision NOT BETWEEN 1 AND 9007199254740991
  OR p_revision IS NULL OR p_from IS NULL OR p_to IS NULL OR NOT isfinite(p_from) OR NOT isfinite(p_to) OR p_from>=p_to OR p_to-p_from>interval '31 days'
  OR (p_event IS NULL AND (p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR p_alert IS NOT NULL OR p_episode IS NOT NULL OR p_event_at IS NOT NULL))
  OR (p_event IS NOT NULL AND (p_event='00000000-0000-0000-0000-000000000000' OR p_alert IS NULL OR p_alert='00000000-0000-0000-0000-000000000000'
    OR p_episode='00000000-0000-0000-0000-000000000000' OR p_event_at IS NULL OR NOT isfinite(p_event_at) OR p_event_at<p_from OR p_event_at>=p_to OR p_cursor IS NOT NULL))
 THEN RAISE EXCEPTION 'lifecycle request bounds invalid' USING ERRCODE='22023'; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN RAISE EXCEPTION 'lifecycle requires repeatable read' USING ERRCODE='55000'; END IF;
 SELECT t.revision INTO v_current FROM control.observation_target t WHERE t.instance_id=p_target;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF v_current<>p_revision THEN RAISE EXCEPTION 'lifecycle target revision changed' USING ERRCODE='40001'; END IF;
 v_cutoff:=v_now;v_issued:=v_now;
 IF p_cursor IS NOT NULL THEN
  IF jsonb_typeof(p_cursor) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_cursor))<>10
    OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_cursor) k WHERE k NOT IN('targetId','targetRevision','fromUtc','toUtc','cutoffUtc','issuedAtUtc','readEpoch','limit','afterEventAtUtc','afterEventId'))
    OR (p_cursor->>'targetId')::uuid IS DISTINCT FROM p_target OR (p_cursor->>'targetRevision')::bigint IS DISTINCT FROM p_revision
    OR (p_cursor->>'fromUtc')::timestamptz IS DISTINCT FROM p_from OR (p_cursor->>'toUtc')::timestamptz IS DISTINCT FROM p_to
    OR (p_cursor->>'limit')::integer IS DISTINCT FROM p_limit OR coalesce(p_cursor->>'readEpoch','') !~ '^[0-9a-f]{64}$'
  THEN RAISE EXCEPTION 'lifecycle cursor context invalid' USING ERRCODE='22023'; END IF;
  v_cutoff:=(p_cursor->>'cutoffUtc')::timestamptz;v_issued:=(p_cursor->>'issuedAtUtc')::timestamptz;
  v_after_at:=(p_cursor->>'afterEventAtUtc')::timestamptz;v_after_id:=(p_cursor->>'afterEventId')::uuid;
  IF v_cutoff IS NULL OR v_issued IS NULL OR NOT isfinite(v_cutoff) OR NOT isfinite(v_issued) OR v_cutoff>v_issued OR v_issued>v_now
    OR v_after_at IS NULL OR NOT isfinite(v_after_at) OR v_after_at<p_from OR v_after_at>=p_to OR v_after_id IS NULL OR v_after_id='00000000-0000-0000-0000-000000000000'
  THEN RAISE EXCEPTION 'lifecycle cursor timing invalid' USING ERRCODE='22023'; END IF;
  IF v_now>=v_issued+interval '15 minutes' THEN RAISE EXCEPTION 'lifecycle cursor expired' USING ERRCODE='40001'; END IF;
 END IF;
 IF p_event IS NOT NULL THEN
  SELECT s.payload INTO v_selected FROM reporting.alert_lifecycle_sources(p_target,p_event_at,p_event_at+interval '1 microsecond',v_cutoff,p_event) s
   WHERE s.event_at=p_event_at AND (s.payload->>'alertId')::uuid=p_alert AND (s.payload->>'episodeId')::uuid IS NOT DISTINCT FROM p_episode;
  v_epoch:=encode(sha256(convert_to(jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'event',v_selected)::text,'UTF8')),'hex');
  RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'readEpoch',v_epoch,'repositoryTimeUtc',v_now,
   'state',CASE WHEN v_selected IS NULL THEN 'unavailable' WHEN v_selected->>'provenance'='complete' THEN 'available' ELSE 'partial' END,
   'reason',CASE WHEN v_selected IS NULL THEN 'event_not_retained' WHEN v_selected->>'provenance'='complete' THEN 'retained_lifecycle' ELSE 'legacy_provenance' END,'event',v_selected);
 END IF;
 SELECT count(*)::integer,coalesce(jsonb_agg(s.payload ORDER BY s.event_at,s.event_id),'[]'::jsonb)
  INTO v_count,v_all FROM(SELECT * FROM reporting.alert_lifecycle_sources(p_target,p_from,p_to,v_cutoff) ORDER BY event_at,event_id LIMIT 10001) s;
 IF v_count>10000 THEN RAISE EXCEPTION 'lifecycle window exceeds event bound' USING ERRCODE='54000'; END IF;
 SELECT count(*)::integer INTO v_cycles FROM(SELECT DISTINCT e->>'alertId',e->>'episodeId' FROM jsonb_array_elements(v_all) e LIMIT 1001) c;
 IF v_cycles>1000 THEN RAISE EXCEPTION 'lifecycle window exceeds cycle bound' USING ERRCODE='54000'; END IF;
 SELECT count(*)::integer INTO v_complete FROM jsonb_array_elements(v_all) e WHERE e->>'provenance'='complete';
 SELECT b.started_at INTO v_boundary FROM alerting.lifecycle_capture_boundary b WHERE b.singleton;
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('contract','alert-lifecycle-v1','targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'events',v_all,'boundary',v_boundary)::text,'UTF8')),'hex');
 IF p_cursor IS NOT NULL AND v_epoch IS DISTINCT FROM p_cursor->>'readEpoch' THEN RAISE EXCEPTION 'lifecycle manifest changed' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v_all) e WHERE (e->>'eventAtUtc')::timestamptz=v_after_at AND (e->>'eventId')::uuid=v_after_id)
 THEN RAISE EXCEPTION 'lifecycle cursor anchor invalid' USING ERRCODE='22023'; END IF;
 SELECT coalesce(jsonb_agg(e ORDER BY (e->>'eventAtUtc')::timestamptz,(e->>'eventId')::uuid),'[]'::jsonb) INTO v_items
 FROM(SELECT e FROM jsonb_array_elements(v_all) e WHERE v_after_at IS NULL OR ((e->>'eventAtUtc')::timestamptz,(e->>'eventId')::uuid)>(v_after_at,v_after_id)
  ORDER BY (e->>'eventAtUtc')::timestamptz,(e->>'eventId')::uuid LIMIT p_limit+1) page;
 IF jsonb_array_length(v_items)>p_limit THEN
  v_items:=v_items-p_limit;v_last:=v_items->(p_limit-1);
  v_next:=jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'issuedAtUtc',v_issued,'readEpoch',v_epoch,'limit',p_limit,'afterEventAtUtc',v_last->'eventAtUtc','afterEventId',v_last->'eventId');
 END IF;
 RETURN jsonb_build_object('targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',v_cutoff,'readEpoch',v_epoch,'repositoryTimeUtc',v_now,
  'state',CASE WHEN v_count=0 THEN 'unavailable' WHEN v_complete=v_count THEN 'available' ELSE 'partial' END,
  'reason',CASE WHEN v_count=0 THEN 'no_lifecycle_events' WHEN v_complete=v_count THEN 'retained_lifecycle' ELSE 'legacy_provenance' END,
  'coverage',jsonb_build_object('eventCount',v_count,'completeEventCount',v_complete,'legacyPartialEventCount',v_count-v_complete,'cycleCount',v_cycles,'provenanceBoundaryUtc',v_boundary,'retentionDays',NULL),'items',v_items,'nextAnchor',v_next);
END $$;
REVOKE ALL ON FUNCTION alerting.lifecycle_event_uuid(text),alerting.lifecycle_state_name(smallint),alerting.lifecycle_history_payload(bigint,boolean),alerting.lifecycle_operation_payload(text,uuid,boolean),alerting.capture_lifecycle_provenance(),reporting.alert_lifecycle_sources(uuid,timestamptz,timestamptz,timestamptz,uuid),reporting.read_alert_lifecycle(uuid,bigint,timestamptz,timestamptz,integer,jsonb,uuid,uuid,uuid,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_alert_lifecycle(uuid,bigint,timestamptz,timestamptz,integer,jsonb,uuid,uuid,uuid,timestamptz) TO sqlobserver_server;
