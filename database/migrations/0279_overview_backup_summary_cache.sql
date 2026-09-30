-- Fenced, revision-bound aggregates. Fleet reads never enumerate backup databases or samples.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE INDEX backup_freshness_policy_context ON control.backup_freshness_policy(instance_id,target_revision,recorded_at);

CREATE TABLE reporting.overview_backup_latest
(
 instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 assessed_at timestamptz NOT NULL CHECK(isfinite(assessed_at)),
 valid_until timestamptz NOT NULL CHECK(isfinite(valid_until) AND valid_until>assessed_at AND valid_until<=assessed_at+interval '2 minutes'),
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object' AND payload->>'schemaVersion'='1' AND octet_length(payload::text)<=16384)
);
ALTER TABLE reporting.overview_backup_latest ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.overview_backup_latest FORCE ROW LEVEL SECURITY;
CREATE POLICY overview_backup_latest_owner ON reporting.overview_backup_latest
 FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON reporting.overview_backup_latest FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.backup_cache_keys(p_value jsonb,p_keys text[])
RETURNS boolean LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER SET search_path=pg_catalog AS $keys$
BEGIN
 IF jsonb_typeof(p_value) IS DISTINCT FROM 'object' THEN RETURN false; END IF;
 RETURN (SELECT count(*)=cardinality(p_keys) AND bool_and(key=ANY(p_keys)) FROM jsonb_object_keys(p_value) key);
END $keys$;
REVOKE ALL ON FUNCTION reporting.backup_cache_keys(jsonb,text[]) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.backup_cache_counts_valid(p_counts jsonb,p_expected integer)
RETURNS boolean LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER SET search_path=pg_catalog AS $counts$
DECLARE key text; n integer; total integer:=0; unknown_total integer:=0; reason jsonb; code integer; prior integer:=-1;
BEGIN
 IF NOT reporting.backup_cache_keys(p_counts,ARRAY['withinThreshold','policyViolated','indeterminate','notRequired','disabled','unknownReasons'])
  OR jsonb_typeof(p_counts->'unknownReasons') IS DISTINCT FROM 'array' THEN RETURN false; END IF;
 IF jsonb_array_length(p_counts->'unknownReasons')>25 THEN RETURN false; END IF;
 FOREACH key IN ARRAY ARRAY['withinThreshold','policyViolated','indeterminate','notRequired','disabled'] LOOP
  IF jsonb_typeof(p_counts->key) IS DISTINCT FROM 'number' THEN RETURN false; END IF;
  n:=(p_counts->>key)::integer;
  IF n NOT BETWEEN 0 AND 512 THEN RETURN false; END IF; total:=total+n;
 END LOOP;
 FOR reason IN SELECT value FROM jsonb_array_elements(p_counts->'unknownReasons') LOOP
  IF NOT reporting.backup_cache_keys(reason,ARRAY['reason','count']) OR jsonb_typeof(reason->'reason') IS DISTINCT FROM 'number'
   OR jsonb_typeof(reason->'count') IS DISTINCT FROM 'number' THEN RETURN false; END IF;
  code:=(reason->>'reason')::integer; n:=(reason->>'count')::integer;
  IF code NOT BETWEEN 0 AND 24 OR code IN (1,2,17,18,20,21) OR code<=prior OR n NOT BETWEEN 1 AND 512 THEN RETURN false; END IF;
  prior:=code; unknown_total:=unknown_total+n;
 END LOOP;
 RETURN total=p_expected AND unknown_total=(p_counts->>'indeterminate')::integer;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RETURN false;
END $counts$;
REVOKE ALL ON FUNCTION reporting.backup_cache_counts_valid(jsonb,integer) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.overview_backup_context_matches(p_instance uuid,p_revision bigint,p_context jsonb,p_now timestamptz)
RETURNS boolean LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $context$
DECLARE latest record; scan_at timestamptz; enabled boolean; policies bigint; qualification bigint;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM control.observation_target target WHERE target.instance_id=p_instance AND target.revision=p_revision
  AND target.lifecycle_state='active') THEN RETURN false; END IF;
 SELECT coalesce(schedule.enabled,false) INTO enabled FROM control.collector_schedule schedule WHERE schedule.instance_id=p_instance
  AND schedule.target_revision=p_revision AND schedule.collector_id='backups.status' AND schedule.collector_version=2;
 IF coalesce(enabled,false) IS DISTINCT FROM (p_context->>'collectorEnabled')::boolean THEN RETURN false; END IF;
 SELECT count(*) INTO policies FROM control.backup_freshness_policy policy WHERE policy.instance_id=p_instance
  AND policy.target_revision=p_revision AND policy.recorded_at<=p_now;
 SELECT coalesce(max(command.command_revision),0) INTO qualification FROM control.backup_zone_qualification_command command
  WHERE command.instance_id=p_instance AND command.target_revision=p_revision AND command.recorded_at<=p_now;
 IF policies IS DISTINCT FROM (p_context->>'policyHistoryCount')::bigint
  OR qualification IS DISTINCT FROM (p_context->>'qualificationCommandRevision')::bigint THEN RETURN false; END IF;
 SELECT run.run_id,outcome.completed_at INTO latest FROM telemetry.collection_run_v2 run
  JOIN telemetry.collection_run_outcome outcome ON outcome.run_id=run.run_id
  WHERE run.instance_id=p_instance AND run.target_revision=p_revision AND run.collector_id='backups.status'
   AND run.collector_version=2 AND run.output_schema_version=2
   AND run.started_at BETWEEN p_now-interval '7 days' AND p_now AND outcome.completed_at<=p_now
  ORDER BY outcome.completed_at DESC,run.run_id DESC LIMIT 1;
 IF latest.run_id IS DISTINCT FROM (p_context->>'runId')::uuid
  OR latest.completed_at IS DISTINCT FROM (p_context->>'completedAtUtc')::timestamptz THEN RETURN false; END IF;
 IF latest.run_id IS NOT NULL THEN
  SELECT scan.received_at INTO scan_at FROM telemetry.backup_v2_scan scan WHERE scan.instance_id=p_instance
   AND scan.target_revision=p_revision AND scan.run_id=latest.run_id AND scan.received_at BETWEEN p_now-interval '7 days' AND p_now;
 END IF;
 -- Canonical scan/sample graphs are immutable and retention removes the entire group atomically.
 -- This receipt lookup detects group retirement without a database/sample walk on fleet reads.
 RETURN scan_at IS NOT DISTINCT FROM (p_context->>'receivedAtUtc')::timestamptz;
END $context$;
REVOKE ALL ON FUNCTION reporting.overview_backup_context_matches(uuid,bigint,jsonb,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.publish_overview_backup_latest(p_instance uuid,p_revision bigint,p_payload jsonb,p_owner uuid,p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $publish$
DECLARE body jsonb; context jsonb; normalized jsonb; expected jsonb; header record;
 assessed timestamptz; expires timestamptz; now_utc timestamptz; n integer; state integer; faults integer; changed integer;
BEGIN
 PERFORM reporting.assert_overview_backup_read_target(p_instance,p_revision,p_owner,p_fencing);
 -- Administration and collector commits lock these same rows. Serialize context verification with them.
 PERFORM 1 FROM control.observation_target target WHERE target.instance_id=p_instance FOR SHARE;
 PERFORM 1 FROM control.collector_schedule schedule WHERE schedule.instance_id=p_instance AND schedule.collector_id='backups.status' FOR SHARE;
 IF p_payload IS NULL OR octet_length(p_payload::text)>16384 OR NOT reporting.backup_cache_keys(p_payload,ARRAY['schemaVersion','summary'])
  OR p_payload->'schemaVersion' IS DISTINCT FROM '1'::jsonb THEN RAISE EXCEPTION 'Backup cache envelope is invalid' USING ERRCODE='22023'; END IF;
 body:=p_payload->'summary'; context:=body->'context';
 IF NOT reporting.backup_cache_keys(body,ARRAY['context','validUntilUtc','status','full','log','observedFaultDatabases'])
  OR NOT reporting.backup_cache_keys(context,ARRAY['targetId','atUtc','targetRevision','runId','sourceObservedAtUtc','completedAtUtc',
   'receivedAtUtc','sourceZoneId','historyLowerBoundLocal','coverage','evidenceStatus','sourceRows','outputItems','retainedItems',
   'databaseCount','collectorEnabled','policyHistoryCount','qualificationCommandRevision'])
 THEN RAISE EXCEPTION 'Backup cache schema is invalid' USING ERRCODE='22023'; END IF;
 assessed:=(context->>'atUtc')::timestamptz; expires:=(body->>'validUntilUtc')::timestamptz; now_utc:=clock_timestamp();
 IF assessed IS NULL OR expires IS NULL OR NOT isfinite(assessed) OR NOT isfinite(expires)
  OR assessed>now_utc OR assessed<now_utc-interval '10 seconds' OR expires<=assessed OR expires>assessed+interval '2 minutes'
 THEN RAISE EXCEPTION 'Backup cache time bounds are invalid' USING ERRCODE='22023'; END IF;
 SELECT * INTO header FROM reporting.get_backups_v2_header(p_instance,assessed,p_revision);
 expected:=jsonb_build_object('targetId',p_instance,'atUtc',assessed,'targetRevision',header.target_revision,'runId',header.run_id,
  'sourceObservedAtUtc',header.source_observed_at,'completedAtUtc',header.completed_at,'receivedAtUtc',header.received_at,
  'sourceZoneId',header.source_zone_id,'historyLowerBoundLocal',header.history_lower_bound_local,'coverage',header.coverage,
  'evidenceStatus',CASE header.evidence_status WHEN 'not_collected' THEN 1 WHEN 'collection_failed' THEN 2 WHEN 'evidence_not_retained' THEN 3
   WHEN 'withheld_by_limit' THEN 4 WHEN 'no_data' THEN 5 WHEN 'retained' THEN 6 END,
  'sourceRows',header.source_rows,'outputItems',header.output_items,'retainedItems',header.retained_items,'databaseCount',header.database_count,
  'collectorEnabled',header.collector_enabled,'policyHistoryCount',header.policy_history_count,'qualificationCommandRevision',header.qualification_command_revision);
 normalized:=context||jsonb_build_object('targetId',(context->>'targetId')::uuid,'atUtc',assessed,
  'sourceObservedAtUtc',(context->>'sourceObservedAtUtc')::timestamptz,'completedAtUtc',(context->>'completedAtUtc')::timestamptz,
  'receivedAtUtc',(context->>'receivedAtUtc')::timestamptz,'historyLowerBoundLocal',(context->>'historyLowerBoundLocal')::timestamp);
 IF normalized IS DISTINCT FROM expected THEN RETURN false; END IF;
 n:=CASE WHEN header.evidence_status='retained' THEN header.database_count ELSE 0 END;
 IF NOT reporting.backup_cache_counts_valid(body->'full',n) OR NOT reporting.backup_cache_counts_valid(body->'log',n)
  OR jsonb_typeof(body->'status') IS DISTINCT FROM 'number' OR jsonb_typeof(body->'observedFaultDatabases') IS DISTINCT FROM 'number'
 THEN RAISE EXCEPTION 'Backup cache check counts are invalid' USING ERRCODE='22023'; END IF;
 faults:=(body->>'observedFaultDatabases')::integer;
 state:=CASE WHEN header.evidence_status<>'retained' THEN 0
  WHEN (body->'full'->>'policyViolated')::integer+(body->'log'->>'policyViolated')::integer>0 THEN 2
  WHEN NOT header.collector_enabled OR header.coverage<>1 OR (body->'full'->>'indeterminate')::integer+(body->'log'->>'indeterminate')::integer>0 THEN 0
  WHEN (body->'full'->>'disabled')::integer+(body->'log'->>'disabled')::integer=2*n THEN 3
  WHEN (body->'full'->>'withinThreshold')::integer+(body->'log'->>'withinThreshold')::integer>0 THEN 1 ELSE 0 END;
 IF faults NOT BETWEEN 0 AND n OR (body->>'status')::integer<>state THEN RAISE EXCEPTION 'Backup cache aggregate state is invalid' USING ERRCODE='22023'; END IF;
 now_utc:=clock_timestamp();
 IF expires<=now_utc OR NOT reporting.overview_backup_context_matches(p_instance,p_revision,context,now_utc) THEN RETURN false; END IF;
 INSERT INTO reporting.overview_backup_latest AS current_cache(instance_id,target_revision,assessed_at,valid_until,payload)
 VALUES(p_instance,p_revision,assessed,expires,p_payload)
 ON CONFLICT(instance_id) DO UPDATE SET target_revision=EXCLUDED.target_revision,assessed_at=EXCLUDED.assessed_at,
  valid_until=EXCLUDED.valid_until,payload=EXCLUDED.payload
 WHERE (current_cache.target_revision,current_cache.assessed_at)<(EXCLUDED.target_revision,EXCLUDED.assessed_at);
 GET DIAGNOSTICS changed=ROW_COUNT;
 PERFORM control.assert_worker_lease('overview/latest-status',p_owner,p_fencing);
 RETURN changed=1;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'Backup cache payload types are invalid' USING ERRCODE='22023';
END $publish$;
REVOKE ALL ON FUNCTION reporting.publish_overview_backup_latest(uuid,bigint,jsonb,uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.publish_overview_backup_latest(uuid,bigint,jsonb,uuid,bigint) TO sqlobserver_collector;

CREATE FUNCTION reporting.get_overview_backup_latest(p_instance uuid,p_revision bigint,p_cutoff timestamptz)
RETURNS TABLE(payload jsonb) LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC' AS $read$
DECLARE now_utc timestamptz:=clock_timestamp();
BEGIN
 IF p_instance IS NULL OR p_revision IS NULL OR p_revision<=0 OR p_cutoff IS NULL OR NOT isfinite(p_cutoff)
  OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance::text
 THEN RAISE EXCEPTION 'Backup cache read scope is invalid' USING ERRCODE='42501'; END IF;
 IF p_cutoff>now_utc+interval '1 minute' THEN RAISE EXCEPTION 'Backup cache cutoff is invalid' USING ERRCODE='22023'; END IF;
 RETURN QUERY SELECT cached.payload FROM reporting.overview_backup_latest cached
  WHERE cached.instance_id=p_instance AND cached.target_revision=p_revision
   AND cached.assessed_at<=p_cutoff AND p_cutoff<cached.valid_until AND now_utc<cached.valid_until
   AND reporting.overview_backup_context_matches(p_instance,p_revision,cached.payload->'summary'->'context',now_utc);
END $read$;
REVOKE ALL ON FUNCTION reporting.get_overview_backup_latest(uuid,bigint,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_overview_backup_latest(uuid,bigint,timestamptz) TO sqlobserver_server;
