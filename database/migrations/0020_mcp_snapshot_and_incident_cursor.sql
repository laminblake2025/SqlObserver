-- M11 final pagination repair.  This migration is append-only: the prior
-- M11 functions remain available for already-deployed callers while the
-- fixed overloads add collection/computation fences and incident cursors.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Migrations 0016-0019 were initially published without selecting the
-- repository owner role. A database upgraded from that faulty prefix can
-- therefore contain these functions owned by the migration login. Transfer
-- only those known M11 functions while that login still owns them, so this
-- append-only repair can safely replace them below. On a correctly deployed
-- prefix every function is already owned by sqlobserver_migrator and this is
-- a no-op. The conditional is important: sqlobserver_migrator cannot alter
-- an object it does not own after SET ROLE.
DO $m11_repair_function_owners$
DECLARE
    function_signature text;
    function_oid oid;
BEGIN
    FOREACH function_signature IN ARRAY ARRAY[
        'reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)',
        'reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer)',
        'reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)',
        'reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer,timestamptz,uuid)'
    ]::text[]
    LOOP
        function_oid := pg_catalog.to_regprocedure(function_signature)::oid;
        IF function_oid IS NOT NULL
           AND EXISTS
           (
               SELECT 1
               FROM pg_catalog.pg_proc AS pr
               WHERE pr.oid = function_oid
                 AND pg_catalog.pg_get_userbyid(pr.proowner) = session_user
           ) THEN
            EXECUTE format(
                'ALTER FUNCTION %s OWNER TO sqlobserver_migrator',
                function_signature::regprocedure
            );
        END IF;
    END LOOP;
END
$m11_repair_function_owners$;

SET LOCAL ROLE sqlobserver_migrator;

CREATE OR REPLACE FUNCTION reporting.list_metric_series(
 p_instance_id uuid,
 p_target_revision bigint,
 p_from_utc timestamptz,
 p_to_utc timestamptz,
 p_metric_key text,
 p_limit integer,
 p_snapshot_utc timestamptz,
 p_cursor_at timestamptz,
 p_cursor_run_id uuid,
 p_cursor_metric_key text,
 p_cursor_dimensions jsonb)
RETURNS TABLE(observed_at timestamptz,run_id uuid,metric_key text,metric_value double precision,dimensions jsonb)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,control
SET TimeZone='UTC' AS $$
 SELECT h.observed_at,h.run_id,h.metric_key,h.metric_value,h.dimensions
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision
   AND h.metric_key=p_metric_key AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc
   AND h.observed_at<=p_snapshot_utc AND h.collected_at<=p_snapshot_utc
   AND p_limit BETWEEN 1 AND 1001 AND p_to_utc>p_from_utc
   AND p_to_utc-p_from_utc<=interval '31 days'
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND ((p_cursor_at IS NULL AND p_cursor_run_id IS NULL AND p_cursor_metric_key IS NULL AND p_cursor_dimensions IS NULL)
        OR (p_cursor_at IS NOT NULL AND p_cursor_run_id IS NOT NULL AND p_cursor_metric_key IS NOT NULL AND p_cursor_dimensions IS NOT NULL
            AND (h.observed_at,h.run_id,h.metric_key,h.dimensions)>(p_cursor_at,p_cursor_run_id,p_cursor_metric_key,p_cursor_dimensions)))
 ORDER BY h.observed_at,h.run_id,h.metric_key,h.dimensions LIMIT p_limit;
$$;

CREATE OR REPLACE FUNCTION reporting.get_m10_forecast_scoped(
 p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_dimensions jsonb,
 p_horizon interval,p_snapshot_utc timestamptz,p_limit integer,
 p_cursor_horizon_start timestamptz,p_cursor_forecast_id uuid)
RETURNS SETOF analytics.metric_forecast LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $$
 SELECT f.* FROM analytics.metric_forecast f
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND f.instance_id=p_instance_id AND f.target_revision=p_target_revision
   AND f.metric_key=p_metric_key AND f.dimensions=p_dimensions
   AND f.computed_at<=p_snapshot_utc
   AND f.horizon_start<=p_snapshot_utc+p_horizon AND f.horizon_end>p_snapshot_utc
   AND p_horizon BETWEEN interval '1 hour' AND interval '366 days'
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND p_limit BETWEEN 1 AND 201
   AND ((p_cursor_horizon_start IS NULL AND p_cursor_forecast_id IS NULL)
        OR (p_cursor_horizon_start IS NOT NULL AND p_cursor_forecast_id IS NOT NULL
            AND (f.horizon_start,f.forecast_id)>(p_cursor_horizon_start,p_cursor_forecast_id)))
 ORDER BY f.horizon_start,f.forecast_id LIMIT p_limit;
$$;

CREATE OR REPLACE FUNCTION reporting.search_m10_diagnostics(
 p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,
 p_limit integer,p_cursor_at timestamptz,p_cursor_event_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,event_id uuid,event_kind text,severity smallint,safe_metadata jsonb,collected_at timestamptz,target_revision bigint)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,events,control SET TimeZone='UTC' AS $$
 SELECT d.occurred_at,d.event_id,d.event_kind,d.severity,d.safe_metadata,d.collected_at,p_target_revision
 FROM events.diagnostic_event d
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND d.instance_id=p_instance_id AND d.collected_at<=p_snapshot_utc
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND p_from_utc>=p_snapshot_utc-interval '7 days' AND p_to_utc>p_from_utc
   AND (p_cursor_at IS NULL AND p_cursor_event_id IS NULL
        OR p_cursor_at IS NOT NULL AND p_cursor_event_id IS NOT NULL
           AND (d.occurred_at,d.event_id)>(p_cursor_at,p_cursor_event_id))
   AND d.occurred_at>=p_from_utc AND d.occurred_at<p_to_utc
 ORDER BY d.occurred_at,d.event_id LIMIT least(greatest(p_limit,1),101);
$$;

-- Evidence and generations are independent streams.  Each function accepts
-- one complete stream cursor, allowing the other stream to be exhausted
-- without losing a row from a page continuation.
CREATE OR REPLACE FUNCTION reporting.get_m10_incident_evidence(
 p_instance_id uuid,p_target_revision bigint,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz,
 p_cursor_occurred_at timestamptz,p_cursor_packet_id uuid)
RETURNS TABLE(occurred_at timestamptz,packet_id uuid,evidence_kind text,source_run_id uuid,source_digest bytea,identity_digest bytea,source_cutoff_digest bytea,source_cutoff_utc timestamptz,evidence jsonb,confidence numeric,visibility_state text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT e.occurred_at,e.packet_id,e.evidence_kind,e.source_run_id,e.source_digest,e.identity_digest,e.source_cutoff_digest,e.source_cutoff_utc,e.evidence,e.confidence,e.visibility_state
 FROM analytics.evidence_packet_v2 e
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND e.instance_id=p_instance_id AND e.target_revision=p_target_revision
   AND EXISTS (SELECT 1 FROM analytics.incident_generation g WHERE g.instance_id=e.instance_id AND g.target_revision=e.target_revision AND g.thread_id=p_thread_id AND g.evidence_packet_id=e.packet_id AND g.observed_at<=p_snapshot_utc)
   AND e.occurred_at<=p_snapshot_utc AND (e.source_cutoff_utc IS NULL OR e.source_cutoff_utc<=p_snapshot_utc)
   AND p_limit BETWEEN 1 AND 257
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND (p_cursor_occurred_at IS NULL AND p_cursor_packet_id IS NULL
        OR p_cursor_occurred_at IS NOT NULL AND p_cursor_packet_id IS NOT NULL
           AND (e.occurred_at,e.packet_id)>(p_cursor_occurred_at,p_cursor_packet_id))
 ORDER BY e.occurred_at,e.packet_id LIMIT p_limit;
$$;

CREATE OR REPLACE FUNCTION reporting.get_m10_incident_generations(
 p_instance_id uuid,p_target_revision bigint,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz,
 p_cursor_generation bigint)
RETURNS TABLE(thread_id uuid,generation bigint,observed_at timestamptz,correlation_digest bytea,supersedes_previous boolean,evidence_packet_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT g.thread_id,g.generation,g.observed_at,g.correlation_digest,g.supersedes_previous,g.evidence_packet_id
 FROM analytics.incident_generation g
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND g.instance_id=p_instance_id AND g.target_revision=p_target_revision AND g.thread_id=p_thread_id
   AND g.observed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 257
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND (p_cursor_generation IS NULL OR g.generation>p_cursor_generation)
 ORDER BY g.generation LIMIT p_limit;
$$;

REVOKE ALL ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb),reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer,timestamptz,uuid),reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz),reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz,timestamptz,uuid),reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb),reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer,timestamptz,uuid),reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz),reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz,timestamptz,uuid),reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz,bigint) TO sqlobserver_server;
