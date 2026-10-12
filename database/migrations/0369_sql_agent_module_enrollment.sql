-- Explicit, revocable module authorization; this does not enable a collector or grant source permissions.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.validate_sql_agent_module_enrollment(p_value jsonb)
RETURNS boolean LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog AS $validate$
DECLARE k text; b jsonb; start_at timestamptz; end_at timestamptz; sid_parts text[]; i integer;
BEGIN
 IF jsonb_typeof(p_value)<>'object' OR octet_length(p_value::text)>16384
 OR (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(p_value) key) IS DISTINCT FROM
 ARRAY['binding','enrollmentId','evidenceSha256','expectedWindowsSid','expiresAtUtc','notBeforeUtc']::text[]
 THEN RETURN false; END IF;
 b:=p_value->'binding';
 IF jsonb_typeof(b)<>'object' OR (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(b) key) IS DISTINCT FROM
 ARRAY['certificateHost','certificateId','definitionSha256','endpointHost','installationPolicyVersion','instanceName','machineName','moduleVersion','objectId','probeVersion','publicCertificateSha256','serverName','sourceVersion','tcpPort','thumbprintHex']::text[]
 THEN RETURN false; END IF;
 FOREACH k IN ARRAY ARRAY['enrollmentId','evidenceSha256','expectedWindowsSid','expiresAtUtc','notBeforeUtc'] LOOP
  IF jsonb_typeof(p_value->k)<>'string' THEN RETURN false; END IF;
 END LOOP;
 IF (p_value->>'enrollmentId') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
 OR (p_value->>'enrollmentId')::uuid='00000000-0000-0000-0000-000000000000'
 OR p_value->>'evidenceSha256' !~ '^[0-9a-f]{64}$' OR p_value->>'evidenceSha256'=repeat('0',64)
 OR length(p_value->>'expectedWindowsSid')>184 OR p_value->>'expectedWindowsSid' !~ '^S-1-(0|[1-9][0-9]*)(-(0|[1-9][0-9]*))+$'
 THEN RETURN false; END IF;
 sid_parts:=string_to_array(p_value->>'expectedWindowsSid','-');
 IF cardinality(sid_parts) NOT BETWEEN 4 AND 18 OR sid_parts[3]::numeric>281474976710655 THEN RETURN false; END IF;
 FOR i IN 4..cardinality(sid_parts) LOOP
  IF sid_parts[i]::numeric>4294967295 THEN RETURN false; END IF;
 END LOOP;
 FOREACH k IN ARRAY ARRAY['certificateHost','definitionSha256','endpointHost','instanceName','machineName','publicCertificateSha256','serverName','thumbprintHex'] LOOP
  IF jsonb_typeof(b->k)<>'string' THEN RETURN false; END IF;
 END LOOP;
 FOREACH k IN ARRAY ARRAY['certificateId','installationPolicyVersion','moduleVersion','objectId','probeVersion','sourceVersion','tcpPort'] LOOP
  IF jsonb_typeof(b->k)<>'number' OR b->>k !~ '^[0-9]+$' THEN RETURN false; END IF;
 END LOOP;
 IF b->>'moduleVersion'<>'1' OR b->>'sourceVersion'<>'2' OR b->>'probeVersion'<>'1' OR b->>'installationPolicyVersion'<>'2'
 OR (b->>'objectId')::bigint NOT BETWEEN 1 AND 2147483647 OR (b->>'certificateId')::bigint NOT BETWEEN 1 AND 2147483647
 OR (b->>'tcpPort')::integer NOT BETWEEN 1 AND 65535
 OR b->>'definitionSha256'<>'447846fb7a7eaa0442b2292b17b82ee17aaf998c3b91c161cba403f7c0bf8512'
 OR b->>'publicCertificateSha256' !~ '^[0-9a-f]{64}$' OR b->>'thumbprintHex' !~ '^[0-9a-f]{40}$'
 THEN RETURN false; END IF;
 FOREACH k IN ARRAY ARRAY['endpointHost','certificateHost'] LOOP
  IF length(b->>k) NOT BETWEEN 1 AND 255 OR b->>k !~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$' THEN RETURN false; END IF;
 END LOOP;
 FOREACH k IN ARRAY ARRAY['serverName','machineName','instanceName'] LOOP
  IF length(b->>k)>128 OR b->>k ~ '[[:cntrl:]]'
  OR (k<>'instanceName' OR length(b->>k)>0) AND b->>k !~ '[^[:space:]]' THEN RETURN false; END IF;
 END LOOP;
 IF p_value->>'notBeforeUtc' !~ '(Z|\+00:00)$' OR p_value->>'expiresAtUtc' !~ '(Z|\+00:00)$' THEN RETURN false; END IF;
 start_at:=(p_value->>'notBeforeUtc')::timestamptz; end_at:=(p_value->>'expiresAtUtc')::timestamptz;
 RETURN isfinite(start_at) AND isfinite(end_at) AND start_at<end_at
  AND start_at>=timestamptz '0001-01-01Z' AND end_at<timestamptz '10000-01-01Z';
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR datetime_field_overflow OR invalid_datetime_format THEN RETURN false;
END $validate$;
REVOKE ALL ON FUNCTION control.validate_sql_agent_module_enrollment(jsonb) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE TABLE control.sql_agent_module_enrollment_command(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision BETWEEN 1 AND 9007199254740991),
 command_revision bigint NOT NULL CHECK(command_revision BETWEEN 1 AND 9007199254740991),
 operation_id uuid NOT NULL UNIQUE CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
 request_sha256 bytea NOT NULL CHECK(octet_length(request_sha256)=32),
 enrollment jsonb CHECK(enrollment IS NULL OR control.validate_sql_agent_module_enrollment(enrollment)),
 audit_activity_id uuid NOT NULL, audit_occurred_at timestamptz NOT NULL CHECK(isfinite(audit_occurred_at)),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,target_revision,command_revision));
ALTER TABLE control.sql_agent_module_enrollment_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.sql_agent_module_enrollment_command FORCE ROW LEVEL SECURITY;
CREATE POLICY sql_agent_module_enrollment_owner ON control.sql_agent_module_enrollment_command FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER sql_agent_module_enrollment_append_only BEFORE UPDATE OR DELETE ON control.sql_agent_module_enrollment_command
 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
REVOKE ALL ON control.sql_agent_module_enrollment_command FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION control.set_sql_agent_module_enrollment(p_instance uuid,p_target_revision bigint,p_expected_revision bigint,
 p_operation uuid,p_enrollment jsonb,p_actor text,p_correlation uuid)
RETURNS TABLE(result_status text,command_revision bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $command$
DECLARE target control.observation_target%ROWTYPE; current_revision bigint; digest bytea;
 replay control.sql_agent_module_enrollment_command%ROWTYPE; audit_id uuid; audit_at timestamptz;
BEGIN
 IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000' OR p_target_revision IS NULL
 OR p_target_revision NOT BETWEEN 1 AND 9007199254740991 OR p_expected_revision IS NULL OR p_expected_revision NOT BETWEEN 0 AND 9007199254740990
 OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000' OR p_actor IS NULL OR p_correlation IS NULL
 OR (p_enrollment IS NOT NULL AND NOT control.validate_sql_agent_module_enrollment(p_enrollment))
 THEN RAISE EXCEPTION 'Invalid module enrollment command' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-operation:'||p_operation::text,0));
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:agent-module-target:'||p_instance::text,0));
 SELECT * INTO target FROM control.observation_target WHERE instance_id=p_instance FOR UPDATE;
 IF NOT FOUND THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','failed','target_not_found');
  RETURN QUERY SELECT 'not_found'::text,NULL::bigint; RETURN;
 END IF;
 IF target.lifecycle_state<>'active' THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict',CASE WHEN target.lifecycle_state='retired' THEN 'target_retired' ELSE 'revision_conflict' END);
  RETURN QUERY SELECT 'inactive'::text,NULL::bigint; RETURN;
 END IF;
 IF target.revision<>p_target_revision THEN
  PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict','revision_conflict');
  RETURN QUERY SELECT 'revision_conflict'::text,NULL::bigint; RETURN;
 END IF;
 SELECT coalesce(max(c.command_revision),0) INTO current_revision FROM control.sql_agent_module_enrollment_command c
 WHERE c.instance_id=p_instance AND c.target_revision=p_target_revision;
 digest:=sha256(convert_to(jsonb_build_array(p_instance,p_target_revision,p_expected_revision,p_operation,p_enrollment,p_actor)::text,'UTF8'));
 SELECT * INTO replay FROM control.sql_agent_module_enrollment_command WHERE operation_id=p_operation;
 IF FOUND THEN
  IF replay.request_sha256=digest AND replay.instance_id=p_instance AND replay.target_revision=p_target_revision AND replay.command_revision=current_revision THEN
   RETURN QUERY SELECT 'replayed'::text,current_revision; RETURN;
  END IF;
 ELSIF current_revision=p_expected_revision THEN
  SELECT audit_activity_id,repository_time INTO audit_id,audit_at FROM audit.append_administrative_activity(
   p_actor,p_correlation,'update_observation_target',p_instance,'granted','succeeded','completed');
  INSERT INTO control.sql_agent_module_enrollment_command(instance_id,target_revision,command_revision,operation_id,request_sha256,enrollment,audit_activity_id,audit_occurred_at)
  VALUES(p_instance,p_target_revision,current_revision+1,p_operation,digest,p_enrollment,audit_id,audit_at);
  RETURN QUERY SELECT 'applied'::text,current_revision+1; RETURN;
 END IF;
 PERFORM audit.append_administrative_activity(p_actor,p_correlation,'update_observation_target',p_instance,'granted','conflict','revision_conflict');
 RETURN QUERY SELECT 'revision_conflict'::text,current_revision;
END $command$;
REVOKE ALL ON FUNCTION control.set_sql_agent_module_enrollment(uuid,bigint,bigint,uuid,jsonb,text,uuid) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_sql_agent_module_enrollment(uuid,bigint,bigint,uuid,jsonb,text,uuid) TO sqlobserver_server;

CREATE FUNCTION control.read_sql_agent_module_enrollment(p_instance uuid,p_target_revision bigint)
RETURNS TABLE(result_status text,command_revision bigint,enrollment jsonb)
LANGUAGE plpgsql SECURITY DEFINER STABLE PARALLEL UNSAFE SET search_path=pg_catalog AS $read$
DECLARE target control.observation_target%ROWTYPE;
BEGIN
 IF p_instance IS NULL OR p_instance='00000000-0000-0000-0000-000000000000' OR p_target_revision IS NULL OR p_target_revision NOT BETWEEN 1 AND 9007199254740991
 THEN RAISE EXCEPTION 'Invalid module enrollment read' USING ERRCODE='22023'; END IF;
 SELECT * INTO target FROM control.observation_target WHERE instance_id=p_instance;
 IF NOT FOUND THEN RETURN QUERY SELECT 'not_found'::text,NULL::bigint,NULL::jsonb; RETURN; END IF;
 IF target.lifecycle_state<>'active' THEN RETURN QUERY SELECT 'inactive'::text,NULL::bigint,NULL::jsonb; RETURN; END IF;
 IF target.revision<>p_target_revision THEN RETURN QUERY SELECT 'revision_conflict'::text,NULL::bigint,NULL::jsonb; RETURN; END IF;
 RETURN QUERY SELECT 'found'::text,coalesce(c.command_revision,0),c.enrollment FROM (SELECT 1) anchor
 LEFT JOIN LATERAL(SELECT h.command_revision,h.enrollment FROM control.sql_agent_module_enrollment_command h
  WHERE h.instance_id=p_instance AND h.target_revision=p_target_revision ORDER BY h.command_revision DESC LIMIT 1)c ON true;
END $read$;
REVOKE ALL ON FUNCTION control.read_sql_agent_module_enrollment(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.read_sql_agent_module_enrollment(uuid,bigint) TO sqlobserver_server;

CREATE FUNCTION control.resolve_sql_agent_module_enrollment(p_instance uuid,p_target_revision bigint)
RETURNS TABLE(command_revision bigint,enrollment jsonb)
LANGUAGE sql SECURITY DEFINER STABLE PARALLEL UNSAFE SET search_path=pg_catalog SET TimeZone='UTC' AS $resolve$
 SELECT r.command_revision,CASE WHEN r.enrollment IS NOT NULL
  AND (r.enrollment->>'notBeforeUtc')::timestamptz<=statement_timestamp()
  AND statement_timestamp()<(r.enrollment->>'expiresAtUtc')::timestamptz THEN r.enrollment ELSE NULL END
 FROM control.read_sql_agent_module_enrollment(p_instance,p_target_revision) r WHERE r.result_status='found';
$resolve$;
REVOKE ALL ON FUNCTION control.resolve_sql_agent_module_enrollment(uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.resolve_sql_agent_module_enrollment(uuid,bigint) TO sqlobserver_collector;
