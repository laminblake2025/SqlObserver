-- Target-scoped, generation-fenced history read over committed change rows.
-- The insertion trigger increments publication_generation in the same
-- transaction so concurrent pages either agree or report stale.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE system.server_configuration_publication (
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
    target_revision bigint NOT NULL CHECK (target_revision > 0),
    publication_generation bigint NOT NULL CHECK (publication_generation > 0),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
        CHECK (isfinite(updated_at))
);
ALTER TABLE system.server_configuration_publication ENABLE ROW LEVEL SECURITY;
ALTER TABLE system.server_configuration_publication FORCE ROW LEVEL SECURITY;
CREATE POLICY server_configuration_publication_migrator
    ON system.server_configuration_publication FOR ALL TO sqlobserver_migrator
    USING (true) WITH CHECK (true);
REVOKE ALL ON system.server_configuration_publication
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION events.bump_server_configuration_publication()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
BEGIN
    IF EXISTS (
        SELECT 1 FROM inserted_rows AS inserted
        LEFT JOIN control.observation_target AS target
          ON target.instance_id=inserted.instance_id
         AND target.revision=inserted.target_revision
         AND target.lifecycle_state='active'
        WHERE target.instance_id IS NULL
    ) THEN RAISE EXCEPTION 'Configuration publication target revision changed'
        USING ERRCODE='55000'; END IF;
    INSERT INTO system.server_configuration_publication
        (instance_id,target_revision,publication_generation,updated_at)
    SELECT inserted.instance_id,inserted.target_revision,1,clock_timestamp()
    FROM inserted_rows AS inserted
    GROUP BY inserted.instance_id,inserted.target_revision
    ORDER BY inserted.instance_id
    ON CONFLICT(instance_id) DO UPDATE
    SET target_revision=excluded.target_revision,
        publication_generation=CASE
            WHEN system.server_configuration_publication.target_revision=
                 excluded.target_revision
            THEN system.server_configuration_publication.publication_generation+1
            ELSE 1 END,
        updated_at=excluded.updated_at;
    RETURN NULL;
END;
$fn$;
REVOKE ALL ON FUNCTION events.bump_server_configuration_publication()
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER server_configuration_change_publication
    AFTER INSERT ON events.server_configuration_change
    REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT
    EXECUTE FUNCTION events.bump_server_configuration_publication();

CREATE FUNCTION reporting.list_server_configuration_changes(
    p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,
    p_limit integer,p_after_detected_at timestamptz DEFAULT NULL,
    p_after_run_id uuid DEFAULT NULL,p_after_configuration_id integer DEFAULT NULL,
    p_expected_revision bigint DEFAULT NULL,p_expected_generation bigint DEFAULT NULL)
RETURNS TABLE(target_revision bigint,publication_generation bigint,has_more boolean,
    configuration_id integer,configuration_name text,source_type text,
    previous_configured_value text,configured_value text,
    previous_in_use_value text,in_use_value text,
    configured_changed boolean,in_use_changed boolean,
    previous_observed_at timestamptz,observed_at timestamptz,
    detected_at timestamptz,run_id uuid)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $fn$
DECLARE
    v_revision bigint;
    v_generation bigint;
BEGIN
    IF p_instance_id IS NULL OR p_from_utc IS NULL OR p_to_utc IS NULL
       OR NOT isfinite(p_from_utc) OR NOT isfinite(p_to_utc)
       OR p_to_utc<=p_from_utc OR p_to_utc-p_from_utc>interval '7 days'
       OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100
       OR ((p_after_detected_at IS NULL)::integer
           +(p_after_run_id IS NULL)::integer
           +(p_after_configuration_id IS NULL)::integer) NOT IN (0,3)
       OR ((p_expected_revision IS NULL)::integer
           +(p_expected_generation IS NULL)::integer) NOT IN (0,2)
       OR (p_after_detected_at IS NULL) <> (p_expected_revision IS NULL)
       OR (p_after_detected_at IS NOT NULL AND
           (NOT isfinite(p_after_detected_at) OR
            p_after_detected_at<p_from_utc OR p_after_detected_at>=p_to_utc OR
            p_after_configuration_id NOT BETWEEN 1 AND 1000000))
    THEN RAISE EXCEPTION 'Configuration history bounds or cursor are invalid'
        USING ERRCODE='22023'; END IF;
    IF NOT control.query_performance_target_claim(p_instance_id)
    THEN RAISE EXCEPTION 'Configuration history target scope is required'
        USING ERRCODE='42501'; END IF;

    SELECT target.revision INTO v_revision
    FROM control.observation_target AS target
    WHERE target.instance_id=p_instance_id AND target.lifecycle_state='active';
    IF NOT FOUND THEN RAISE EXCEPTION 'Configuration history target is inactive'
        USING ERRCODE='42501'; END IF;
    SELECT publication.publication_generation INTO v_generation
    FROM system.server_configuration_publication AS publication
    WHERE publication.instance_id=p_instance_id
      AND publication.target_revision=v_revision;
    v_generation:=coalesce(v_generation,0);
    IF p_expected_revision IS NOT NULL AND
       (p_expected_revision<>v_revision OR p_expected_generation<>v_generation)
    THEN RAISE EXCEPTION 'Configuration history cursor is stale'
        USING ERRCODE='40001'; END IF;

    RETURN QUERY
    WITH bounded AS MATERIALIZED (
        SELECT change.configuration_id,change.configuration_name,
               change.source_type,change.previous_configured_value,
               change.configured_value,change.previous_in_use_value,
               change.in_use_value,change.configured_changed,
               change.in_use_changed,change.previous_observed_at,
               change.observed_at,change.detected_at,change.run_id
        FROM events.server_configuration_change AS change
        WHERE change.instance_id=p_instance_id
          AND change.target_revision=v_revision
          AND change.detected_at>=p_from_utc
          AND change.detected_at<p_to_utc
          AND (p_after_detected_at IS NULL OR
               change.detected_at<p_after_detected_at OR
               (change.detected_at=p_after_detected_at AND
                (change.run_id,change.configuration_id)>
                    (p_after_run_id,p_after_configuration_id)))
        ORDER BY change.detected_at DESC,change.run_id,change.configuration_id
        LIMIT p_limit+1
    )
    SELECT v_revision,v_generation,
           EXISTS(SELECT 1 FROM bounded OFFSET p_limit),
           item.configuration_id,item.configuration_name,item.source_type,
           item.previous_configured_value,item.configured_value,
           item.previous_in_use_value,item.in_use_value,
           item.configured_changed,item.in_use_changed,
           item.previous_observed_at,item.observed_at,item.detected_at,item.run_id
    FROM (SELECT 1) AS anchor
    LEFT JOIN LATERAL (
        SELECT * FROM bounded
        ORDER BY bounded.detected_at DESC,bounded.run_id,bounded.configuration_id
        LIMIT p_limit
    ) AS item ON true;
END;
$fn$;

REVOKE ALL ON FUNCTION reporting.list_server_configuration_changes(
    uuid,timestamptz,timestamptz,integer,timestamptz,uuid,integer,bigint,bigint)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_server_configuration_changes(
    uuid,timestamptz,timestamptz,integer,timestamptz,uuid,integer,bigint,bigint)
    TO sqlobserver_server;
