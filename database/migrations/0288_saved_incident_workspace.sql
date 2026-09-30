-- User investigations preserve a window, notes and a bounded server-owned
-- evidence snapshot. Originals may subsequently expire without changing it.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TimeZone = 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

CREATE TABLE analytics.saved_incident
(
    thread_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL,
    operation_id uuid NOT NULL UNIQUE CHECK(operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    actor_sid text NOT NULL,
    request_digest bytea NOT NULL CHECK(octet_length(request_digest)=32),
    from_utc timestamptz NOT NULL,
    to_utc timestamptz NOT NULL,
    saved_at timestamptz NOT NULL,
    title text NOT NULL CHECK(octet_length(title) BETWEEN 1 AND 200 AND title !~ '[[:cntrl:]]'),
    notes text NOT NULL CHECK(octet_length(notes)<=8192),
    evidence_truncated boolean NOT NULL,
    FOREIGN KEY(thread_id,instance_id,target_revision)
        REFERENCES analytics.incident_thread(thread_id,instance_id,target_revision),
    CHECK(to_utc>from_utc AND to_utc-from_utc<=interval '7 days' AND to_utc<=saved_at)
);

CREATE TABLE analytics.saved_incident_evidence
(
    thread_id uuid NOT NULL REFERENCES analytics.saved_incident(thread_id),
    instance_id uuid NOT NULL,
    target_revision bigint NOT NULL,
    occurred_at timestamptz NOT NULL,
    packet_id uuid NOT NULL,
    evidence_kind text NOT NULL CHECK(octet_length(evidence_kind) BETWEEN 1 AND 64),
    source_run_id uuid,
    source_digest bytea NOT NULL CHECK(octet_length(source_digest)=32),
    identity_digest bytea NOT NULL CHECK(octet_length(identity_digest)=32),
    source_cutoff_digest bytea NOT NULL CHECK(octet_length(source_cutoff_digest)=32),
    source_cutoff_utc timestamptz,
    evidence jsonb NOT NULL CHECK(jsonb_typeof(evidence)='object' AND octet_length(evidence::text)<=262144),
    confidence numeric(5,4) CHECK(confidence BETWEEN 0 AND 1),
    visibility_state text NOT NULL CHECK(visibility_state IN ('complete','partial','unavailable','unsupported')),
    PRIMARY KEY(thread_id,occurred_at,packet_id),
    FOREIGN KEY(thread_id,instance_id,target_revision)
        REFERENCES analytics.incident_thread(thread_id,instance_id,target_revision)
);

ALTER TABLE analytics.saved_incident ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.saved_incident FORCE ROW LEVEL SECURITY;
ALTER TABLE analytics.saved_incident_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.saved_incident_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY saved_incident_scope ON analytics.saved_incident
    USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY saved_incident_owner ON analytics.saved_incident
    TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY saved_incident_evidence_scope ON analytics.saved_incident_evidence
    USING(instance_id::text=current_setting('sqlobserver.target_scope',true))
    WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY saved_incident_evidence_owner ON analytics.saved_incident_evidence
    TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON analytics.saved_incident,analytics.saved_incident_evidence
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.read_saved_incident(p_instance_id uuid,p_thread_id uuid)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $read$
DECLARE result jsonb;
BEGIN
    PERFORM control.resolve_m10_target_revision(p_instance_id,NULL);
    SELECT jsonb_build_object(
        'targetId',s.instance_id,'targetRevision',s.target_revision,'threadId',s.thread_id,
        'operationId',s.operation_id,'fromUtc',s.from_utc,'toUtc',s.to_utc,
        'savedAtUtc',s.saved_at,'title',s.title,'notes',s.notes,'evidenceTruncated',s.evidence_truncated,
        'evidence',coalesce((SELECT jsonb_agg(jsonb_build_object(
            'packetId',e.packet_id,'occurredAtUtc',e.occurred_at,'evidenceKind',e.evidence_kind,
            'sourceRunId',e.source_run_id,'sourceSha256',encode(e.source_digest,'hex'),
            'identitySha256',encode(e.identity_digest,'hex'),'sourceCutoffSha256',encode(e.source_cutoff_digest,'hex'),
            'sourceCutoffUtc',e.source_cutoff_utc,'visibilityState',e.visibility_state,
            'confidence',e.confidence,'evidence',e.evidence) ORDER BY e.occurred_at,e.packet_id)
            FROM analytics.saved_incident_evidence e WHERE e.thread_id=s.thread_id
              AND e.instance_id=s.instance_id AND e.target_revision=s.target_revision),'[]'::jsonb))
      INTO result FROM analytics.saved_incident s
      WHERE s.instance_id=p_instance_id AND s.thread_id=p_thread_id;
    RETURN result;
END
$read$;

CREATE FUNCTION control.save_incident(p_instance_id uuid,p_expected_revision bigint,p_operation_id uuid,
    p_from_utc timestamptz,p_to_utc timestamptz,p_title text,p_notes text,p_actor_sid text,p_correlation_id uuid)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control,reporting,audit SET TimeZone='UTC' AS $save$
DECLARE
    v_existing analytics.saved_incident%ROWTYPE;
    v_thread uuid := gen_random_uuid();
    v_saved timestamptz := statement_timestamp();
    v_digest bytea;
    v_count integer;
    v_truncated boolean;
BEGIN
    IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN
        RAISE EXCEPTION 'Incident target scope is required' USING ERRCODE='42501';
    END IF;
    IF p_expected_revision IS NULL OR p_expected_revision<1 OR p_operation_id IS NULL
       OR p_operation_id='00000000-0000-0000-0000-000000000000'::uuid
       OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
       OR p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc
       OR p_to_utc-p_from_utc>interval '7 days' OR p_to_utc>v_saved
       OR p_title IS NULL OR btrim(p_title)='' OR octet_length(p_title)>200 OR p_title ~ '[[:cntrl:]]'
       OR p_notes IS NULL OR octet_length(p_notes)>8192
       OR translate(p_notes,E'\r\n\t','') ~ '[[:cntrl:]]'
       OR p_actor_sid IS NULL OR octet_length(p_actor_sid)>184 OR p_actor_sid !~ '^S-1(-[0-9]+){2,}$' THEN
        RAISE EXCEPTION 'Invalid saved incident request' USING ERRCODE='22023';
    END IF;
    v_digest := sha256(convert_to(jsonb_build_object('targetId',p_instance_id,'targetRevision',p_expected_revision,
        'operationId',p_operation_id,'fromUtc',p_from_utc,'toUtc',p_to_utc,'title',p_title,'notes',p_notes,
        'actorSid',p_actor_sid)::text,'UTF8'));
    -- Serialize retries before looking for an existing result. Correlation IDs
    -- may differ on retries; actor and exact command must remain identical.
    PERFORM pg_advisory_xact_lock(hashtextextended(p_operation_id::text,0));
    SELECT * INTO v_existing FROM analytics.saved_incident WHERE operation_id=p_operation_id;
    IF FOUND THEN
        IF v_existing.instance_id<>p_instance_id OR v_existing.request_digest<>v_digest THEN
            RAISE EXCEPTION 'Incident operation conflicts with its original request'
                USING ERRCODE='40001',CONSTRAINT='saved_incident_operation_conflict';
        END IF;
        RETURN reporting.read_saved_incident(p_instance_id,v_existing.thread_id);
    END IF;
    PERFORM control.resolve_m10_target_revision(p_instance_id,p_expected_revision);
    IF NOT EXISTS(SELECT FROM control.observation_target t WHERE t.instance_id=p_instance_id AND t.lifecycle_state='active') THEN
        RAISE EXCEPTION 'Incident target is inactive' USING ERRCODE='40001';
    END IF;
    INSERT INTO analytics.incident_thread(thread_id,instance_id,target_revision,opened_at,state,current_generation,summary)
      VALUES(v_thread,p_instance_id,p_expected_revision,p_from_utc,'open',1,
        jsonb_build_object('origin','user','windowFromUtc',p_from_utc,'windowToUtc',p_to_utc));
    INSERT INTO analytics.saved_incident(thread_id,instance_id,target_revision,operation_id,actor_sid,request_digest,
        from_utc,to_utc,saved_at,title,notes,evidence_truncated)
      VALUES(v_thread,p_instance_id,p_expected_revision,p_operation_id,p_actor_sid,v_digest,
        p_from_utc,p_to_utc,v_saved,p_title,p_notes,false);

    WITH candidates AS MATERIALIZED (
        SELECT e.*,sum(octet_length(e.evidence::text)) OVER(ORDER BY e.occurred_at DESC,e.packet_id DESC) AS bytes,
            row_number() OVER(ORDER BY e.occurred_at DESC,e.packet_id DESC) AS ordinal
        FROM (SELECT * FROM analytics.evidence_packet_v2 packet
            WHERE packet.instance_id=p_instance_id AND packet.target_revision=p_expected_revision
              AND packet.occurred_at>=p_from_utc AND packet.occurred_at<p_to_utc
              AND (packet.source_cutoff_utc IS NULL OR packet.source_cutoff_utc<=v_saved)
            ORDER BY packet.occurred_at DESC,packet.packet_id DESC LIMIT 201) e
    ), captured AS (
        INSERT INTO analytics.saved_incident_evidence(thread_id,instance_id,target_revision,occurred_at,packet_id,
            evidence_kind,source_run_id,source_digest,identity_digest,source_cutoff_digest,source_cutoff_utc,evidence,confidence,visibility_state)
        SELECT v_thread,instance_id,target_revision,occurred_at,packet_id,evidence_kind,source_run_id,
            source_digest,identity_digest,source_cutoff_digest,source_cutoff_utc,evidence,confidence,visibility_state
        FROM candidates WHERE ordinal<=200 AND bytes<=1048576 RETURNING 1
    ) SELECT (SELECT count(*)::integer FROM captured),
        EXISTS(SELECT FROM candidates WHERE ordinal>200 OR bytes>1048576) INTO v_count,v_truncated;

    UPDATE analytics.saved_incident SET evidence_truncated=v_truncated WHERE thread_id=v_thread;
    INSERT INTO analytics.incident_generation(instance_id,target_revision,thread_id,generation,observed_at,
        state,evidence_packet_id,correlation_digest,supersedes_previous,details)
      SELECT p_instance_id,p_expected_revision,v_thread,row_number() OVER(ORDER BY e.occurred_at,e.packet_id),
        v_saved,'open',e.packet_id,v_digest,false,'{}'::jsonb
      FROM analytics.saved_incident_evidence e WHERE e.thread_id=v_thread;
    IF v_count=0 THEN
        INSERT INTO analytics.incident_generation(instance_id,target_revision,thread_id,generation,observed_at,
            state,correlation_digest,details)
          VALUES(p_instance_id,p_expected_revision,v_thread,1,v_saved,'open',v_digest,'{}'::jsonb);
    END IF;
    UPDATE analytics.incident_thread SET current_generation=greatest(v_count,1) WHERE thread_id=v_thread;
    INSERT INTO audit.activity(activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,
        subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details)
      VALUES(gen_random_uuid(),'user',p_actor_sid,'incident.save','allowed','succeeded','incident',v_thread::text,
        p_correlation_id,v_digest,jsonb_build_object('targetId',p_instance_id,'threadId',v_thread,
          'operationId',p_operation_id,'evidenceCount',v_count,'evidenceTruncated',v_truncated));
    RETURN reporting.read_saved_incident(p_instance_id,v_thread);
END
$save$;

-- Both existing overloads flow through this paged projection. For saved
-- investigations use durable copies, never a changing original packet.
CREATE OR REPLACE FUNCTION reporting.get_m10_incident_evidence(
 p_instance_id uuid,p_target_revision bigint,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz,
 p_cursor_occurred_at timestamptz,p_cursor_packet_id uuid)
RETURNS TABLE(occurred_at timestamptz,packet_id uuid,evidence_kind text,source_run_id uuid,source_digest bytea,
 identity_digest bytea,source_cutoff_digest bytea,source_cutoff_utc timestamptz,evidence jsonb,confidence numeric,visibility_state text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $evidence$
 WITH source AS (
   SELECT e.occurred_at,e.packet_id,e.evidence_kind,e.source_run_id,e.source_digest,e.identity_digest,
     e.source_cutoff_digest,e.source_cutoff_utc,e.evidence,e.confidence,e.visibility_state
   FROM analytics.saved_incident_evidence e JOIN analytics.saved_incident s ON s.thread_id=e.thread_id
   WHERE e.thread_id=p_thread_id AND e.instance_id=p_instance_id AND e.target_revision=p_target_revision
     AND s.saved_at<=p_snapshot_utc
   UNION ALL
   SELECT e.occurred_at,e.packet_id,e.evidence_kind,e.source_run_id,e.source_digest,e.identity_digest,
     e.source_cutoff_digest,e.source_cutoff_utc,e.evidence,e.confidence,e.visibility_state
   FROM analytics.evidence_packet_v2 e
   WHERE e.instance_id=p_instance_id AND e.target_revision=p_target_revision
     AND NOT EXISTS(SELECT FROM analytics.saved_incident s WHERE s.thread_id=p_thread_id)
     AND EXISTS(SELECT FROM analytics.incident_generation g WHERE g.instance_id=e.instance_id
       AND g.target_revision=e.target_revision AND g.thread_id=p_thread_id
       AND g.evidence_packet_id=e.packet_id AND g.observed_at<=p_snapshot_utc)
 ) SELECT e.* FROM source e
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND e.occurred_at<=p_snapshot_utc AND (e.source_cutoff_utc IS NULL OR e.source_cutoff_utc<=p_snapshot_utc)
   AND p_limit BETWEEN 1 AND 257 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND (p_cursor_occurred_at IS NULL AND p_cursor_packet_id IS NULL
        OR p_cursor_occurred_at IS NOT NULL AND p_cursor_packet_id IS NOT NULL
          AND (e.occurred_at,e.packet_id)>(p_cursor_occurred_at,p_cursor_packet_id))
 ORDER BY e.occurred_at,e.packet_id LIMIT p_limit;
$evidence$;

CREATE OR REPLACE FUNCTION reporting.get_m10_incident_evidence(
 p_instance_id uuid,p_target_revision bigint,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,packet_id uuid,evidence_kind text,source_run_id uuid,source_digest bytea,
 identity_digest bytea,source_cutoff_digest bytea,source_cutoff_utc timestamptz,evidence jsonb,confidence numeric,visibility_state text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $legacy$
 SELECT * FROM reporting.get_m10_incident_evidence(p_instance_id,p_target_revision,p_thread_id,p_limit,p_snapshot_utc,NULL,NULL)
 WHERE p_limit BETWEEN 1 AND 256;
$legacy$;

REVOKE ALL ON FUNCTION reporting.read_saved_incident(uuid,uuid),
    control.save_incident(uuid,bigint,uuid,timestamptz,timestamptz,text,text,text,uuid)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.read_saved_incident(uuid,uuid),
    control.save_incident(uuid,bigint,uuid,timestamptz,timestamptz,text,text,text,uuid) TO sqlobserver_server;
