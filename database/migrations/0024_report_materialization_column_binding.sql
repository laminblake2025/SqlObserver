-- Forward-only repair: PL/pgSQL output parameters shadow same-named columns.
-- Preserve the existing signature, owner, ACL, scope checks and materialization bounds.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE OR REPLACE FUNCTION reporting.create_report_run(
 p_target_id uuid, p_report_kind text, p_operation_id uuid, p_actor_sid text,
 p_from_utc timestamptz, p_to_utc timestamptz, p_parameter_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,reporting,control,audit SET TimeZone='UTC' AS $$
DECLARE d reporting.report_definition%ROWTYPE; revision bigint; existing reporting.report_run%ROWTYPE; section_name text; new_run_id uuid;
 metric_row record; incident_row record; metric_cursor_at timestamptz; metric_cursor_run uuid; metric_cursor_key text; metric_cursor_dimensions jsonb;
 incident_cursor_at timestamptz; incident_cursor_id uuid; batch_count integer; next_ordinal bigint;
BEGIN
 IF p_target_id IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope',true) THEN RAISE EXCEPTION 'target_scope_denied' USING ERRCODE='42501'; END IF;
 SELECT definition.* INTO d FROM reporting.report_definition AS definition WHERE definition.report_kind=p_report_kind AND definition.definition_version=1;
 IF NOT FOUND THEN RAISE EXCEPTION 'unknown_report_kind' USING ERRCODE='22023'; END IF;
 IF p_operation_id IS NULL OR p_operation_id='00000000-0000-0000-0000-000000000000' OR octet_length(p_parameter_digest)<>32 THEN RAISE EXCEPTION 'invalid_report_request' USING ERRCODE='22023'; END IF;
 IF p_report_kind='instance-health' AND (p_from_utc IS NOT NULL OR p_to_utc IS NOT NULL) THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 IF p_report_kind<>'instance-health' AND (p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc) THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 IF p_report_kind='capacity-readiness' AND p_to_utc-p_from_utc>interval '31 days' THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 IF p_report_kind IN ('performance-window','incident-evidence') AND p_to_utc-p_from_utc>interval '7 days' THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 SELECT target.revision INTO revision FROM control.observation_target AS target WHERE target.instance_id=p_target_id AND target.retired_at IS NULL;
 IF revision IS NULL THEN RAISE EXCEPTION 'target_not_found' USING ERRCODE='02000'; END IF;
 SELECT * INTO existing FROM reporting.report_run WHERE target_id=p_target_id AND operation_id=p_operation_id;
 IF FOUND THEN
   IF existing.parameter_digest<>p_parameter_digest OR existing.report_kind<>p_report_kind THEN RAISE EXCEPTION 'idempotency_conflict' USING ERRCODE='23505'; END IF;
   RETURN QUERY SELECT existing.run_id,existing.target_revision,existing.definition_version,existing.snapshot_utc,existing.expires_at_utc,existing.state; RETURN;
 END IF;
 snapshot_utc := clock_timestamp(); expires_at_utc := snapshot_utc + interval '24 hours'; new_run_id := gen_random_uuid(); run_id := new_run_id; target_revision := revision; definition_version := 1; state := 'finalized';
 INSERT INTO reporting.report_run VALUES (new_run_id,p_target_id,revision,p_report_kind,1,p_operation_id,p_actor_sid,p_parameter_digest,snapshot_utc,expires_at_utc,'finalized',snapshot_utc);
 -- Materialization is intentionally all-or-nothing.  The initial fixed
 -- Producers use only fixed sanitized repository projections.
 FOREACH section_name IN ARRAY d.sections LOOP
   IF section_name='health' THEN
     INSERT INTO reporting.report_section_row(run_id,section,ordinal,"values")
       SELECT new_run_id,section_name,row_number() OVER (ORDER BY metric_observed_at,metric_key,metric_sample_id),ARRAY[metric_observed_at::text,metric_key,metric_value::text,health_state]
       FROM reporting.get_instance_health(p_target_id) AS health
       WHERE health.target_revision=revision AND metric_key IS NOT NULL AND metric_observed_at IS NOT NULL
         AND metric_observed_at <= snapshot_utc
         AND (last_attempt_at IS NULL OR last_attempt_at <= snapshot_utc)
         AND (last_success_at IS NULL OR last_success_at <= snapshot_utc)
       LIMIT 2000;
   ELSIF section_name='performance' THEN
     next_ordinal := 1;
     metric_cursor_at := NULL; metric_cursor_run := NULL; metric_cursor_key := NULL; metric_cursor_dimensions := NULL;
     LOOP
       batch_count := 0;
       FOR metric_row IN SELECT * FROM reporting.list_metric_series(p_target_id,revision,p_from_utc,p_to_utc,'host.cpu.percent',1001,snapshot_utc,metric_cursor_at,metric_cursor_run,metric_cursor_key,metric_cursor_dimensions) LOOP
         batch_count := batch_count + 1;
         INSERT INTO reporting.report_section_row(run_id,section,ordinal,"values") VALUES(new_run_id,section_name,next_ordinal,ARRAY[metric_row.observed_at::text,metric_row.metric_key,metric_row.metric_value::text,'available']); next_ordinal := next_ordinal + 1;
         metric_cursor_at := metric_row.observed_at; metric_cursor_run := metric_row.run_id; metric_cursor_key := metric_row.metric_key; metric_cursor_dimensions := metric_row.dimensions;
         EXIT WHEN (SELECT count(*) FROM reporting.report_section_row AS section_row WHERE section_row.run_id=new_run_id AND section_row.section=section_name) >= 10000;
       END LOOP;
       EXIT WHEN batch_count < 1001 OR (SELECT count(*) FROM reporting.report_section_row AS section_row WHERE section_row.run_id=new_run_id AND section_row.section=section_name) >= 10000;
     END LOOP;
   ELSIF section_name='incidents' THEN
     next_ordinal := 1;
     incident_cursor_at := NULL; incident_cursor_id := NULL;
     LOOP
       batch_count := 0;
       FOR incident_row IN SELECT * FROM reporting.search_m10_diagnostics(p_target_id,revision,p_from_utc,p_to_utc,101,incident_cursor_at,incident_cursor_id,snapshot_utc) LOOP
         batch_count := batch_count + 1;
         INSERT INTO reporting.report_section_row(run_id,section,ordinal,"values") VALUES(new_run_id,section_name,next_ordinal,ARRAY[incident_row.occurred_at::text,incident_row.severity::text,incident_row.event_kind,'available']); next_ordinal := next_ordinal + 1;
         incident_cursor_at := incident_row.occurred_at; incident_cursor_id := incident_row.event_id;
         EXIT WHEN (SELECT count(*) FROM reporting.report_section_row AS section_row WHERE section_row.run_id=new_run_id AND section_row.section=section_name) >= 10000;
       END LOOP;
       EXIT WHEN batch_count < 101 OR (SELECT count(*) FROM reporting.report_section_row AS section_row WHERE section_row.run_id=new_run_id AND section_row.section=section_name) >= 10000;
     END LOOP;
   ELSIF section_name='capacity' THEN
     next_ordinal := 1;
     metric_cursor_at := NULL; metric_cursor_run := NULL; metric_cursor_key := NULL; metric_cursor_dimensions := NULL;
     LOOP
       batch_count := 0;
       FOR metric_row IN SELECT * FROM reporting.list_metric_series(p_target_id,revision,p_from_utc,p_to_utc,'host.volume.free_bytes',1001,snapshot_utc,metric_cursor_at,metric_cursor_run,metric_cursor_key,metric_cursor_dimensions) LOOP
         batch_count := batch_count + 1;
         INSERT INTO reporting.report_section_row(run_id,section,ordinal,"values") VALUES(new_run_id,section_name,next_ordinal,ARRAY[metric_row.observed_at::text,metric_row.metric_key,metric_row.metric_value::text,'available']); next_ordinal := next_ordinal + 1;
         metric_cursor_at := metric_row.observed_at; metric_cursor_run := metric_row.run_id; metric_cursor_key := metric_row.metric_key; metric_cursor_dimensions := metric_row.dimensions;
         EXIT WHEN (SELECT count(*) FROM reporting.report_section_row AS section_row WHERE section_row.run_id=new_run_id AND section_row.section=section_name) >= 10000;
       END LOOP;
       EXIT WHEN batch_count < 1001 OR (SELECT count(*) FROM reporting.report_section_row AS section_row WHERE section_row.run_id=new_run_id AND section_row.section=section_name) >= 10000;
     END LOOP;
   END IF;
   IF (SELECT count(*) FROM reporting.report_section_row AS existing_row WHERE existing_row.run_id=new_run_id AND existing_row.section=section_name) > 10000 THEN RAISE EXCEPTION 'report_row_limit' USING ERRCODE='22023'; END IF;
 END LOOP;
 IF (SELECT COALESCE(sum(octet_length(item)),0) FROM reporting.report_section_row AS row CROSS JOIN LATERAL unnest(row."values") AS item WHERE row.run_id=new_run_id) > 8388608 THEN RAISE EXCEPTION 'report_materialization_bytes' USING ERRCODE='22023'; END IF;
 INSERT INTO audit.report_activity(run_id,target_id,actor_sid,operation_id,activity_kind,outcome) VALUES (new_run_id,p_target_id,p_actor_sid,p_operation_id,'create','succeeded');
 RETURN NEXT;
END $$;

