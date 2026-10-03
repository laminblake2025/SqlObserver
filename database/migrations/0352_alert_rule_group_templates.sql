-- Immutable library versions, complete authorized cohorts and explicit bounded batches.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE alerting.rule_template(
 template_id uuid PRIMARY KEY CHECK(template_id<>'00000000-0000-0000-0000-000000000000'),
 owner_sid text NOT NULL,current_version bigint NOT NULL CHECK(current_version BETWEEN 1 AND 9007199254740991));
CREATE TABLE alerting.rule_template_version(
 template_id uuid NOT NULL REFERENCES alerting.rule_template,version bigint NOT NULL CHECK(version BETWEEN 1 AND 9007199254740991),
 payload jsonb NOT NULL,version_digest text NOT NULL CHECK(version_digest ~ '^[0-9a-f]{64}$'),actor_sid text NOT NULL,
 operation_id uuid NOT NULL UNIQUE,audit_id uuid NOT NULL UNIQUE,created_at timestamptz NOT NULL,
 PRIMARY KEY(template_id,version));
CREATE TABLE alerting.rule_deployment(
 deployment_id uuid PRIMARY KEY CHECK(deployment_id<>'00000000-0000-0000-0000-000000000000'),revision bigint NOT NULL DEFAULT 1 CHECK(revision BETWEEN 1 AND 9007199254740991),
 actor_sid text NOT NULL,template_id uuid NOT NULL,template_version bigint NOT NULL,group_selector jsonb,
 mode text NOT NULL CHECK(mode IN ('create_missing','replace_named','rollback')),source_deployment_id uuid REFERENCES alerting.rule_deployment,
 preview_at timestamptz NOT NULL,expires_at timestamptz NOT NULL,sealed boolean NOT NULL DEFAULT false,reason text NOT NULL,
 FOREIGN KEY(template_id,template_version) REFERENCES alerting.rule_template_version);
CREATE TABLE alerting.rule_deployment_member(
 deployment_id uuid NOT NULL REFERENCES alerting.rule_deployment,instance_id uuid NOT NULL REFERENCES control.observation_target,
 manifest jsonb NOT NULL,status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','skipped','conflict','failed','stopped')),
 result_revision bigint,result_audit_id uuid,result_at timestamptz,result_code text,
 result_sqlstate text CHECK(result_sqlstate IS NULL OR result_sqlstate ~ '^[0-9A-Z]{5}$'),
 PRIMARY KEY(deployment_id,instance_id));
CREATE TABLE alerting.rule_template_binding(
 instance_id uuid NOT NULL,rule_id uuid NOT NULL,template_id uuid NOT NULL,template_version bigint NOT NULL,
 deployment_id uuid NOT NULL REFERENCES alerting.rule_deployment,rule_revision bigint NOT NULL,configuration jsonb NOT NULL,
 PRIMARY KEY(instance_id,rule_id),FOREIGN KEY(instance_id,rule_id) REFERENCES alerting.rule(instance_id,rule_id),
 FOREIGN KEY(template_id,template_version) REFERENCES alerting.rule_template_version);
CREATE TABLE alerting.rule_template_activity(
 audit_id uuid PRIMARY KEY,operation_id uuid NOT NULL UNIQUE,actor_sid text NOT NULL,action_name text NOT NULL,
 subject_id uuid NOT NULL,correlation_id uuid NOT NULL,recorded_at timestamptz NOT NULL,safe_details jsonb NOT NULL);
CREATE TABLE alerting.rule_template_operation(
 operation_id uuid PRIMARY KEY,change_sequence bigserial NOT NULL UNIQUE,actor_sid text NOT NULL,action_name text NOT NULL,
 subject_id uuid NOT NULL,request_digest bytea NOT NULL CHECK(octet_length(request_digest)=32),receipt jsonb NOT NULL,
 recorded_at timestamptz NOT NULL);
CREATE INDEX ix_rule_deployment_history ON alerting.rule_template_operation(subject_id,change_sequence DESC);

DO $permissions$
DECLARE name text;
BEGIN
 FOREACH name IN ARRAY ARRAY['rule_template','rule_template_version','rule_deployment','rule_deployment_member','rule_template_binding','rule_template_activity','rule_template_operation'] LOOP
  EXECUTE format('ALTER TABLE alerting.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE alerting.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON alerting.%I FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true)',name);
  EXECUTE format('REVOKE ALL ON alerting.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',name);
 END LOOP;
END $permissions$;
CREATE POLICY member_target_scope ON alerting.rule_deployment_member USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
REVOKE ALL ON SEQUENCE alerting.rule_template_operation_change_sequence_seq FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION alerting.rule_template_immutable() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'template provenance is immutable' USING ERRCODE='55000'; END $$;
CREATE TRIGGER template_version_immutable BEFORE UPDATE OR DELETE ON alerting.rule_template_version FOR EACH ROW EXECUTE FUNCTION alerting.rule_template_immutable();
CREATE TRIGGER template_operation_immutable BEFORE UPDATE OR DELETE ON alerting.rule_template_operation FOR EACH ROW EXECUTE FUNCTION alerting.rule_template_immutable();
CREATE TRIGGER template_activity_immutable BEFORE UPDATE OR DELETE ON alerting.rule_template_activity FOR EACH ROW EXECUTE FUNCTION alerting.rule_template_immutable();
CREATE TRIGGER deployment_manifest_immutable BEFORE UPDATE OF manifest OR DELETE ON alerting.rule_deployment_member FOR EACH ROW EXECUTE FUNCTION alerting.rule_template_immutable();

CREATE FUNCTION alerting.rule_template_id(p_text text) RETURNS uuid LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 -- Native rule writers require RFC 4122 version/variant bits for operation IDs.
 -- Match the existing alerting.sha_uuid deterministic SHA-derived convention.
 SELECT (substr(h,1,12)||'5'||substr(h,14,3)||'8'||substr(h,18,15))::uuid FROM(SELECT encode(sha256(convert_to(p_text,'UTF8')),'hex') h) q;
$$;
CREATE FUNCTION alerting.rule_template_safe(p_text text,p_max integer) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT p_text IS NOT NULL AND octet_length(p_text) BETWEEN 1 AND p_max AND btrim(p_text)<>'' AND p_text !~ '[[:cntrl:]]';
$$;
CREATE FUNCTION alerting.rule_template_require(p_command jsonb,p_actor text,p_has_admin boolean) RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT coalesce(p_has_admin,false) THEN RAISE EXCEPTION 'administrator access required' USING ERRCODE='42501'; END IF;
 IF jsonb_typeof(p_command) IS DISTINCT FROM 'object' OR NOT alerting.rule_template_safe(p_actor,256)
   OR NOT alerting.rule_template_safe(p_command->>'reason',512)
   OR coalesce(p_command->>'operationId','') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
   OR (p_command->>'operationId')::uuid='00000000-0000-0000-0000-000000000000'
 THEN RAISE EXCEPTION 'invalid template command' USING ERRCODE='22023'; END IF;
END $$;
CREATE FUNCTION alerting.rule_deployment_authorized(p_id uuid,p_all boolean,p_scope uuid[]) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
 SELECT EXISTS(SELECT 1 FROM alerting.rule_deployment WHERE deployment_id=p_id)
 AND NOT EXISTS(SELECT 1 FROM alerting.rule_deployment_member WHERE deployment_id=p_id AND NOT(coalesce(p_all,false) OR instance_id=ANY(coalesce(p_scope,ARRAY[]::uuid[]))));
$$;
CREATE FUNCTION alerting.rule_deployment_get(p_id uuid,p_all boolean,p_scope uuid[],p_has_admin boolean) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE job alerting.rule_deployment%ROWTYPE;items jsonb;now_at timestamptz:=clock_timestamp();
BEGIN
 IF NOT coalesce(p_has_admin,false) THEN RAISE EXCEPTION 'administrator access required' USING ERRCODE='42501'; END IF;
 SELECT * INTO job FROM alerting.rule_deployment WHERE deployment_id=p_id;IF NOT FOUND THEN RETURN NULL; END IF;
 IF NOT alerting.rule_deployment_authorized(p_id,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;
 SELECT coalesce(jsonb_agg((m.manifest-'beforeBinding'-'restoreBinding')||jsonb_build_object('status',m.status,'resultRevision',m.result_revision,'resultAuditId',m.result_audit_id,'resultAtUtc',m.result_at,'resultCode',m.result_code,
 'currentRuleRevision',r.revision,'currentValues',CASE WHEN r.rule_id IS NULL THEN NULL ELSE alerting.rule_template_values(r) END,
 'matchesApplied',CASE WHEN m.status='applied' THEN coalesce(r.revision=m.result_revision AND alerting.rule_template_values(r)=m.manifest->'desiredValues',false) ELSE NULL END) ORDER BY m.instance_id),'[]'::jsonb)
 INTO items FROM alerting.rule_deployment_member m LEFT JOIN alerting.rule r ON r.instance_id=m.instance_id AND r.rule_id=(m.manifest->>'ruleId')::uuid WHERE m.deployment_id=p_id;
 RETURN jsonb_build_object('deploymentId',p_id,'revision',job.revision,'actorSid',job.actor_sid,'group',job.group_selector,'mode',job.mode,'sourceDeploymentId',job.source_deployment_id,
 'template',alerting.rule_template_get(job.template_id,job.template_version,true),'previewAtUtc',job.preview_at,'expiresAtUtc',job.expires_at,'sealed',job.sealed,
 'pendingCount',(SELECT count(*) FROM alerting.rule_deployment_member WHERE deployment_id=p_id AND status='pending'),'serverNowUtc',now_at,'reason',job.reason,'items',items);
END $$;

CREATE FUNCTION alerting.rule_deployment_preview(p_command jsonb,p_actor text,p_correlation uuid,p_all boolean,p_scope uuid[],p_has_admin boolean,p_approval jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE id uuid:=(p_command->>'deploymentId')::uuid;template uuid:=(p_command->>'templateId')::uuid;v_requested_version bigint:=(p_command->>'templateVersion')::bigint;
 ids uuid[];v alerting.rule_template_version%ROWTYPE;target record;r alerting.rule%ROWTYPE;v_values jsonb;before_values jsonb;before_binding jsonb;decision text;available boolean;rule_id uuid;manifest jsonb;receipt jsonb;now_at timestamptz;a uuid:=gen_random_uuid();
BEGIN
 PERFORM alerting.rule_template_require(p_command,p_actor,p_has_admin);
 IF id IS NULL OR id='00000000-0000-0000-0000-000000000000' OR template IS NULL OR v_requested_version IS NULL OR v_requested_version NOT BETWEEN 1 AND 9007199254740991
 OR p_command->>'mode' NOT IN('create_missing','replace_named') OR p_command->'group'->>'kind' NOT IN('environment','tag')
 OR coalesce(p_command->'group'->>'value','') !~ '^[a-z0-9][a-z0-9._:-]{0,63}$'
 THEN RAISE EXCEPTION 'invalid deployment preview' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('rule-deployment-jobs',0));
 receipt:=alerting.rule_template_replay(p_command,p_actor,'deployment.preview',id);
 IF receipt IS NOT NULL THEN
  IF NOT alerting.rule_deployment_authorized(id,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;RETURN receipt;
 END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN RAISE EXCEPTION 'preview requires a coherent repeatable-read snapshot' USING ERRCODE='22023'; END IF;
 IF EXISTS(SELECT 1 FROM alerting.rule_deployment WHERE deployment_id=id) THEN RAISE EXCEPTION 'deployment already exists' USING ERRCODE='40001'; END IF;
 SELECT array_agg(t.instance_id ORDER BY t.instance_id) INTO ids FROM control.observation_target t JOIN control.observation_target_grouping g USING(instance_id)
 WHERE t.host_name IS NOT NULL AND t.lifecycle_state<>'retired' AND
 CASE WHEN p_command->'group'->>'kind'='environment' THEN g.environment_name=p_command->'group'->>'value' ELSE p_command->'group'->>'value'=ANY(g.tags) END;
 -- Authorization precedes both count disclosure and durable preview writes.
 IF EXISTS(SELECT 1 FROM unnest(ids) t(id) WHERE NOT(coalesce(p_all,false) OR t.id=ANY(coalesce(p_scope,ARRAY[]::uuid[])))) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;
 IF coalesce(cardinality(ids),0) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'group must contain one to one hundred targets' USING ERRCODE='22023'; END IF;
 SELECT * INTO v FROM alerting.rule_template_version tv WHERE tv.template_id=template AND tv.version=(p_command->>'templateVersion')::bigint;
 IF NOT FOUND THEN RAISE EXCEPTION 'template version not found' USING ERRCODE='P0002'; END IF;
 v_values:=v.payload->'values';PERFORM alerting.rule_template_admit(v_values,v.payload->>'sourceTemplateName',v.payload->>'catalogDigest',p_approval);
 now_at:=clock_timestamp();
 INSERT INTO alerting.rule_deployment VALUES(id,1,p_actor,template,v_requested_version,p_command->'group',p_command->>'mode',NULL,now_at,now_at+interval '15 minutes',false,p_command->>'reason');
 FOR target IN SELECT t.instance_id,t.display_name,t.revision,coalesce(g.revision,0) grouping_revision FROM control.observation_target t LEFT JOIN control.observation_target_grouping g USING(instance_id)
 WHERE t.instance_id=ANY(ids) ORDER BY t.instance_id FOR UPDATE OF t LOOP
  SELECT * INTO r FROM alerting.rule WHERE instance_id=target.instance_id AND name=v_values->>'name' FOR UPDATE;
  before_values:=CASE WHEN FOUND THEN alerting.rule_template_values(r) ELSE NULL END;
  SELECT to_jsonb(b) INTO before_binding FROM alerting.rule_template_binding b WHERE b.instance_id=target.instance_id AND b.rule_id=r.rule_id AND b.rule_revision=r.revision AND b.configuration=before_values;
  rule_id:=coalesce(r.rule_id,alerting.rule_template_id(id::text||'|'||target.instance_id::text||'|rule'));
  available:=alerting.rule_template_available(target.instance_id,target.revision,v_values);
  decision:=CASE WHEN before_values IS NOT NULL AND p_command->>'mode'='create_missing' THEN 'skip_existing'
   WHEN before_values IS NOT NULL AND (before_values->>'kind' IS DISTINCT FROM v_values->>'kind' OR before_values->>'metricId' IS DISTINCT FROM v_values->>'metricId') THEN 'incompatible'
   WHEN before_values=v_values THEN 'skip_unchanged' WHEN (v_values->>'enabled')::boolean AND NOT available THEN 'incompatible'
   WHEN before_values IS NULL THEN 'create' ELSE 'update' END;
  manifest:=jsonb_build_object('targetId',target.instance_id,'targetName',target.display_name,'targetRevision',target.revision,'groupingRevision',target.grouping_revision,
   'ruleId',rule_id,'expectedRuleRevision',r.revision,'beforeValues',before_values,'desiredValues',v_values,'decision',decision,'compatibility',CASE WHEN available THEN 'available' ELSE 'unavailable' END,
   'compatibilityReason',CASE WHEN available THEN NULL ELSE 'enabled_source_schedule_unavailable' END,'operationId',alerting.rule_template_id(id::text||'|'||target.instance_id::text||'|apply'),'beforeBinding',before_binding);
  INSERT INTO alerting.rule_deployment_member(deployment_id,instance_id,manifest) VALUES(id,target.instance_id,manifest);
 END LOOP;
 receipt:=jsonb_build_object('operationId',p_command->>'operationId','auditId',a,'recordedAtUtc',now_at,'deployment',alerting.rule_deployment_get(id,p_all,p_scope,true));
 RETURN alerting.rule_template_record(p_command,p_actor,'deployment.preview',id,p_correlation,receipt);
END $$;

CREATE FUNCTION alerting.rule_deployment_rollback_preview(p_command jsonb,p_actor text,p_correlation uuid,p_all boolean,p_scope uuid[],p_has_admin boolean,p_approval jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE id uuid:=(p_command->>'deploymentId')::uuid;source uuid:=(p_command->>'sourceDeploymentId')::uuid;job alerting.rule_deployment%ROWTYPE;
 m alerting.rule_deployment_member%ROWTYPE;target record;r alerting.rule%ROWTYPE;v alerting.rule_template_version%ROWTYPE;desired jsonb;manifest jsonb;receipt jsonb;now_at timestamptz;a uuid:=gen_random_uuid();available boolean;
BEGIN
 PERFORM alerting.rule_template_require(p_command,p_actor,p_has_admin);
 IF id IS NULL OR source IS NULL OR id=source OR id='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'invalid rollback identity' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('rule-deployment-jobs',0));
 receipt:=alerting.rule_template_replay(p_command,p_actor,'deployment.rollback-preview',id);
 IF receipt IS NOT NULL THEN IF NOT alerting.rule_deployment_authorized(id,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;RETURN receipt; END IF;
 IF current_setting('transaction_isolation')<>'repeatable read' THEN RAISE EXCEPTION 'preview requires a coherent repeatable-read snapshot' USING ERRCODE='22023'; END IF;
 IF EXISTS(SELECT 1 FROM alerting.rule_deployment WHERE deployment_id=id) THEN RAISE EXCEPTION 'deployment already exists' USING ERRCODE='40001'; END IF;
 SELECT * INTO job FROM alerting.rule_deployment WHERE deployment_id=source FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'source deployment not found' USING ERRCODE='P0002'; END IF;
 IF NOT alerting.rule_deployment_authorized(source,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;
 IF NOT job.sealed OR job.mode='rollback' THEN RAISE EXCEPTION 'source deployment must be sealed' USING ERRCODE='40001'; END IF;
 IF NOT EXISTS(SELECT 1 FROM alerting.rule_deployment_member WHERE deployment_id=source AND status='applied') THEN RAISE EXCEPTION 'no applied members' USING ERRCODE='22023'; END IF;
 SELECT * INTO v FROM alerting.rule_template_version WHERE template_id=job.template_id AND version=job.template_version;
 now_at:=clock_timestamp();INSERT INTO alerting.rule_deployment VALUES(id,1,p_actor,job.template_id,job.template_version,NULL,'rollback',source,now_at,now_at+interval '15 minutes',false,p_command->>'reason');
 FOR m IN SELECT * FROM alerting.rule_deployment_member WHERE deployment_id=source AND status='applied' ORDER BY instance_id LOOP
  SELECT t.instance_id,t.display_name,t.revision,coalesce(g.revision,0) grouping_revision INTO target FROM control.observation_target t LEFT JOIN control.observation_target_grouping g USING(instance_id) WHERE t.instance_id=m.instance_id FOR UPDATE OF t;
  SELECT * INTO r FROM alerting.rule WHERE instance_id=m.instance_id AND rule_id=(m.manifest->>'ruleId')::uuid FOR UPDATE;
  desired:=CASE WHEN m.manifest->'beforeValues'='null'::jsonb THEN jsonb_set(m.manifest->'desiredValues','{enabled}','false'::jsonb) ELSE m.manifest->'beforeValues' END;
  PERFORM alerting.rule_template_admit(desired,v.payload->>'sourceTemplateName',v.payload->>'catalogDigest',p_approval);
  available:=alerting.rule_template_available(m.instance_id,target.revision,desired);
  manifest:=jsonb_build_object('targetId',m.instance_id,'targetName',target.display_name,'targetRevision',target.revision,'groupingRevision',target.grouping_revision,'ruleId',m.manifest->'ruleId',
   'expectedRuleRevision',m.result_revision,'beforeValues',m.manifest->'desiredValues','desiredValues',desired,'decision',CASE WHEN r.revision IS DISTINCT FROM m.result_revision OR alerting.rule_template_values(r) IS DISTINCT FROM m.manifest->'desiredValues' THEN 'incompatible' WHEN m.manifest->'beforeValues'='null'::jsonb THEN 'disable_created' ELSE 'restore' END,
   'compatibility',CASE WHEN available THEN 'available' ELSE 'unavailable' END,'compatibilityReason',CASE WHEN available THEN NULL ELSE 'enabled_source_schedule_unavailable' END,
   'operationId',alerting.rule_template_id(id::text||'|'||m.instance_id::text||'|apply'),'restoreBinding',m.manifest->'beforeBinding');
  INSERT INTO alerting.rule_deployment_member(deployment_id,instance_id,manifest) VALUES(id,m.instance_id,manifest);
 END LOOP;
 receipt:=jsonb_build_object('operationId',p_command->>'operationId','auditId',a,'recordedAtUtc',now_at,'deployment',alerting.rule_deployment_get(id,p_all,p_scope,true));
 RETURN alerting.rule_template_record(p_command,p_actor,'deployment.rollback-preview',id,p_correlation,receipt);
END $$;
CREATE FUNCTION alerting.rule_template_replay(p_command jsonb,p_actor text,p_action text,p_subject uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE old alerting.rule_template_operation%ROWTYPE;digest bytea:=sha256(convert_to(p_command::text,'UTF8'));
BEGIN
 SELECT * INTO old FROM alerting.rule_template_operation WHERE operation_id=(p_command->>'operationId')::uuid;
 IF FOUND THEN
  IF old.actor_sid IS DISTINCT FROM p_actor OR old.action_name IS DISTINCT FROM p_action OR old.subject_id IS DISTINCT FROM p_subject OR old.request_digest IS DISTINCT FROM digest
  THEN RAISE EXCEPTION 'operation payload conflict' USING ERRCODE='40001'; END IF;
  RETURN old.receipt;
 END IF;
 RETURN NULL;
END $$;
CREATE FUNCTION alerting.rule_template_record(p_command jsonb,p_actor text,p_action text,p_subject uuid,p_correlation uuid,p_receipt jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
BEGIN
 INSERT INTO alerting.rule_template_activity VALUES((p_receipt->>'auditId')::uuid,(p_command->>'operationId')::uuid,p_actor,p_action,p_subject,p_correlation,(p_receipt->>'recordedAtUtc')::timestamptz,
  jsonb_build_object('reason',p_command->>'reason','operationId',p_command->>'operationId','result',p_receipt));
 INSERT INTO alerting.rule_template_operation(operation_id,actor_sid,action_name,subject_id,request_digest,receipt,recorded_at)
 VALUES((p_command->>'operationId')::uuid,p_actor,p_action,p_subject,sha256(convert_to(p_command::text,'UTF8')),p_receipt,(p_receipt->>'recordedAtUtc')::timestamptz);
 RETURN p_receipt;
END $$;

-- Values use the native wire shape. Metadata never originates in request bodies.
CREATE FUNCTION alerting.rule_template_admit(p_values jsonb,p_source text,p_catalog text,p_approval jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE kind integer;comparison integer;collector text;schema integer;unit text;expected_source text;
 threshold double precision;hysteresis double precision;matches integer;clears integer;window_seconds integer;evaluation integer;
BEGIN
 IF p_catalog IS DISTINCT FROM (SELECT catalog_digest FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts')
 THEN RAISE EXCEPTION 'catalog changed' USING ERRCODE='40001'; END IF;
 IF jsonb_typeof(p_values) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(p_values))<>11
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_values) k WHERE k NOT IN ('name','kind','metricId','comparison','threshold','hysteresis','confirmationCount','confirmationSeconds','evaluationSeconds','clearConfirmationCount','enabled'))
 OR jsonb_typeof(p_values->'enabled') IS DISTINCT FROM 'boolean' OR coalesce(p_values->>'name','') !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'
 OR jsonb_typeof(p_values->'threshold') IS DISTINCT FROM 'number' OR jsonb_typeof(p_values->'hysteresis') IS DISTINCT FROM 'number'
 OR coalesce(p_values->>'confirmationCount','') !~ '^(100|[1-9][0-9]?)$' OR coalesce(p_values->>'clearConfirmationCount','') !~ '^(100|[1-9][0-9]?)$'
 OR coalesce(p_values->>'confirmationSeconds','') !~ '^[0-9]{1,6}$' OR coalesce(p_values->>'evaluationSeconds','') !~ '^[0-9]{1,4}$'
 THEN RAISE EXCEPTION 'invalid rule settings' USING ERRCODE='22023'; END IF;
 kind:=CASE p_values->>'kind' WHEN 'metric_threshold' THEN 1 WHEN 'collector_health' THEN 2 WHEN 'backup_full_policy_v2' THEN 3 WHEN 'backup_log_policy_v2' THEN 4 WHEN 'backup_observed_faults_v2' THEN 5 WHEN 'blocking_duration_v1' THEN 6 WHEN 'deadlock_count_v1' THEN 7 END;
 comparison:=CASE p_values->>'comparison' WHEN 'greater_than' THEN 1 WHEN 'greater_than_or_equal' THEN 2 WHEN 'less_than' THEN 3 WHEN 'less_than_or_equal' THEN 4 WHEN 'equal' THEN 5 END;
 expected_source:=CASE kind WHEN 1 THEN 'metric.threshold' WHEN 2 THEN 'collector.health' WHEN 3 THEN 'backup.full_policy' WHEN 4 THEN 'backup.log_policy' WHEN 5 THEN 'backup.observed_faults' WHEN 6 THEN 'blocking.duration' WHEN 7 THEN 'deadlocks.count' END;
 collector:=CASE WHEN kind IN(1,2) THEN 'engine.core' WHEN kind IN(3,4,5) THEN 'backups.status' WHEN kind=6 THEN 'blocking.current' WHEN kind=7 THEN 'deadlocks.system-health' END;
 schema:=CASE WHEN kind IN(3,4,5) THEN 2 ELSE 1 END;unit:=CASE WHEN kind IN(3,4,5) THEN 'affected databases' WHEN kind=6 THEN 'seconds' ELSE 'count' END;
 IF kind IS NULL OR comparison IS NULL OR p_source IS DISTINCT FROM expected_source OR p_approval IS NULL
 OR p_approval->>'catalogDigest' IS DISTINCT FROM p_catalog
 OR (CASE WHEN p_approval ? 'items' THEN NOT EXISTS(SELECT 1 FROM jsonb_array_elements(p_approval->'items') e WHERE e->>'name'=p_source AND e->>'kind'=p_values->>'kind' AND e->>'metricId' IS NOT DISTINCT FROM p_values->>'metricId')
 ELSE p_approval->>'sourceTemplateName' IS DISTINCT FROM p_source OR p_approval->>'sourceCollector' IS DISTINCT FROM collector OR (p_approval->>'sourceSchemaVersion')::integer IS DISTINCT FROM schema OR p_approval->>'unit' IS DISTINCT FROM unit END)
 THEN RAISE EXCEPTION 'source approval changed' USING ERRCODE='40001'; END IF;
 threshold:=(p_values->>'threshold')::double precision;hysteresis:=(p_values->>'hysteresis')::double precision;matches:=(p_values->>'confirmationCount')::integer;clears:=(p_values->>'clearConfirmationCount')::integer;window_seconds:=(p_values->>'confirmationSeconds')::integer;evaluation:=(p_values->>'evaluationSeconds')::integer;
 IF window_seconds NOT BETWEEN 0 AND 604800 OR evaluation NOT BETWEEN 1 AND 3600 OR threshold<0 OR hysteresis<0 OR hysteresis>threshold
 OR threshold IN('NaN'::float8,'Infinity'::float8,'-Infinity'::float8) OR hysteresis IN('NaN'::float8,'Infinity'::float8,'-Infinity'::float8)
 OR (kind=1 AND (p_values->>'metricId' IS DISTINCT FROM 'engine.user_connections' OR comparison NOT IN(1,2) OR threshold>1000000 OR evaluation<15))
 OR (kind<>1 AND p_values->'metricId' IS DISTINCT FROM 'null'::jsonb)
 OR (kind=2 AND (p_values->>'name'<>'collector.health' OR comparison<>3 OR threshold<>1 OR hysteresis<>0 OR matches<>2 OR window_seconds<>300 OR evaluation<>30))
 OR (kind IN(3,4,5) AND (comparison<>1 OR threshold<>0 OR hysteresis<>0 OR evaluation<15 OR window_seconds<evaluation*(greatest(matches,clears)-1)))
 OR (kind=6 AND (comparison NOT IN(1,2) OR threshold>86400 OR evaluation NOT BETWEEN 5 AND 60 OR window_seconds<evaluation*(greatest(matches,clears)-1)))
 OR (kind=7 AND (comparison NOT IN(1,2) OR threshold>256 OR threshold<>trunc(threshold) OR hysteresis<>trunc(hysteresis) OR evaluation NOT BETWEEN 15 AND 60 OR window_seconds<evaluation*(greatest(matches,clears)-1)))
 THEN RAISE EXCEPTION 'settings outside product catalog' USING ERRCODE='22023'; END IF;
 RETURN jsonb_build_object('Kind',kind,'MetricId',p_values->'metricId','Comparison',comparison,'Name',p_values->>'name','Threshold',p_values->'threshold','Hysteresis',p_values->'hysteresis',
 'ConfirmationCount',matches,'ClearConfirmationCount',clears,'ConfirmationWindow',window_seconds||' seconds','EvaluationInterval',evaluation||' seconds','Enabled',p_values->'enabled','SourceCollector',collector,'SourceSchemaVersion',schema,'CatalogDigest',p_catalog);
END $$;
CREATE FUNCTION alerting.rule_template_values(p_rule alerting.rule) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('name',p_rule.name,'kind',CASE p_rule.kind WHEN 1 THEN 'metric_threshold' WHEN 2 THEN 'collector_health' WHEN 3 THEN 'backup_full_policy_v2' WHEN 4 THEN 'backup_log_policy_v2' WHEN 5 THEN 'backup_observed_faults_v2' WHEN 6 THEN 'blocking_duration_v1' WHEN 7 THEN 'deadlock_count_v1' END,
 'metricId',p_rule.metric_id,'comparison',CASE p_rule.comparison WHEN 1 THEN 'greater_than' WHEN 2 THEN 'greater_than_or_equal' WHEN 3 THEN 'less_than' WHEN 4 THEN 'less_than_or_equal' WHEN 5 THEN 'equal' END,
 'threshold',p_rule.threshold,'hysteresis',p_rule.hysteresis,'confirmationCount',p_rule.confirmation_count,'confirmationSeconds',extract(epoch FROM p_rule.confirmation_window)::integer,
 'evaluationSeconds',extract(epoch FROM p_rule.evaluation_interval)::integer,'clearConfirmationCount',p_rule.clear_confirmation_count,'enabled',p_rule.enabled);
$$;
CREATE FUNCTION alerting.rule_template_available(p_target uuid,p_revision bigint,p_values jsonb) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
 SELECT EXISTS(SELECT 1 FROM control.collector_schedule s JOIN control.collector_contract c USING(collector_id,collector_version)
 WHERE s.instance_id=p_target AND s.target_revision=p_revision AND s.enabled
 AND s.collector_id=CASE WHEN p_values->>'kind' IN('metric_threshold','collector_health') THEN 'engine.core' WHEN p_values->>'kind' LIKE 'backup_%' THEN 'backups.status' WHEN p_values->>'kind'='blocking_duration_v1' THEN 'blocking.current' ELSE 'deadlocks.system-health' END
 AND c.output_schema_version=CASE WHEN p_values->>'kind' LIKE 'backup_%' THEN 2 ELSE 1 END);
$$;

CREATE FUNCTION alerting.rule_template_get(p_id uuid,p_version bigint,p_has_admin boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE value jsonb;
BEGIN
 IF NOT coalesce(p_has_admin,false) THEN RAISE EXCEPTION 'administrator access required' USING ERRCODE='42501'; END IF;
 SELECT jsonb_build_object('templateId',t.template_id,'version',v.version,'currentVersion',t.current_version,'title',v.payload->>'title','ownerSid',t.owner_sid,'actorSid',v.actor_sid,'sourceTemplateName',v.payload->>'sourceTemplateName',
 'catalogDigest',v.payload->>'catalogDigest','versionDigest',v.version_digest,'values',v.payload->'values','reason',v.payload->>'reason','operationId',v.operation_id,'auditId',v.audit_id,'createdAtUtc',v.created_at,'serverNowUtc',clock_timestamp())
 INTO value FROM alerting.rule_template t JOIN alerting.rule_template_version v ON v.template_id=t.template_id AND v.version=coalesce(p_version,t.current_version) WHERE t.template_id=p_id;
 RETURN value;
END $$;
CREATE FUNCTION alerting.rule_deployment_batch(p_command jsonb,p_actor text,p_correlation uuid,p_all boolean,p_scope uuid[],p_has_admin boolean,p_approval jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE id uuid:=(p_command->>'deploymentId')::uuid;job alerting.rule_deployment%ROWTYPE;m alerting.rule_deployment_member%ROWTYPE;v alerting.rule_template_version%ROWTYPE;
 target record;r alerting.rule%ROWTYPE;rule_json jsonb;receipt jsonb;items jsonb:='[]';now_at timestamptz;v_result_at timestamptz;result_audit uuid;v_result_revision bigint;
 v_status text;code text;error_state text;pending integer;operation uuid;decision text;a uuid:=gen_random_uuid();previous_scope text:=current_setting('sqlobserver.target_scope',true);
BEGIN
 PERFORM alerting.rule_template_require(p_command,p_actor,p_has_admin);
 IF id IS NULL OR id='00000000-0000-0000-0000-000000000000' OR p_command->>'action' NOT IN('apply','stop')
 OR (p_command->>'expectedRevision')::bigint NOT BETWEEN 1 AND 9007199254740990 THEN RAISE EXCEPTION 'invalid batch command' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('rule-deployment-jobs',0));
 receipt:=alerting.rule_template_replay(p_command,p_actor,'deployment.'||(p_command->>'action'),id);
 IF receipt IS NOT NULL THEN IF NOT alerting.rule_deployment_authorized(id,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;RETURN receipt; END IF;
 SELECT * INTO job FROM alerting.rule_deployment WHERE deployment_id=id FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'deployment not found' USING ERRCODE='P0002'; END IF;
 IF NOT alerting.rule_deployment_authorized(id,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;
 IF job.revision IS DISTINCT FROM (p_command->>'expectedRevision')::bigint OR job.sealed THEN RAISE EXCEPTION 'job revision changed or job sealed' USING ERRCODE='40001'; END IF;
 now_at:=clock_timestamp();
 IF p_command->>'action'='apply' AND now_at>=job.expires_at THEN RAISE EXCEPTION 'preview expired' USING ERRCODE='40001'; END IF;
 SELECT * INTO v FROM alerting.rule_template_version WHERE template_id=job.template_id AND version=job.template_version;
 FOR m IN SELECT * FROM alerting.rule_deployment_member WHERE deployment_id=id AND rule_deployment_member.status='pending' ORDER BY instance_id
 LIMIT(CASE WHEN p_command->>'action'='apply' THEN 10 ELSE 100 END) FOR UPDATE LOOP
  v_result_revision:=NULL;result_audit:=NULL;v_result_at:=NULL;code:=NULL;error_state:=NULL;decision:=m.manifest->>'decision';operation:=(m.manifest->>'operationId')::uuid;
  IF p_command->>'action'='stop' THEN v_status:='stopped';code:='job_stopped';v_result_at:=now_at;
  ELSE
   -- PL/pgSQL exception blocks are per-target savepoints. Rule writes and their
   -- native audit/state/outbox changes roll back together on any target failure.
   BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(m.instance_id::text,0));
    SELECT t.revision,t.lifecycle_state,coalesce(g.revision,0) grouping_revision INTO target FROM control.observation_target t
     LEFT JOIN control.observation_target_grouping g USING(instance_id) WHERE t.instance_id=m.instance_id FOR UPDATE OF t;
    IF NOT FOUND OR target.lifecycle_state='retired' OR target.revision IS DISTINCT FROM (m.manifest->>'targetRevision')::bigint OR target.grouping_revision IS DISTINCT FROM (m.manifest->>'groupingRevision')::bigint
    THEN RAISE EXCEPTION 'target or grouping changed' USING ERRCODE='40001'; END IF;
    SELECT * INTO r FROM alerting.rule WHERE instance_id=m.instance_id AND rule_id=(m.manifest->>'ruleId')::uuid FOR UPDATE;
    IF r.revision IS DISTINCT FROM (m.manifest->>'expectedRuleRevision')::bigint
     OR (CASE WHEN r.rule_id IS NULL THEN 'null'::jsonb ELSE alerting.rule_template_values(r) END) IS DISTINCT FROM m.manifest->'beforeValues'
     OR (r.rule_id IS NULL AND EXISTS(SELECT 1 FROM alerting.rule WHERE instance_id=m.instance_id AND name=m.manifest->'desiredValues'->>'name'))
    THEN RAISE EXCEPTION 'rule changed' USING ERRCODE='40001'; END IF;
    rule_json:=alerting.rule_template_admit(m.manifest->'desiredValues',v.payload->>'sourceTemplateName',v.payload->>'catalogDigest',p_approval);
    IF decision='incompatible' THEN RAISE EXCEPTION 'incompatible preview member' USING ERRCODE='40001'; END IF;
    IF decision IN('skip_existing','skip_unchanged') THEN v_status:='skipped';code:=decision;v_result_revision:=r.revision;v_result_at:=clock_timestamp();
    ELSE
     IF (m.manifest->'desiredValues'->>'enabled')::boolean AND NOT alerting.rule_template_available(m.instance_id,target.revision,m.manifest->'desiredValues')
     THEN RAISE EXCEPTION 'source unavailable' USING ERRCODE='40001'; END IF;
     PERFORM set_config('sqlobserver.target_scope',m.instance_id::text,true);
     rule_json:=rule_json||jsonb_build_object('Action',CASE WHEN r.rule_id IS NULL THEN 'CreateAlertRule' WHEN NOT(m.manifest->'desiredValues'->>'enabled')::boolean THEN 'RetireAlertRule' ELSE 'UpdateAlertRule' END,
      'RuleId',m.manifest->'ruleId','TargetId',m.instance_id,'ExpectedRevision',r.revision,'RequestDigest',encode(sha256(convert_to(m.manifest::text,'UTF8')),'hex'));
     SELECT write.audit_id,write.recorded_at INTO result_audit,v_result_at FROM alerting.upsert_rule(rule_json,operation::text,p_actor,p_correlation) write;
     SELECT revision INTO STRICT v_result_revision FROM alerting.rule WHERE instance_id=m.instance_id AND rule_id=(m.manifest->>'ruleId')::uuid;
     v_status:='applied';
     IF job.mode='rollback' THEN
      DELETE FROM alerting.rule_template_binding WHERE instance_id=m.instance_id AND rule_id=(m.manifest->>'ruleId')::uuid;
      IF m.manifest->'restoreBinding' IS NOT NULL AND m.manifest->'restoreBinding'<>'null'::jsonb
       AND m.manifest->'restoreBinding'->'configuration'=m.manifest->'desiredValues' THEN
       INSERT INTO alerting.rule_template_binding VALUES(m.instance_id,(m.manifest->>'ruleId')::uuid,(m.manifest->'restoreBinding'->>'template_id')::uuid,
        (m.manifest->'restoreBinding'->>'template_version')::bigint,(m.manifest->'restoreBinding'->>'deployment_id')::uuid,v_result_revision,m.manifest->'desiredValues');
      END IF;
     ELSE
      INSERT INTO alerting.rule_template_binding VALUES(m.instance_id,(m.manifest->>'ruleId')::uuid,job.template_id,job.template_version,id,v_result_revision,m.manifest->'desiredValues')
       ON CONFLICT(instance_id,rule_id) DO UPDATE SET template_id=EXCLUDED.template_id,template_version=EXCLUDED.template_version,deployment_id=EXCLUDED.deployment_id,rule_revision=EXCLUDED.rule_revision,configuration=EXCLUDED.configuration;
     END IF;
     UPDATE audit.activity SET safe_details=jsonb_build_object('deploymentId',id,'templateId',job.template_id,'templateVersion',job.template_version,'versionDigest',v.version_digest,'catalogDigest',v.payload->>'catalogDigest','operationId',operation,'reason',p_command->>'reason') WHERE activity_id=result_audit;
    END IF;
   EXCEPTION WHEN OTHERS THEN
    GET STACKED DIAGNOSTICS error_state=RETURNED_SQLSTATE;
    -- Cancellation and connection/statement deadlines remain uncertain writes;
    -- they must abort the batch rather than manufacture terminal target results.
    IF error_state IN('57014','08000','08003','08006','57P01') THEN RAISE; END IF;
    v_status:=CASE WHEN error_state IN('40001','23505','42501','55000') THEN 'conflict' ELSE 'failed' END;
    code:=CASE WHEN error_state='40001' THEN 'preview_binding_changed' WHEN error_state='23505' THEN 'rule_name_conflict' WHEN error_state='42501' THEN 'forbidden' ELSE 'rule_write_failed' END;
    v_result_revision:=NULL;result_audit:=NULL;v_result_at:=clock_timestamp();
   END;
  END IF;
  PERFORM set_config('sqlobserver.target_scope',m.instance_id::text,true);
  IF v_status<>'applied' THEN
   result_audit:=gen_random_uuid();v_result_at:=coalesce(v_result_at,clock_timestamp());
   INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
   VALUES(v_result_at,result_audit,'user',p_actor,'alert.rule.template.'||(p_command->>'action'),'allowed',CASE WHEN v_status IN('skipped','stopped') THEN 'succeeded' WHEN v_status='conflict' THEN 'conflict' ELSE 'failed' END,
    'alerting',m.instance_id::text,p_correlation,jsonb_build_object('deploymentId',id,'templateId',job.template_id,'templateVersion',job.template_version,'operationId',p_command->>'operationId','reason',p_command->>'reason','resultCode',code));
  END IF;
  -- Keep only SQLSTATE in the private ledger for native diagnostics. Provider
  -- messages, details and query text never enter the public receipt or audit.
  UPDATE alerting.rule_deployment_member SET status=v_status,result_revision=v_result_revision,result_audit_id=result_audit,result_at=v_result_at,result_code=code,result_sqlstate=error_state WHERE deployment_id=id AND instance_id=m.instance_id;
  items:=items||jsonb_build_array(jsonb_build_object('targetId',m.instance_id,'ruleId',m.manifest->'ruleId','status',v_status,'code',code,'ruleRevision',v_result_revision,'auditId',result_audit,'recordedAtUtc',v_result_at));
 END LOOP;
 PERFORM set_config('sqlobserver.target_scope',coalesce(previous_scope,''),true);
 SELECT count(*) INTO pending FROM alerting.rule_deployment_member WHERE deployment_id=id AND rule_deployment_member.status='pending';
 UPDATE alerting.rule_deployment SET revision=revision+1,sealed=pending=0 WHERE deployment_id=id RETURNING revision INTO job.revision;
 receipt:=jsonb_build_object('operationId',p_command->>'operationId','auditId',a,'recordedAtUtc',now_at,'deploymentId',id,'revision',job.revision,'action',p_command->>'action','items',items,'pendingCount',pending,'sealed',pending=0);
 RETURN alerting.rule_template_record(p_command,p_actor,'deployment.'||(p_command->>'action'),id,p_correlation,receipt);
END $$;

CREATE FUNCTION alerting.rule_template_list(p_limit integer,p_before uuid,p_has_admin boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE items jsonb;more boolean;next_id uuid;
BEGIN
 IF NOT coalesce(p_has_admin,false) THEN RAISE EXCEPTION 'administrator access required' USING ERRCODE='42501'; END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 OR p_before='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'invalid page' USING ERRCODE='22023'; END IF;
 IF p_before IS NOT NULL AND NOT EXISTS(SELECT 1 FROM alerting.rule_template WHERE template_id=p_before) THEN RAISE EXCEPTION 'page anchor not found' USING ERRCODE='P0002'; END IF;
 SELECT coalesce(jsonb_agg(alerting.rule_template_get(t.template_id,NULL,true) ORDER BY t.template_id DESC),'[]'::jsonb) INTO items FROM(SELECT template_id FROM alerting.rule_template WHERE p_before IS NULL OR template_id<p_before ORDER BY template_id DESC LIMIT p_limit+1)t;
 more:=jsonb_array_length(items)>p_limit;IF more THEN items:=items-p_limit;next_id:=(items->(p_limit-1)->>'templateId')::uuid; END IF;
 RETURN jsonb_build_object('serverNowUtc',clock_timestamp(),'items',items,'nextBeforeTemplateId',next_id);
END $$;
CREATE FUNCTION alerting.rule_template_versions(p_id uuid,p_limit integer,p_before bigint,p_has_admin boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE items jsonb;next_version bigint;
BEGIN
 IF NOT coalesce(p_has_admin,false) THEN RAISE EXCEPTION 'administrator access required' USING ERRCODE='42501'; END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 OR p_before<=0 THEN RAISE EXCEPTION 'invalid page' USING ERRCODE='22023'; END IF;
 IF NOT EXISTS(SELECT 1 FROM alerting.rule_template WHERE template_id=p_id) OR (p_before IS NOT NULL AND NOT EXISTS(SELECT 1 FROM alerting.rule_template_version WHERE template_id=p_id AND version=p_before)) THEN RAISE EXCEPTION 'page anchor not found' USING ERRCODE='P0002'; END IF;
 SELECT coalesce(jsonb_agg(alerting.rule_template_get(p_id,t.version,true) ORDER BY t.version DESC),'[]'::jsonb) INTO items FROM(SELECT version FROM alerting.rule_template_version WHERE template_id=p_id AND(p_before IS NULL OR version<p_before) ORDER BY version DESC LIMIT p_limit+1)t;
 IF jsonb_array_length(items)>p_limit THEN items:=items-p_limit;next_version:=(items->(p_limit-1)->>'version')::bigint; END IF;
 RETURN jsonb_build_object('templateId',p_id,'serverNowUtc',clock_timestamp(),'items',items,'nextBeforeVersion',next_version);
END $$;
CREATE FUNCTION alerting.rule_deployment_list(p_limit integer,p_before uuid,p_all boolean,p_scope uuid[],p_has_admin boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE items jsonb;next_id uuid;
BEGIN
 IF NOT coalesce(p_has_admin,false) THEN RAISE EXCEPTION 'administrator access required' USING ERRCODE='42501'; END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 OR p_before='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'invalid page' USING ERRCODE='22023'; END IF;
 IF p_before IS NOT NULL AND NOT alerting.rule_deployment_authorized(p_before,p_all,p_scope) THEN RAISE EXCEPTION 'page anchor not found' USING ERRCODE='P0002'; END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('deploymentId',j.deployment_id,'revision',j.revision,'actorSid',j.actor_sid,'group',j.group_selector,'mode',j.mode,'sourceDeploymentId',j.source_deployment_id,
 'templateId',j.template_id,'templateVersion',j.template_version,'templateTitle',v.payload->>'title','previewAtUtc',j.preview_at,'sealed',j.sealed,
 'pendingCount',(SELECT count(*) FROM alerting.rule_deployment_member WHERE deployment_id=j.deployment_id AND status='pending'),'memberCount',(SELECT count(*) FROM alerting.rule_deployment_member WHERE deployment_id=j.deployment_id),'serverNowUtc',clock_timestamp()) ORDER BY j.deployment_id DESC),'[]'::jsonb)
 INTO items FROM(SELECT * FROM alerting.rule_deployment WHERE(p_before IS NULL OR deployment_id<p_before) AND alerting.rule_deployment_authorized(deployment_id,p_all,p_scope) ORDER BY deployment_id DESC LIMIT p_limit+1)j
 JOIN alerting.rule_template_version v ON v.template_id=j.template_id AND v.version=j.template_version;
 IF jsonb_array_length(items)>p_limit THEN items:=items-p_limit;next_id:=(items->(p_limit-1)->>'deploymentId')::uuid; END IF;
 RETURN jsonb_build_object('serverNowUtc',clock_timestamp(),'items',items,'nextBeforeDeploymentId',next_id);
END $$;
CREATE FUNCTION alerting.rule_deployment_history(p_id uuid,p_limit integer,p_before uuid,p_all boolean,p_scope uuid[],p_has_admin boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE items jsonb;anchor bigint;next_id uuid;
BEGIN
 IF NOT coalesce(p_has_admin,false) OR NOT alerting.rule_deployment_authorized(p_id,p_all,p_scope) THEN RAISE EXCEPTION 'deployment access denied' USING ERRCODE='42501'; END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 OR p_before='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'invalid page' USING ERRCODE='22023'; END IF;
 IF p_before IS NOT NULL THEN SELECT change_sequence INTO anchor FROM alerting.rule_template_operation WHERE operation_id=p_before AND subject_id=p_id AND action_name IN('deployment.apply','deployment.stop');IF NOT FOUND THEN RAISE EXCEPTION 'page anchor not found' USING ERRCODE='P0002'; END IF; END IF;
 SELECT coalesce(jsonb_agg(receipt ORDER BY change_sequence DESC),'[]'::jsonb) INTO items FROM(SELECT receipt,change_sequence FROM alerting.rule_template_operation WHERE subject_id=p_id AND action_name IN('deployment.apply','deployment.stop') AND(anchor IS NULL OR change_sequence<anchor) ORDER BY change_sequence DESC LIMIT p_limit+1)r;
 IF jsonb_array_length(items)>p_limit THEN items:=items-p_limit;next_id:=(items->(p_limit-1)->>'operationId')::uuid; END IF;
 RETURN jsonb_build_object('deploymentId',p_id,'serverNowUtc',clock_timestamp(),'items',items,'nextBeforeOperationId',next_id);
END $$;

CREATE FUNCTION alerting.rule_template_write(p_command jsonb,p_actor text,p_correlation uuid,p_all boolean,p_scope uuid[],p_has_admin boolean,p_approval jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET row_security=on AS $$
DECLARE id uuid:=(p_command->>'templateId')::uuid;old alerting.rule_template%ROWTYPE;v_new_version bigint;payload jsonb;admission jsonb;digest text;receipt jsonb;now_at timestamptz;a uuid:=gen_random_uuid();
BEGIN
 PERFORM alerting.rule_template_require(p_command,p_actor,p_has_admin);
 IF id IS NULL OR id='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'template identity required' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('rule-template-library',0));
 receipt:=alerting.rule_template_replay(p_command,p_actor,'template.write',id);IF receipt IS NOT NULL THEN RETURN receipt; END IF;
 SELECT * INTO old FROM alerting.rule_template WHERE template_id=id FOR UPDATE;
 IF FOUND THEN
  IF old.owner_sid IS DISTINCT FROM p_actor AND NOT coalesce(p_all,false) THEN RAISE EXCEPTION 'template owner access required' USING ERRCODE='42501'; END IF;
  IF (p_command->>'expectedVersion')::bigint IS DISTINCT FROM old.current_version THEN RAISE EXCEPTION 'template version changed' USING ERRCODE='40001'; END IF;
  v_new_version:=old.current_version+1;
 ELSE
  IF p_command->>'expectedVersion' IS NOT NULL THEN RAISE EXCEPTION 'template not found' USING ERRCODE='P0002'; END IF;v_new_version:=1;
 END IF;
 IF NOT alerting.rule_template_safe(p_command->>'title',256) OR v_new_version>9007199254740991 THEN RAISE EXCEPTION 'invalid template version' USING ERRCODE='22023'; END IF;
 admission:=alerting.rule_template_admit(p_command->'values',p_command->>'sourceTemplateName',p_command->>'catalogDigest',p_approval);
 payload:=jsonb_build_object('title',p_command->>'title','sourceTemplateName',p_command->>'sourceTemplateName','catalogDigest',p_command->>'catalogDigest','values',p_command->'values','reason',p_command->>'reason',
  'sourceCollector',admission->>'SourceCollector','sourceSchemaVersion',admission->'SourceSchemaVersion','unit',p_approval->>'unit');
 digest:=encode(sha256(convert_to(payload::text,'UTF8')),'hex');now_at:=clock_timestamp();
 INSERT INTO alerting.rule_template VALUES(id,p_actor,v_new_version) ON CONFLICT(template_id) DO UPDATE SET current_version=EXCLUDED.current_version;
 INSERT INTO alerting.rule_template_version VALUES(id,v_new_version,payload,digest,p_actor,(p_command->>'operationId')::uuid,a,now_at);
 receipt:=jsonb_build_object('operationId',p_command->>'operationId','auditId',a,'recordedAtUtc',now_at,'templateId',id,'version',v_new_version,'versionDigest',digest);
 RETURN alerting.rule_template_record(p_command,p_actor,'template.write',id,p_correlation,receipt);
END $$;

-- Helpers are never callable by runtime roles. All definitions precede grants.
DO $function_grants$
DECLARE signature regprocedure;
BEGIN
 FOR signature IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='alerting' AND p.proname IN(
 'rule_template_immutable','rule_template_id','rule_template_safe','rule_template_require','rule_template_replay','rule_template_record','rule_template_admit','rule_template_values','rule_template_available','rule_deployment_authorized',
 'rule_template_get','rule_template_write','rule_template_list','rule_template_versions','rule_deployment_get','rule_deployment_list','rule_deployment_preview','rule_deployment_rollback_preview','rule_deployment_batch','rule_deployment_history') LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',signature);
 END LOOP;
END $function_grants$;
GRANT EXECUTE ON FUNCTION alerting.rule_template_get(uuid,bigint,boolean),alerting.rule_template_list(integer,uuid,boolean),alerting.rule_template_versions(uuid,integer,bigint,boolean),
 alerting.rule_template_write(jsonb,text,uuid,boolean,uuid[],boolean,jsonb),alerting.rule_deployment_preview(jsonb,text,uuid,boolean,uuid[],boolean,jsonb),
 alerting.rule_deployment_rollback_preview(jsonb,text,uuid,boolean,uuid[],boolean,jsonb),alerting.rule_deployment_batch(jsonb,text,uuid,boolean,uuid[],boolean,jsonb),
 alerting.rule_deployment_get(uuid,boolean,uuid[],boolean),alerting.rule_deployment_list(integer,uuid,boolean,uuid[],boolean),alerting.rule_deployment_history(uuid,integer,uuid,boolean,uuid[],boolean)
 TO sqlobserver_server;
