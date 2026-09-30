-- Materialize the durable user investigation, never its expired source telemetry.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE reporting.report_definition DROP CONSTRAINT ck_report_definition_kind;
ALTER TABLE reporting.report_definition ADD CONSTRAINT ck_report_definition_kind CHECK
    (report_kind IN ('instance-health','performance-window','incident-evidence','capacity-readiness','saved-investigation'));
ALTER TABLE reporting.report_definition DROP CONSTRAINT ck_report_definition_fixed_shape;
ALTER TABLE reporting.report_definition ADD CONSTRAINT ck_report_definition_fixed_shape CHECK (
    (report_kind='instance-health' AND sections=ARRAY['health']::text[])
    OR (report_kind='performance-window' AND sections=ARRAY['performance']::text[])
    OR (report_kind='incident-evidence' AND sections=ARRAY['incidents']::text[])
    OR (report_kind='capacity-readiness' AND sections=ARRAY['capacity']::text[])
    OR (report_kind='saved-investigation' AND sections=ARRAY['investigation','saved-evidence']::text[]));
ALTER TABLE reporting.report_run DROP CONSTRAINT ck_report_run_kind;
ALTER TABLE reporting.report_run ADD CONSTRAINT ck_report_run_kind CHECK
    (report_kind IN ('instance-health','performance-window','incident-evidence','capacity-readiness','saved-investigation'));
INSERT INTO reporting.report_definition(report_kind,definition_version,title,sections)
    VALUES('saved-investigation',1,'Saved Investigation',ARRAY['investigation','saved-evidence']);

-- The existing cell/row limits remain. Only retained notes may keep CR/LF/TAB.
CREATE OR REPLACE FUNCTION reporting.validate_report_row()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,reporting AS $validate$
DECLARE v_notes boolean;
BEGIN
    v_notes := NEW.section='investigation' AND NEW."values"[1]='notes'
      AND EXISTS(SELECT FROM reporting.report_run r WHERE r.run_id=NEW.run_id AND r.report_kind='saved-investigation');
    IF cardinality(NEW."values") NOT BETWEEN 1 AND 32
       OR (SELECT coalesce(sum(octet_length(coalesce(item,''))),0) FROM unnest(NEW."values") AS item)>65536
       OR EXISTS(SELECT FROM unnest(NEW."values") WITH ORDINALITY AS cell(value,ordinal)
          WHERE octet_length(cell.value)>4096
            OR CASE WHEN v_notes AND cell.ordinal=3 THEN translate(cell.value,E'\r\n\t','') ELSE cell.value END ~ '[[:cntrl:]]') THEN
        RAISE EXCEPTION 'report row contains an unsafe value' USING ERRCODE='22023';
    END IF;
    RETURN NEW;
END
$validate$;
REVOKE ALL ON FUNCTION reporting.validate_report_row()
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION reporting.read_report_page(p_target_id uuid,p_run_id uuid,p_section text,p_after_ordinal bigint,p_limit integer)
RETURNS TABLE(ordinal bigint,"values" text[],has_more boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $page$
 SELECT r.ordinal,r."values",count(*) OVER()>least(greatest(p_limit,1),200)
 FROM reporting.report_section_row r JOIN reporting.report_run run ON run.run_id=r.run_id
 WHERE p_limit BETWEEN 1 AND 10000 AND p_target_id::text=current_setting('sqlobserver.target_scope',true)
   AND run.target_id=p_target_id AND run.run_id=p_run_id AND run.expires_at_utc>clock_timestamp()
   AND r.section IN ('health','performance','incidents','capacity','investigation','saved-evidence') AND r.section=p_section
   AND EXISTS(SELECT FROM reporting.report_definition d WHERE d.report_kind=run.report_kind
     AND d.definition_version=run.definition_version AND p_section=ANY(d.sections))
   AND r.ordinal>greatest(coalesce(p_after_ordinal,0),0)
 ORDER BY r.ordinal LIMIT least(greatest(p_limit,1),201);
$page$;

-- Keep the old fixed materializer intact behind an allowlisted entry point.
-- Its old branches cannot produce the new kind from a caller-supplied window.
ALTER FUNCTION reporting.create_report_run(uuid,text,uuid,text,timestamptz,timestamptz,bytea)
    RENAME TO create_window_report_run;
REVOKE ALL ON FUNCTION reporting.create_window_report_run(uuid,text,uuid,text,timestamptz,timestamptz,bytea)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION reporting.create_report_run(p_target_id uuid,p_report_kind text,p_operation_id uuid,p_actor_sid text,
    p_from_utc timestamptz,p_to_utc timestamptz,p_parameter_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting AS $window$
BEGIN
    IF p_report_kind IS NULL OR p_report_kind NOT IN ('instance-health','performance-window','incident-evidence','capacity-readiness') THEN
        RAISE EXCEPTION 'Window report kind is invalid' USING ERRCODE='22023';
    END IF;
    RETURN QUERY SELECT * FROM reporting.create_window_report_run(p_target_id,p_report_kind,p_operation_id,p_actor_sid,p_from_utc,p_to_utc,p_parameter_digest);
END
$window$;

CREATE FUNCTION reporting.create_saved_investigation_report(p_target_id uuid,p_thread_id uuid,
    p_operation_id uuid,p_actor_sid text,p_parameter_digest bytea)
RETURNS TABLE(run_id uuid,target_revision bigint,definition_version integer,snapshot_utc timestamptz,expires_at_utc timestamptz,state text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control,audit
SET TimeZone='UTC' AS $saved$
DECLARE
    v_saved analytics.saved_incident%ROWTYPE;
    v_existing reporting.report_run%ROWTYPE;
    v_run uuid := gen_random_uuid();
    v_at timestamptz := clock_timestamp();
    v_digest bytea;
    v_bytes bigint;
BEGIN
    PERFORM control.resolve_m10_target_revision(p_target_id,NULL);
    IF p_thread_id IS NULL OR p_thread_id='00000000-0000-0000-0000-000000000000'::uuid
       OR p_operation_id IS NULL OR p_operation_id='00000000-0000-0000-0000-000000000000'::uuid
       OR p_actor_sid IS NULL OR octet_length(p_actor_sid)>184 OR p_actor_sid !~ '^S-1(-[0-9]+){2,}$' THEN
        RAISE EXCEPTION 'Invalid saved investigation report request' USING ERRCODE='22023';
    END IF;
    v_digest := sha256(convert_to(format('{"reportKind":"saved-investigation","threadId":"%s"}',p_thread_id::text),'UTF8'));
    IF p_parameter_digest IS DISTINCT FROM v_digest THEN
        RAISE EXCEPTION 'Saved investigation report digest does not match its thread' USING ERRCODE='22023';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text||':'||p_operation_id::text,0));
    SELECT r.* INTO v_existing FROM reporting.report_run r WHERE r.target_id=p_target_id AND r.operation_id=p_operation_id;
    IF FOUND THEN
        IF v_existing.report_kind<>'saved-investigation' OR v_existing.parameter_digest<>v_digest OR v_existing.actor_sid<>p_actor_sid THEN
            RAISE EXCEPTION 'Saved investigation report operation conflicts' USING ERRCODE='23505';
        END IF;
        IF v_existing.expires_at_utc<=clock_timestamp() THEN
            RAISE EXCEPTION 'Saved investigation report expired' USING ERRCODE='02000';
        END IF;
        RETURN QUERY SELECT v_existing.run_id,v_existing.target_revision,v_existing.definition_version,
            v_existing.snapshot_utc,v_existing.expires_at_utc,v_existing.state;
        RETURN;
    END IF;
    SELECT s.* INTO v_saved FROM analytics.saved_incident s WHERE s.instance_id=p_target_id AND s.thread_id=p_thread_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Saved investigation not found' USING ERRCODE='02000'; END IF;
    INSERT INTO reporting.report_run(run_id,target_id,target_revision,report_kind,definition_version,operation_id,actor_sid,
        parameter_digest,snapshot_utc,expires_at_utc,state,created_at)
      VALUES(v_run,p_target_id,v_saved.target_revision,'saved-investigation',1,p_operation_id,p_actor_sid,
        v_digest,v_at,v_at+interval '24 hours','finalized',v_at);

    -- Split by 1000 Unicode characters: at most 4000 UTF-8 bytes per cell.
    -- Parts preserve text exactly; they are not a truncated preview.
    WITH fields AS (
        SELECT * FROM (VALUES
            (1,'threadId',v_saved.thread_id::text),(2,'title',v_saved.title),
            (3,'fromUtc',v_saved.from_utc::text),(4,'toUtc',v_saved.to_utc::text),
            (5,'savedAtUtc',v_saved.saved_at::text),(6,'targetRevision',v_saved.target_revision::text),
            (7,'evidenceTruncated',v_saved.evidence_truncated::text),
            (8,'captureLimit','At most 200 packets and 1 MiB of existing evidence; saved coverage may be partial.'),
            (9,'emptyCapture','An empty capture does not mean the server was healthy.'),
            (10,'notes',v_saved.notes)) AS f(field_order,field,value)
    ), parts AS (
        SELECT f.*,n,substring(f.value FROM (n-1)*1000+1 FOR 1000) AS part
        FROM fields f CROSS JOIN LATERAL generate_series(1,greatest(1,(char_length(f.value)+999)/1000)) n
    ) INSERT INTO reporting.report_section_row(run_id,section,ordinal,"values")
        SELECT v_run,'investigation',row_number() OVER(ORDER BY field_order,n),ARRAY[field,n::text,part] FROM parts;
    INSERT INTO reporting.report_section_row(run_id,section,ordinal,"values")
      SELECT v_run,'saved-evidence',row_number() OVER(ORDER BY e.occurred_at,e.packet_id,n),ARRAY[
        e.packet_id::text,e.occurred_at::text,e.evidence_kind,e.visibility_state,e.confidence::text,e.source_run_id::text,
        encode(e.source_digest,'hex'),encode(e.identity_digest,'hex'),encode(e.source_cutoff_digest,'hex'),e.source_cutoff_utc::text,
        -- A JSON string envelope makes every CSV part start with a quote,
        -- so formula neutralization cannot alter a fragment at a split boundary.
        n::text,to_jsonb(substring(e.evidence::text FROM (n-1)*1000+1 FOR 1000))::text]
      FROM analytics.saved_incident_evidence e
      CROSS JOIN LATERAL generate_series(1,greatest(1,(char_length(e.evidence::text)+999)/1000)) n
      WHERE e.thread_id=p_thread_id AND e.instance_id=p_target_id;
    SELECT coalesce(sum(octet_length(array_to_string(r."values",''))),0) INTO v_bytes
      FROM reporting.report_section_row r WHERE r.run_id=v_run;
    IF v_bytes>8388608 OR (SELECT count(*) FROM reporting.report_section_row r WHERE r.run_id=v_run)>10000 THEN
        RAISE EXCEPTION 'report_materialization_bytes or report_row_limit exceeded' USING ERRCODE='22023';
    END IF;
    INSERT INTO audit.report_activity(run_id,target_id,actor_sid,operation_id,activity_kind,outcome,safe_detail)
      VALUES(v_run,p_target_id,p_actor_sid,p_operation_id,'create','accepted','saved investigation materialized');
    RETURN QUERY SELECT v_run,v_saved.target_revision,1,v_at,v_at+interval '24 hours','finalized'::text;
END
$saved$;
REVOKE ALL ON FUNCTION reporting.create_report_run(uuid,text,uuid,text,timestamptz,timestamptz,bytea),
    reporting.create_saved_investigation_report(uuid,uuid,uuid,text,bytea) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.create_report_run(uuid,text,uuid,text,timestamptz,timestamptz,bytea),
    reporting.create_saved_investigation_report(uuid,uuid,uuid,text,bytea) TO sqlobserver_server;
