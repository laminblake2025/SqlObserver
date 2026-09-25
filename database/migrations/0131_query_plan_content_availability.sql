-- Plan metadata reports whether the latest matching run has an exact,
-- target-bound protected plan link. It never returns plan XML.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.get_query_plan_metadata(
    p_instance_id uuid, p_database_id integer,
    p_query_fingerprint bytea, p_plan_fingerprint bytea)
RETURNS TABLE(plan_fingerprint bytea, source events.query_performance_source,
    observed_at timestamptz, coverage text, content_available boolean)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, control, events, security
AS $fn$
    SELECT p.plan_fingerprint, o.source, o.observed_at, r.coverage,
           EXISTS (
               SELECT 1
               FROM events.query_performance_plan_content_link AS link
               JOIN security.protected_diagnostic_payload AS payload
                 ON payload.payload_id = link.content_reference
                AND payload.instance_id = link.instance_id
                AND payload.payload_kind = 'execution_plan'
               WHERE link.collection_run_id = o.collection_run_id
                 AND link.instance_id = p_instance_id
                 AND link.database_id = p.database_id
                 AND link.query_fingerprint = p.query_fingerprint
                 AND link.plan_fingerprint = p.plan_fingerprint
                 AND link.content_available
           )
    FROM events.query_performance_plan AS p
    JOIN events.query_performance_query AS q
      USING (collection_run_id, database_id, query_fingerprint)
    JOIN events.query_performance_observation AS o
      ON o.collection_run_id = p.collection_run_id
     AND o.database_id = p.database_id
     AND o.query_fingerprint = p.query_fingerprint
     AND o.plan_fingerprint = p.plan_fingerprint
    JOIN events.query_performance_run AS r
      ON r.collection_run_id = o.collection_run_id
    WHERE q.instance_id = p_instance_id
      AND p.database_id = p_database_id
      AND p.query_fingerprint = p_query_fingerprint
      AND p.plan_fingerprint = p_plan_fingerprint
      AND control.query_performance_target_claim(p_instance_id)
    ORDER BY o.observed_at DESC, o.interval_end DESC,
             o.collection_run_id DESC, o.observation_key DESC
    LIMIT 1;
$fn$;
REVOKE ALL ON FUNCTION control.get_query_plan_metadata(uuid,integer,bytea,bytea)
    FROM PUBLIC, sqlobserver_collector;
GRANT EXECUTE ON FUNCTION control.get_query_plan_metadata(uuid,integer,bytea,bytea)
    TO sqlobserver_server;
