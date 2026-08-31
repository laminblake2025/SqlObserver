-- M12 reports/exports.  This is a reviewed, append-only materialization
-- boundary: reports read existing repository projections and never contact a
-- monitored target.  All timestamps are repository UTC and runs expire after
-- exactly 24 hours.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- The expiry definer is deliberately separate from the migrator owner.  The
-- bootstrap login creates it before this migration changes role; on an
-- already bootstrapped database a missing role is a deployment error rather
-- than an implicit privilege escalation.
DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='sqlobserver_report_expirer') THEN
        IF NOT pg_catalog.pg_has_role(pg_catalog.current_user,'createrole','member') THEN
            RAISE EXCEPTION 'sqlobserver_report_expirer must be provisioned by the bootstrap/admin login' USING ERRCODE='42501';
        END IF;
        EXECUTE 'CREATE ROLE sqlobserver_report_expirer WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='sqlobserver_report_expirer' AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolinherit OR rolreplication OR NOT rolbypassrls)) THEN
        RAISE EXCEPTION 'sqlobserver_report_expirer has unsafe attributes' USING ERRCODE='55000';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership JOIN pg_catalog.pg_roles member_role ON member_role.oid=membership.member WHERE member_role.rolname='sqlobserver_report_expirer')
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership JOIN pg_catalog.pg_roles granted_role ON granted_role.oid=membership.roleid WHERE granted_role.rolname='sqlobserver_report_expirer') THEN
        RAISE EXCEPTION 'sqlobserver_report_expirer must not have role memberships' USING ERRCODE='55000';
    END IF;
END
$role$;
-- Temporary ownership-transfer membership is revoked before this migration
-- commits; the runtime collector receives function EXECUTE only.
GRANT sqlobserver_report_expirer TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;

CREATE TABLE reporting.report_definition
(
    report_kind text NOT NULL,
    definition_version integer NOT NULL,
    title text NOT NULL,
    sections text[] NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (report_kind, definition_version),
    CONSTRAINT ck_report_definition_kind CHECK (report_kind IN ('instance-health','performance-window','incident-evidence','capacity-readiness')),
    CONSTRAINT ck_report_definition_version CHECK (definition_version = 1),
    CONSTRAINT ck_report_definition_sections CHECK (cardinality(sections) BETWEEN 1 AND 16),
    CONSTRAINT ck_report_definition_fixed_shape CHECK (
        (report_kind = 'instance-health' AND sections = ARRAY['health']::text[])
        OR (report_kind = 'performance-window' AND sections = ARRAY['performance']::text[])
        OR (report_kind = 'incident-evidence' AND sections = ARRAY['incidents']::text[])
        OR (report_kind = 'capacity-readiness' AND sections = ARRAY['capacity']::text[]))
);

INSERT INTO reporting.report_definition(report_kind, definition_version, title, sections) VALUES
 ('instance-health',1,'Instance Health',ARRAY['health']),
 ('performance-window',1,'Performance Window',ARRAY['performance']),
 ('incident-evidence',1,'Incident Evidence',ARRAY['incidents']),
 ('capacity-readiness',1,'Capacity/Readiness',ARRAY['capacity'])
ON CONFLICT DO NOTHING;

CREATE TABLE reporting.report_run
(
    run_id uuid PRIMARY KEY,
    target_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    target_revision bigint NOT NULL,
    report_kind text NOT NULL,
    definition_version integer NOT NULL,
    operation_id uuid NOT NULL,
    actor_sid text NOT NULL,
    parameter_digest bytea NOT NULL,
    snapshot_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    state text NOT NULL DEFAULT 'finalized',
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(target_id, operation_id),
    CONSTRAINT ck_report_run_kind CHECK (report_kind IN ('instance-health','performance-window','incident-evidence','capacity-readiness')),
    CONSTRAINT ck_report_run_revision CHECK (target_revision > 0),
    CONSTRAINT ck_report_run_digest CHECK (octet_length(parameter_digest) = 32),
    CONSTRAINT ck_report_run_state CHECK (state = 'finalized'),
    CONSTRAINT ck_report_run_expiry CHECK (expires_at_utc = snapshot_utc + interval '24 hours')
);

CREATE TABLE reporting.report_section_row
(
    run_id uuid NOT NULL REFERENCES reporting.report_run(run_id) ON DELETE CASCADE,
    section text NOT NULL,
    ordinal bigint NOT NULL,
    "values" text[] NOT NULL,
    PRIMARY KEY (run_id, section, ordinal),
    CONSTRAINT ck_report_row_section CHECK (section ~ '^[a-z][a-z0-9-]{0,63}$'),
    CONSTRAINT ck_report_row_ordinal CHECK (ordinal > 0),
    CONSTRAINT ck_report_row_values CHECK (cardinality("values") BETWEEN 1 AND 32)
);

CREATE TABLE reporting.report_export_event
(
    event_id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES reporting.report_run(run_id) ON DELETE CASCADE,
    actor_sid text NOT NULL,
    section text NOT NULL,
    format text NOT NULL,
    row_count integer NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_report_export_format CHECK (format IN ('html','csv')),
    CONSTRAINT ck_report_export_rows CHECK (row_count BETWEEN 0 AND 10000)
);

CREATE TABLE audit.report_activity
(
    activity_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id uuid,
    target_id uuid,
    actor_sid text NOT NULL,
    operation_id uuid,
    activity_kind text NOT NULL,
    outcome text NOT NULL,
    safe_detail text NOT NULL DEFAULT '',
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_report_activity_kind CHECK (activity_kind IN ('create','read','export','deny','timeout','oversize','expire','failure')),
    CONSTRAINT ck_report_activity_outcome CHECK (outcome IN ('accepted','succeeded','denied','failed')),
    CONSTRAINT ck_report_activity_detail CHECK (octet_length(safe_detail) <= 512 AND safe_detail !~ '[[:cntrl:]]')
);

CREATE OR REPLACE FUNCTION reporting.reject_report_mutation()
RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER
SET search_path=pg_catalog,reporting AS $$
BEGIN
 -- Only the dedicated SECURITY DEFINER expiry function runs as the
 -- BYPASSRLS cleanup principal; application roles have no table rights.
 IF TG_OP='DELETE' AND current_user='sqlobserver_report_expirer' THEN RETURN OLD; END IF;
 RAISE EXCEPTION 'reporting objects are immutable' USING ERRCODE='55000';
END $$;
CREATE TRIGGER report_definition_immutable BEFORE UPDATE OR DELETE ON reporting.report_definition FOR EACH ROW EXECUTE FUNCTION reporting.reject_report_mutation();
CREATE TRIGGER report_run_immutable BEFORE UPDATE OR DELETE ON reporting.report_run FOR EACH ROW EXECUTE FUNCTION reporting.reject_report_mutation();
CREATE TRIGGER report_row_immutable BEFORE UPDATE OR DELETE ON reporting.report_section_row FOR EACH ROW EXECUTE FUNCTION reporting.reject_report_mutation();
CREATE TRIGGER report_export_immutable BEFORE UPDATE OR DELETE ON reporting.report_export_event FOR EACH ROW EXECUTE FUNCTION reporting.reject_report_mutation();
CREATE TRIGGER report_activity_immutable BEFORE UPDATE OR DELETE ON audit.report_activity FOR EACH ROW EXECUTE FUNCTION reporting.reject_report_mutation();

CREATE OR REPLACE FUNCTION reporting.validate_report_row()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,reporting AS $$
BEGIN
 IF cardinality(NEW."values") < 1 OR cardinality(NEW."values") > 32 OR (SELECT COALESCE(sum(octet_length(coalesce(item,''))),0) FROM unnest(NEW."values") AS item) > 65536 OR EXISTS (SELECT 1 FROM unnest(NEW."values") AS item WHERE octet_length(item)>4096 OR item ~ '[[:cntrl:]]') THEN RAISE EXCEPTION 'report row contains an unsafe value' USING ERRCODE='22023'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER report_row_validate BEFORE INSERT ON reporting.report_section_row FOR EACH ROW EXECUTE FUNCTION reporting.validate_report_row();

ALTER TABLE reporting.report_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_run FORCE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_definition ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_definition FORCE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_section_row ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_section_row FORCE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_export_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_export_event FORCE ROW LEVEL SECURITY;
-- Server access is always target scoped.  Expiry runs under its dedicated
-- SECURITY DEFINER owner with row_security=off after an atomic lease lock;
-- no caller-settable GUC can widen this policy.
CREATE POLICY report_run_scope ON reporting.report_run USING (target_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY report_definition_read ON reporting.report_definition USING (true);
CREATE POLICY report_row_scope ON reporting.report_section_row USING (EXISTS (SELECT 1 FROM reporting.report_run AS parent_run WHERE parent_run.run_id = report_section_row.run_id AND parent_run.target_id::text = current_setting('sqlobserver.target_scope', true)));
CREATE POLICY report_export_scope ON reporting.report_export_event USING (EXISTS (SELECT 1 FROM reporting.report_run AS parent_run WHERE parent_run.run_id = report_export_event.run_id AND parent_run.target_id::text = current_setting('sqlobserver.target_scope', true)));

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
 SELECT * INTO d FROM reporting.report_definition WHERE report_kind=p_report_kind AND definition_version=1;
 IF NOT FOUND THEN RAISE EXCEPTION 'unknown_report_kind' USING ERRCODE='22023'; END IF;
 IF p_operation_id IS NULL OR p_operation_id='00000000-0000-0000-0000-000000000000' OR octet_length(p_parameter_digest)<>32 THEN RAISE EXCEPTION 'invalid_report_request' USING ERRCODE='22023'; END IF;
 IF p_report_kind='instance-health' AND (p_from_utc IS NOT NULL OR p_to_utc IS NOT NULL) THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 IF p_report_kind<>'instance-health' AND (p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc) THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 IF p_report_kind='capacity-readiness' AND p_to_utc-p_from_utc>interval '31 days' THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 IF p_report_kind IN ('performance-window','incident-evidence') AND p_to_utc-p_from_utc>interval '7 days' THEN RAISE EXCEPTION 'invalid_report_window' USING ERRCODE='22023'; END IF;
 SELECT revision INTO revision FROM control.observation_target WHERE instance_id=p_target_id AND retired_at IS NULL;
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
       FROM reporting.get_instance_health(p_target_id)
       WHERE target_revision=revision AND metric_key IS NOT NULL AND metric_observed_at IS NOT NULL
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
         EXIT WHEN (SELECT count(*) FROM reporting.report_section_row WHERE run_id=new_run_id AND section=section_name) >= 10000;
       END LOOP;
       EXIT WHEN batch_count < 1001 OR (SELECT count(*) FROM reporting.report_section_row WHERE run_id=new_run_id AND section=section_name) >= 10000;
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
         EXIT WHEN (SELECT count(*) FROM reporting.report_section_row WHERE run_id=new_run_id AND section=section_name) >= 10000;
       END LOOP;
       EXIT WHEN batch_count < 101 OR (SELECT count(*) FROM reporting.report_section_row WHERE run_id=new_run_id AND section=section_name) >= 10000;
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
         EXIT WHEN (SELECT count(*) FROM reporting.report_section_row WHERE run_id=new_run_id AND section=section_name) >= 10000;
       END LOOP;
       EXIT WHEN batch_count < 1001 OR (SELECT count(*) FROM reporting.report_section_row WHERE run_id=new_run_id AND section=section_name) >= 10000;
     END LOOP;
   END IF;
   IF (SELECT count(*) FROM reporting.report_section_row AS existing_row WHERE existing_row.run_id=new_run_id AND existing_row.section=section_name) > 10000 THEN RAISE EXCEPTION 'report_row_limit' USING ERRCODE='22023'; END IF;
 END LOOP;
 IF (SELECT COALESCE(sum(octet_length(item)),0) FROM reporting.report_section_row AS row CROSS JOIN LATERAL unnest(row."values") AS item WHERE row.run_id=new_run_id) > 8388608 THEN RAISE EXCEPTION 'report_materialization_bytes' USING ERRCODE='22023'; END IF;
 INSERT INTO audit.report_activity(run_id,target_id,actor_sid,operation_id,activity_kind,outcome) VALUES (new_run_id,p_target_id,p_actor_sid,p_operation_id,'create','succeeded');
 RETURN NEXT;
END $$;

CREATE OR REPLACE FUNCTION reporting.read_report_page(p_target_id uuid,p_run_id uuid,p_section text,p_after_ordinal bigint,p_limit integer)
RETURNS TABLE(ordinal bigint,"values" text[],has_more boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
 SELECT r.ordinal,r."values", count(*) OVER () > least(greatest(p_limit,1),200)
 FROM reporting.report_section_row r JOIN reporting.report_run run ON run.run_id=r.run_id
 WHERE p_limit BETWEEN 1 AND 10000 AND p_target_id::text=current_setting('sqlobserver.target_scope',true) AND run.target_id=p_target_id AND run.run_id=p_run_id AND run.expires_at_utc>clock_timestamp()
   AND r.section IN ('health','performance','incidents','capacity') AND r.section=p_section
   AND EXISTS (SELECT 1 FROM reporting.report_definition definition WHERE definition.report_kind=run.report_kind AND definition.definition_version=run.definition_version AND p_section=ANY(definition.sections))
   AND r.ordinal>greatest(coalesce(p_after_ordinal,0),0)
 ORDER BY r.ordinal LIMIT least(greatest(p_limit,1),201);
$$;

-- Fixed entry points keep the catalog closed for callers and make definition
-- version changes explicit in review.
CREATE OR REPLACE FUNCTION reporting.create_instance_health_report(p_target_id uuid,p_operation_id uuid,p_actor_sid text,p_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting,control,audit SET TimeZone='UTC' AS $$ SELECT * FROM reporting.create_report_run(p_target_id,'instance-health',p_operation_id,p_actor_sid,NULL,NULL,p_digest) $$;
CREATE OR REPLACE FUNCTION reporting.create_performance_window_report(p_target_id uuid,p_operation_id uuid,p_actor_sid text,p_from_utc timestamptz,p_to_utc timestamptz,p_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting,control,audit SET TimeZone='UTC' AS $$ SELECT * FROM reporting.create_report_run(p_target_id,'performance-window',p_operation_id,p_actor_sid,p_from_utc,p_to_utc,p_digest) $$;
CREATE OR REPLACE FUNCTION reporting.create_incident_evidence_report(p_target_id uuid,p_operation_id uuid,p_actor_sid text,p_from_utc timestamptz,p_to_utc timestamptz,p_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting,control,audit SET TimeZone='UTC' AS $$ SELECT * FROM reporting.create_report_run(p_target_id,'incident-evidence',p_operation_id,p_actor_sid,p_from_utc,p_to_utc,p_digest) $$;
CREATE OR REPLACE FUNCTION reporting.create_capacity_readiness_report(p_target_id uuid,p_operation_id uuid,p_actor_sid text,p_from_utc timestamptz,p_to_utc timestamptz,p_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting,control,audit SET TimeZone='UTC' AS $$ SELECT * FROM reporting.create_report_run(p_target_id,'capacity-readiness',p_operation_id,p_actor_sid,p_from_utc,p_to_utc,p_digest) $$;

CREATE OR REPLACE FUNCTION reporting.record_report_export(p_event_id uuid,p_target_id uuid,p_run_id uuid,p_actor_sid text,p_section text,p_format text,p_row_count integer)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting,audit,control AS $$
BEGIN
 IF p_target_id IS NULL OR p_target_id::text<>current_setting('sqlobserver.target_scope',true) THEN RAISE EXCEPTION 'target_scope_denied' USING ERRCODE='42501'; END IF;
 IF NOT EXISTS (SELECT 1 FROM reporting.report_run WHERE run_id=p_run_id AND target_id=p_target_id AND expires_at_utc>clock_timestamp()) THEN RAISE EXCEPTION 'report_expired' USING ERRCODE='22023'; END IF;
 IF p_section IS NULL OR p_format IS NULL OR p_row_count IS NULL OR p_row_count NOT BETWEEN 0 AND 10000 OR NOT EXISTS (SELECT 1 FROM reporting.report_run run JOIN reporting.report_definition definition ON definition.report_kind=run.report_kind AND definition.definition_version=run.definition_version WHERE run.run_id=p_run_id AND p_section=ANY(definition.sections)) THEN RAISE EXCEPTION 'invalid_report_export' USING ERRCODE='22023'; END IF;
 INSERT INTO reporting.report_export_event VALUES(p_event_id,p_run_id,p_actor_sid,p_section,p_format,p_row_count,clock_timestamp());
 INSERT INTO audit.report_activity(run_id,target_id,actor_sid,activity_kind,outcome) VALUES(p_run_id,p_target_id,p_actor_sid,'export','succeeded');
END $$;

CREATE OR REPLACE FUNCTION audit.append_report_activity(p_run_id uuid,p_target_id uuid,p_actor_sid text,p_activity_kind text,p_outcome text,p_safe_detail text DEFAULT '')
RETURNS uuid LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,audit,reporting AS $$
DECLARE id uuid := gen_random_uuid();
BEGIN
 IF p_activity_kind NOT IN ('create','read','export','deny','timeout','oversize','expire','failure') OR p_outcome NOT IN ('accepted','succeeded','denied','failed') OR octet_length(coalesce(p_safe_detail,''))>512 OR coalesce(p_safe_detail,'') ~ '[[:cntrl:]]' THEN RAISE EXCEPTION 'invalid report audit activity' USING ERRCODE='22023'; END IF;
 INSERT INTO audit.report_activity(activity_id,run_id,target_id,actor_sid,activity_kind,outcome,safe_detail) VALUES(id,p_run_id,p_target_id,p_actor_sid,p_activity_kind,p_outcome,coalesce(p_safe_detail,''));
 RETURN id;
END $$;

CREATE OR REPLACE FUNCTION reporting.expire_report_runs(p_limit integer,p_owner uuid,p_fencing bigint)
RETURNS integer LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,reporting,audit,control
SET row_security=off AS $$
DECLARE n integer; lease control.worker_lease%ROWTYPE;
BEGIN
 IF p_limit IS NULL OR p_limit<1 OR p_limit>100 THEN RAISE EXCEPTION 'expiry_limit_out_of_bounds' USING ERRCODE='22023'; END IF;
 -- The lease lock and predicate are one transaction boundary.  A release or
 -- fencing update cannot commit while this function owns the exact row lock.
 SELECT * INTO lease FROM control.worker_lease WHERE work_key='reports/expiry' FOR UPDATE;
 IF NOT FOUND OR lease.owner_execution_id<>p_owner OR lease.fencing_token<>p_fencing OR lease.released_at IS NOT NULL OR lease.expires_at<=clock_timestamp() THEN
   RAISE EXCEPTION 'lease_lost' USING ERRCODE='55000';
 END IF;
 WITH expired AS (SELECT run_id,target_id,actor_sid FROM reporting.report_run WHERE expires_at_utc<=clock_timestamp() ORDER BY expires_at_utc LIMIT p_limit FOR UPDATE)
 INSERT INTO audit.report_activity(run_id,target_id,actor_sid,activity_kind,outcome) SELECT run_id,target_id,actor_sid,'expire','succeeded' FROM expired;
 WITH removed AS (DELETE FROM reporting.report_run WHERE run_id IN (SELECT run_id FROM reporting.report_run WHERE expires_at_utc<=clock_timestamp() ORDER BY expires_at_utc LIMIT p_limit) RETURNING 1) SELECT count(*) INTO n FROM removed;
 RETURN n;
END $$;

REVOKE ALL ON TABLE reporting.report_definition,reporting.report_run,reporting.report_section_row,reporting.report_export_event,audit.report_activity FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.create_report_run(uuid,text,uuid,text,timestamptz,timestamptz,bytea),reporting.read_report_page(uuid,uuid,text,bigint,integer),reporting.record_report_export(uuid,uuid,uuid,text,text,text,integer),reporting.expire_report_runs(integer,uuid,bigint),audit.append_report_activity(uuid,uuid,text,text,text,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION reporting.create_instance_health_report(uuid,uuid,text,bytea),reporting.create_performance_window_report(uuid,uuid,text,timestamptz,timestamptz,bytea),reporting.create_incident_evidence_report(uuid,uuid,text,timestamptz,timestamptz,bytea),reporting.create_capacity_readiness_report(uuid,uuid,text,timestamptz,timestamptz,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reporting.create_report_run(uuid,text,uuid,text,timestamptz,timestamptz,bytea),reporting.read_report_page(uuid,uuid,text,bigint,integer),reporting.record_report_export(uuid,uuid,uuid,text,text,text,integer) TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.create_instance_health_report(uuid,uuid,text,bytea),reporting.create_performance_window_report(uuid,uuid,text,timestamptz,timestamptz,bytea),reporting.create_incident_evidence_report(uuid,uuid,text,timestamptz,timestamptz,bytea),reporting.create_capacity_readiness_report(uuid,uuid,text,timestamptz,timestamptz,bytea) TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION audit.append_report_activity(uuid,uuid,text,text,text,text) TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.expire_report_runs(integer,uuid,bigint) TO sqlobserver_collector;

-- The dedicated BYPASSRLS role has only the fixed rows needed by this one
-- function.  It has no login, inheritance, membership, or schema privileges.
GRANT USAGE ON SCHEMA reporting,control,audit TO sqlobserver_report_expirer;
GRANT SELECT ON TABLE control.worker_lease TO sqlobserver_report_expirer;
GRANT SELECT,DELETE ON TABLE reporting.report_run TO sqlobserver_report_expirer;
GRANT INSERT ON TABLE audit.report_activity TO sqlobserver_report_expirer;
ALTER FUNCTION reporting.expire_report_runs(integer,uuid,bigint) OWNER TO sqlobserver_report_expirer;
REVOKE sqlobserver_report_expirer FROM sqlobserver_migrator;
