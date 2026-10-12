-- Operator-maintained fleet grouping is independent of collector configuration revisions.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE control.observation_target_grouping
(
    instance_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
    environment_name text,
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_target_grouping_environment CHECK
        (environment_name IS NULL OR
         (octet_length(environment_name) BETWEEN 1 AND 64 AND
          environment_name ~ '^[a-z0-9][a-z0-9._:-]*$')),
    CONSTRAINT ck_target_grouping_tag_count CHECK (cardinality(tags) <= 16)
);

COMMENT ON TABLE control.observation_target_grouping IS
    'Operator-supplied fleet labels with their own optimistic revision; changes do not fence collection.';
REVOKE ALL ON TABLE control.observation_target_grouping FROM PUBLIC;

CREATE VIEW reporting.observation_target_grouping
WITH (security_barrier = true)
AS
SELECT target.instance_id,
       grouping.environment_name,
       COALESCE(grouping.tags, ARRAY[]::text[]) AS tags,
       COALESCE(grouping.revision, 0) AS revision
FROM control.observation_target AS target
LEFT JOIN control.observation_target_grouping AS grouping
    ON grouping.instance_id = target.instance_id;

REVOKE ALL ON TABLE reporting.observation_target_grouping FROM PUBLIC;
GRANT SELECT ON TABLE reporting.observation_target_grouping TO sqlobserver_server;

CREATE FUNCTION control.set_observation_target_grouping(
    p_instance_id uuid,
    p_expected_revision bigint,
    p_environment_name text,
    p_tags text[],
    p_actor_identifier text,
    p_correlation_id uuid)
RETURNS TABLE(result_status text, grouping_revision bigint)
LANGUAGE plpgsql
SECURITY DEFINER
VOLATILE
PARALLEL UNSAFE
SET search_path = pg_catalog
SET TimeZone = 'UTC'
AS $sqlobserver$
DECLARE
    target_lifecycle text;
    current_revision bigint;
    new_revision bigint;
BEGIN
    IF p_instance_id IS NULL
       OR p_expected_revision IS NULL OR p_expected_revision < 0
       OR (p_environment_name IS NOT NULL AND
           (octet_length(p_environment_name) NOT BETWEEN 1 AND 64 OR
            p_environment_name !~ '^[a-z0-9][a-z0-9._:-]*$'))
       OR p_tags IS NULL OR cardinality(p_tags) > 16
       OR EXISTS (SELECT 1 FROM unnest(p_tags) AS tag(value)
                  WHERE tag.value IS NULL OR octet_length(tag.value) NOT BETWEEN 1 AND 64
                     OR tag.value !~ '^[a-z0-9][a-z0-9._:-]*$')
       OR (SELECT count(*) <> count(DISTINCT tag.value)
           FROM unnest(p_tags) AS tag(value)) THEN
        RAISE EXCEPTION 'invalid observation-target grouping' USING ERRCODE = '22023';
    END IF;

    SELECT target.lifecycle_state INTO target_lifecycle
    FROM control.observation_target AS target
    WHERE target.instance_id = p_instance_id
    FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier, p_correlation_id, 'update_observation_target',
            p_instance_id, 'granted', 'failed', 'target_not_found');
        RETURN QUERY SELECT 'not_found'::text, NULL::bigint;
        RETURN;
    END IF;
    IF target_lifecycle = 'retired' THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier, p_correlation_id, 'update_observation_target',
            p_instance_id, 'granted', 'conflict', 'target_retired');
        RETURN QUERY SELECT 'already_retired'::text, NULL::bigint;
        RETURN;
    END IF;

    SELECT grouping.revision INTO current_revision
    FROM control.observation_target_grouping AS grouping
    WHERE grouping.instance_id = p_instance_id;
    current_revision := COALESCE(current_revision, 0);
    IF p_expected_revision <> current_revision THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier, p_correlation_id, 'update_observation_target',
            p_instance_id, 'granted', 'conflict', 'revision_conflict');
        RETURN QUERY SELECT 'revision_conflict'::text, current_revision;
        RETURN;
    END IF;

    new_revision := current_revision + 1;
    IF current_revision = 0 THEN
        INSERT INTO control.observation_target_grouping
            (instance_id, environment_name, tags, revision)
        VALUES (p_instance_id, p_environment_name, p_tags, new_revision);
    ELSE
        UPDATE control.observation_target_grouping AS grouping
        SET environment_name = p_environment_name, tags = p_tags,
            revision = new_revision, updated_at = clock_timestamp()
        WHERE grouping.instance_id = p_instance_id;
    END IF;
    PERFORM * FROM audit.append_administrative_activity(
        p_actor_identifier, p_correlation_id, 'update_observation_target',
        p_instance_id, 'granted', 'succeeded', 'completed');
    RETURN QUERY SELECT 'applied'::text, new_revision;
END
$sqlobserver$;

REVOKE ALL ON FUNCTION control.set_observation_target_grouping(uuid, bigint, text, text[], text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION control.set_observation_target_grouping(uuid, bigint, text, text[], text, uuid)
    TO sqlobserver_server;
